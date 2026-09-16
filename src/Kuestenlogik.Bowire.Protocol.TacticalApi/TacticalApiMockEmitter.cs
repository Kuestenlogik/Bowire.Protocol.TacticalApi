// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi;

/// <summary>
/// Plugs into Bowire's mock server via the
/// <see cref="IBowireMockEmitter"/> extension point. When a recording
/// contains steps tagged <c>protocol: "tacticalapi"</c>, the emitter
/// opens a single <see cref="GrpcChannel"/> against the server the
/// recording was captured from and replays each step's request at the
/// captured cadence — same pattern as the Kafka and AMQP emitters,
/// adapted to gRPC's request/response shape.
/// </summary>
/// <remarks>
/// <para>
/// What replays:
/// </para>
/// <list type="bullet">
///   <item>Unary steps (the typed <c>Get</c> / <c>AddOrUpdate</c> /
///   <c>Delete</c> / <c>UpdatePosition</c> RPCs across <c>Situation</c>,
///   <c>OwnPose</c> and <c>BlueForceTracking</c>) call the matching method
///   through the bundled descriptors. The response is logged, not
///   redirected anywhere — the point is "produce realistic traffic against
///   the target". Its <c>ResponseHeader</c> is read, though: a replay the
///   server refused is logged as a warning with the server's reason, not
///   as a call that took N ms (#66 applies to replay as much as to
///   invoke).</item>
///   <item>Server-streaming steps (the three <c>Subscribe…</c> pumps) open
///   the stream and consume frames silently, stopping at the first frame
///   whose header refuses. Useful when the replay timeline wants to hold
///   a subscription open between unary calls.</item>
/// </list>
/// <para>
/// The recording's metadata is honoured the way the live plugin honours
/// it: the first step's bag configures the channel (gRPC-Web, mTLS,
/// self-signed), and each step's own bag travels as request headers minus
/// the transport keys. A recording captured over gRPC-Web replays over
/// gRPC-Web; without that, it dialled the web port with native gRPC and
/// every step failed before reaching a service.
/// </para>
/// <para>
/// Out of scope (matches the live plugin's contract): client-streaming
/// and duplex aren't part of the TacticalAPI .proto surface, so steps
/// tagged that way log a warning and are skipped. Which services exist
/// is <see cref="TacticalApiDescriptors.ServiceFiles"/>' business, not
/// this emitter's — a new upstream service replays without a change
/// here.
/// </para>
/// </remarks>
public sealed class TacticalApiMockEmitter : IBowireMockEmitter
{
    private GrpcChannel? _channel;
    private CancellationTokenSource? _cts;
    private Task? _schedulerTask;
    private bool _disposed;

    /// <inheritdoc />
    public string Id => BowireTacticalApiProtocol.ProtocolId;

    /// <inheritdoc />
    public bool CanEmit(BowireRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return recording.Steps.Any(IsTacticalApiStep);
    }

    /// <inheritdoc />
    public Task StartAsync(
        BowireRecording recording,
        MockEmitterOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var steps = recording.Steps.Where(IsTacticalApiStep).ToList();
        if (steps.Count == 0) return Task.CompletedTask;

        var address = GrpcTransport.ResolveGrpcAddress(steps[0].ServerUrl ?? "https://localhost:5118");
        _channel = GrpcChannel.ForAddress(address, GrpcTransport.BuildChannelOptions(ReadOnly(steps[0].Metadata)));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _schedulerTask = Task.Run(() => RunAsync(steps, options, logger, _cts.Token), _cts.Token);

        logger.LogInformation(
            "tacticalapi-emitter replaying → {Address} (steps={Count})",
            address, steps.Count);
        return Task.CompletedTask;
    }

    private static bool IsTacticalApiStep(BowireRecordingStep s) =>
        string.Equals(s.Protocol, BowireTacticalApiProtocol.ProtocolId, StringComparison.OrdinalIgnoreCase);

