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
///   <item>Unary steps (the typed <c>Get/AddOrUpdate/Delete</c> RPCs on
///   the <c>Situation</c> service) call the matching method through the
///   bundled descriptors. The response is logged, not redirected
///   anywhere — the point is "produce realistic traffic against the
///   target".</item>
///   <item>Server-streaming steps (the <c>SubscribeSituationObjectEvents</c>
///   pump) open the stream but consume frames silently. Useful when the
///   replay timeline wants to hold a subscription open between unary
///   calls.</item>
/// </list>
/// <para>
/// Out of scope (matches the live plugin's contract): client-streaming
/// and duplex aren't part of the TacticalAPI .proto surface, so steps
/// tagged that way log a warning and are skipped.
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
        _channel = GrpcChannel.ForAddress(address);

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

        var sw = Stopwatch.StartNew();
        try
        {
            var invoker = _channel.CreateCallInvoker();
            if (methodDesc.IsServerStreaming)
            {
                using var call = invoker.AsyncServerStreamingCall(
                    grpcMethod, host: null, options: new CallOptions(cancellationToken: ct), request: requestBytes);
                // Drain a few frames so the server sees a real consumer;
                // we don't redirect them anywhere — the point is realistic
                // traffic, not a tee.
                var frames = 0;
                while (frames < 50 && await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                {
                    frames++;
                }
                sw.Stop();
                logger.LogInformation(
                    "tacticalapi-emit(step={StepId}, {Service}/{Method}, frames={Frames}, durationMs={Ms})",
                    step.Id, serviceDesc.FullName, methodDesc.Name, frames, sw.ElapsedMilliseconds);
            }
            else
            {
                await invoker.AsyncUnaryCall(grpcMethod, host: null,
                    options: new CallOptions(cancellationToken: ct), request: requestBytes).ConfigureAwait(false);
                sw.Stop();
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