    private async Task RunAsync(
        List<BowireRecordingStep> steps,
        MockEmitterOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        if (_channel is null) return;

        var baseCapturedAt = steps[0].CapturedAt;
        var speed = options.ReplaySpeed;

        do
        {
            var scheduleStartTicks = Environment.TickCount64;

            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();

                if (speed > 0)
                {
                    var targetOffsetMs = (long)((step.CapturedAt - baseCapturedAt) / speed);
                    var elapsed = Environment.TickCount64 - scheduleStartTicks;
                    var waitMs = targetOffsetMs - elapsed;
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                await EmitAsync(step, logger, ct);
            }
        }
        while (options.Loop && !ct.IsCancellationRequested);
    }

    private async Task EmitAsync(BowireRecordingStep step, ILogger logger, CancellationToken ct)
    {
        if (_channel is null) return;

        // Resolve the (service, method) tuple against the bundled
        // descriptor. Plugin-internal helper keeps the resolution logic
        // in lockstep with InvokeAsync's shape-checks.
        if (!TacticalApiDescriptors.TryResolve(step.Service ?? "", step.Method ?? "", out var serviceDesc, out var methodDesc))
        {
            logger.LogWarning(
                "tacticalapi-emit skipping step '{StepId}' — service '{Service}' / method '{Method}' not in bundled schema.",
                step.Id, step.Service, step.Method);
            return;
        }

        if (methodDesc!.IsClientStreaming)
        {
            // No upstream RPC is client-streaming — see the contract
            // doc on BowireTacticalApiProtocol.
            logger.LogWarning(
                "tacticalapi-emit skipping step '{StepId}' — client-streaming RPCs aren't part of the TacticalAPI surface.",
                step.Id);
            return;
        }

        var requestBytes = DecodeRequestBytes(step, methodDesc, logger);
        if (requestBytes is null) return;

        var grpcMethod = new Method<byte[], byte[]>(
            type: methodDesc.IsServerStreaming ? MethodType.ServerStreaming : MethodType.Unary,
            serviceName: serviceDesc!.FullName,
            name: methodDesc.Name,
            requestMarshaller: Marshallers.Create(static d => d, static d => d),
            responseMarshaller: Marshallers.Create(static d => d, static d => d));

        var callOptions = new CallOptions(headers: BuildHeaders(step.Metadata), cancellationToken: ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var invoker = _channel.CreateCallInvoker();
            if (methodDesc.IsServerStreaming)
            {
                using var call = invoker.AsyncServerStreamingCall(
                    grpcMethod, host: null, options: callOptions, request: requestBytes);
                // Drain a few frames so the server sees a real consumer;
                // we don't redirect them anywhere — the point is realistic
                // traffic, not a tee. Each frame carries a header, and a
                // refusing one ends the drain the way it ends the live pump.
                var frames = 0;
                while (frames < 50 && await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                {
                    frames++;
                    var frame = methodDesc.OutputType.Parser.ParseFrom(call.ResponseStream.Current);
                    var (refusedFrame, frameReason) = BowireTacticalApiProtocol.ReadRefusal(frame);
                    if (refusedFrame)
                    {
                        sw.Stop();
                        Interlocked.Increment(ref _refusedSteps);
                        logger.LogWarning(
                            "tacticalapi-emit(step={StepId}, {Service}/{Method}) refused by the server on frame {Frame}: {Reason}",
                            step.Id, serviceDesc.FullName, methodDesc.Name, frames,
                            frameReason ?? "success = false without an error_message");
                        return;
                    }
                }
                sw.Stop();
                logger.LogInformation(
                    "tacticalapi-emit(step={StepId}, {Service}/{Method}, frames={Frames}, durationMs={Ms})",
                    step.Id, serviceDesc.FullName, methodDesc.Name, frames, sw.ElapsedMilliseconds);
            }
            else
            {
                var responseBytes = await invoker.AsyncUnaryCall(grpcMethod, host: null,
                    options: callOptions, request: requestBytes).ConfigureAwait(false);
                sw.Stop();

                // gRPC said OK; whether the server agreed is in the header.
                var response = methodDesc.OutputType.Parser.ParseFrom(responseBytes);
                var (refused, reason) = BowireTacticalApiProtocol.ReadRefusal(response);
                if (refused)
                {
                    Interlocked.Increment(ref _refusedSteps);
                    logger.LogWarning(
                        "tacticalapi-emit(step={StepId}, {Service}/{Method}, durationMs={Ms}) refused by the server: {Reason}",
                        step.Id, serviceDesc.FullName, methodDesc.Name, sw.ElapsedMilliseconds,
                        reason ?? "success = false without an error_message");
                    return;
                }

                logger.LogInformation(
                    "tacticalapi-emit(step={StepId}, {Service}/{Method}, durationMs={Ms})",
                    step.Id, serviceDesc.FullName, methodDesc.Name, sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "tacticalapi-emit failed for step '{StepId}' on {Service}/{Method}; scheduler continues.",
                step.Id, serviceDesc.FullName, methodDesc.Name);
        }
    }

    /// <summary>
    /// Completes when the replay has run its course — every step emitted once,
    /// or, with <see cref="MockEmitterOptions.Loop"/>, when it was cancelled.
    /// Before <see cref="StartAsync"/>, or for a recording with no step of
    /// this protocol, already complete. A caller that wants to know the
    /// traffic has gone out awaits this rather than guessing a delay.
    /// </summary>
    public Task Completion => _schedulerTask ?? Task.CompletedTask;

    /// <summary>
    /// How many replayed steps the server answered with <c>success = false</c>
    /// so far. A replay is traffic, not a test, so a refusal does not stop
    /// the scheduler — but a recording that is refused on every loop is
    /// worth knowing about, and the log alone is easy to miss.
    /// </summary>
    public int RefusedSteps => Volatile.Read(ref _refusedSteps);

    private int _refusedSteps;

    private static Dictionary<string, string>? ReadOnly(IDictionary<string, string>? metadata)
        => metadata is null ? null : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

    /// <summary>
    /// The step's recorded metadata as request headers, minus what
    /// configures the transport — the same filter the live invoke applies,
    /// so a recording never ships a client-cert path to the server.
    /// </summary>
    private static Metadata BuildHeaders(IDictionary<string, string>? metadata)
    {
        var headers = new Metadata();
        if (metadata is null) return headers;
        foreach (var (key, value) in metadata)
        {
            if (GrpcTransport.IsTransportKey(key)) continue;
            headers.Add(key, value);
        }
        return headers;
    }

    /// <summary>
    /// Decode the recorded request to the protobuf wire bytes the gRPC
    /// channel needs. Precedence is shared with the live invoke path:
    /// <see cref="BowireRecordingStep.ResponseBinary"/> (base64) wins
    /// so the original wire bytes round-trip; <see cref="BowireRecordingStep.Body"/>
    /// (JSON) is parsed through the descriptor as the fallback for
    /// recordings captured before binary capture landed.
    /// </summary>
    internal static byte[]? DecodeRequestBytes(
        BowireRecordingStep step,
        Google.Protobuf.Reflection.MethodDescriptor methodDesc,
        ILogger logger)
    {
        if (!string.IsNullOrEmpty(step.RequestBinary))
        {
            try { return Convert.FromBase64String(step.RequestBinary); }
            catch (FormatException ex)
            {
                logger.LogWarning(ex,
                    "tacticalapi-emit step '{StepId}' RequestBinary is not valid base64 — falling back to Body.",
                    step.Id);
            }
        }
        if (!string.IsNullOrEmpty(step.Body))
        {
            try
            {
                var message = JsonParser.Default.Parse(step.Body, methodDesc.InputType);
                return message.ToByteArray();
            }
            catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
            {
                logger.LogWarning(ex,
                    "tacticalapi-emit step '{StepId}' Body does not match {Type} — skipping.",
                    step.Id, methodDesc.InputType.FullName);
            }
        }
        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cts is not null)
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        if (_schedulerTask is not null)
        {
            try { await _schedulerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* swallow — disposal must not throw */ }
        }
        _channel?.Dispose();
        _cts?.Dispose();
    }
}
