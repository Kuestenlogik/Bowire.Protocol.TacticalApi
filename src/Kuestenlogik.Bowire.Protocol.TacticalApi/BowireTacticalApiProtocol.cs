// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Kuestenlogik.Bowire.Models;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi;

/// <summary>
/// Bowire protocol plugin for Rheinmetall's TacticalAPI — a gRPC interface
/// for situational-awareness systems. The plugin bundles the upstream
/// service schema (compiled in at build time from the EPL-2.0 .proto files)
/// so users get typed discovery + invoke without needing the server to expose
/// gRPC Server Reflection or having to upload the .proto themselves.
/// <para>
/// Discovery is served from the bundled descriptors; invocation walks the
/// generated <see cref="ServiceDescriptor"/> graph and dispatches over
/// <c>Grpc.Net.Client</c>. The surface is whatever
/// <see cref="TacticalApiDescriptors.ServiceFiles"/> lists — today
/// <c>Situation</c>, <c>OwnPose</c> and <c>BlueForceTracking</c>.
/// <see cref="OpenChannelAsync"/> returns <c>null</c> because every
/// TacticalAPI streaming RPC is server-streaming, not duplex — a stable
/// contract, not a pre-1.0 hedge.
/// </para>
/// </summary>
public sealed class BowireTacticalApiProtocol : IBowireProtocol
{
    /// <summary>Protocol identifier used in Bowire URLs (<c>tacticalapi@host:port</c>).</summary>
    internal const string ProtocolId = "tacticalapi";

    /// <summary>Display name for the Bowire sidebar tab.</summary>
    internal const string DisplayName = "TacticalAPI";

    // Plugin-wide defaults. Mirrored as DefaultValue on the BowirePluginSetting
    // entries below so the workbench UI and the runtime can't drift. Zero
    // means "off" for both timeouts: InvokeAsync folds a positive deadline
    // into its CallOptions, InvokeStreamAsync bounds the wait between frames
    // by the idle setting. Each knob has one path, and the paths do not cross.
    internal const int DefaultInvocationDeadlineSeconds = 0;
    internal const int DefaultStreamIdleSeconds = 0;
    internal const bool DefaultAllowSelfSignedCerts = false;
    internal const bool DefaultUseGrpcWeb = false;

    /// <inheritdoc />
    public string Name => DisplayName;

    /// <inheritdoc />
    public string Id => ProtocolId;

    /// <inheritdoc />
    public IReadOnlyList<BowirePluginSetting> Settings =>
    [
        new(GrpcTransport.InvocationDeadlineSecondsKey, "Invocation deadline",
            "Per-call gRPC deadline in seconds for unary calls. 0 means no deadline (default). "
            + "Useful when a downstream server hangs on cold connect. Subscriptions are not deadlined — "
            + "a deadline on a stream would end every subscription on the clock; use the stream idle timeout for those.",
            "number", DefaultInvocationDeadlineSeconds),
        new(GrpcTransport.StreamIdleSecondsKey, "Stream idle timeout",
            "Tear down a server-streaming subscription after this many seconds without a frame. "
            + "0 means 'never' (default — the stream runs until the server closes or the caller cancels). "
            + "The pump ends with one last frame carrying status tacticalapi:stream-idle so the reason is on record.",
            "number", DefaultStreamIdleSeconds),
        new("allowSelfSignedCerts", "Allow self-signed certs",
            "Skip the server certificate chain validation. Off by default. The shared `__bowireMtls__` marker overrides this per-call when present.",
            "bool", DefaultAllowSelfSignedCerts),
        new("useGrpcWeb", "Speak gRPC-Web",
            "Send calls as gRPC-Web over HTTP/1.1 instead of native gRPC over HTTP/2. "
            + "TacticalAPI servers commonly expose both — Rheinmetall's TacNet uses :4267 for native gRPC "
            + "and :4268 for gRPC-Web — and gRPC-Web is what survives a proxy that will not carry h2c. "
            + "Off by default.",
            "bool", DefaultUseGrpcWeb),
    ];

    /// <inheritdoc />
    public string IconSvg =>
        // Generic radar-sweep glyph — three concentric arcs with a sweep
        // line. Drawn from scratch to avoid lifting Rheinmetall's brand
        // marks; situational-awareness systems are a broad domain and
        // a radar icon is the universal shorthand for them.
        """<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="10"/><circle cx="12" cy="12" r="6"/><circle cx="12" cy="12" r="2"/><path d="M12 12 L20 6"/></svg>""";

    /// <inheritdoc />
    /// <remarks>
    /// The hint-less overload can only answer "nothing": this plugin
    /// discovers from a bundled schema rather than from the wire, so with no
    /// way to tell whether it was asked for it has nothing safe to return.
    /// Everything happens in the metadata overload below.
    /// </remarks>
    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
        => DiscoverAsync(serverUrl, showInternalServices, null, ct);

    /// <summary>
    /// Was this plugin the one the caller asked for — by the shared marker the
    /// core merges in, or by a <c>tacticalapi@</c> prefix the core left on the
    /// URL because its hint parser did not recognise it?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker is the path that matters. The gate used to test the URL for
    /// the prefix alone, and that condition can never be true for a hinted
    /// URL: the discovery endpoint runs <see cref="BowireServerUrl.Parse"/>
    /// first, which splits the hint off, pins the probe to this plugin and
    /// hands <c>DiscoverAsync</c> the bare URL. So discovery answered "no
    /// services" on every path, including the one this repo's own sample
    /// depends on, and nothing failed loudly (#61).
    /// </para>
    /// <para>
    /// The prefix check stays as the second half, and it is not a duplicate
    /// truth: <c>Parse</c> only treats <c>hint@rest</c> as a hint when
    /// <c>rest</c> carries a <c>://</c> scheme, so the documented
    /// <c>tacticalapi@host:port</c> shape — the one in this repo's README, and
    /// the one <see cref="GrpcTransport"/> normalises for invoke — reaches
    /// the plugin with the prefix still attached and no marker anywhere. The
    /// two branches cover the two ways the operator's intent survives, and
    /// neither of them fires for a URL that never named this plugin.
    /// </para>
    /// </remarks>
    private static bool WasAskedFor(
        string serverUrl, IReadOnlyDictionary<string, string>? metadata)
        => (metadata is not null
                && metadata.TryGetValue(BowireMetadataKeys.PluginHint, out var pinned)
                && string.Equals(pinned, ProtocolId, StringComparison.OrdinalIgnoreCase))
            || serverUrl.StartsWith($"{ProtocolId}@", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices,
        IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        // The gate itself stays: without one the bundled services leaked into
        // every added source URL (Petstore, custom REST APIs, arbitrary gRPC
        // endpoints), which let the operator "call" a service that isn't on
        // the wire and hit HTTP 464 when the gRPC request landed on a
        // plain-REST backend. Operator: 'when using the template
        // petstore3.swagger.io as source, i get also situation service with
        // method list, e.g. GetSituationObjects. when calling this i get Bad
        // gRPC response. HTTP status code: 464.'
        //
        // With the gate: only a URL that named this plugin —
        // `tacticalapi@http://host:port` through the core's hint parser, or
        // the bare `tacticalapi@host:port` shape it leaves alone — surfaces
        // the bundled descriptors. Plain `grpc://…` still discovers via
        // Server Reflection through the core gRPC plugin, and the hint-less
        // all-plugins fan-out gets nothing from here at all.
        if (string.IsNullOrWhiteSpace(serverUrl) || !WasAskedFor(serverUrl, metadata))
        {
            return Task.FromResult(new List<BowireServiceInfo>());
        }

        // The bundled .proto schema is the source of truth for this
        // scheme — the plugin's whole reason to exist is that the user
        // can ask for typed discovery against any TacticalAPI endpoint
        // without the server having to expose Server Reflection.
        var services = TacticalApiDescriptors.BuildServiceInfos();
        return Task.FromResult(services);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unary path resolves the (service, method) tuple against the bundled
    /// descriptors via <see cref="TacticalApiDescriptors.TryResolve(string, string, out ServiceDescriptor?, out MethodDescriptor?, out string?)"/>,
    /// parses the request JSON into a
    /// typed <see cref="IMessage"/> via the descriptor's parser, serializes to
    /// the protobuf wire format, dispatches over <see cref="CallInvoker"/> with
    /// a passthrough <see cref="Method{TRequest,TResponse}"/> (same pattern as
    /// Bowire's core gRPC plugin so the wire bytes also land in
    /// <see cref="InvokeResult.ResponseBinary"/> for mock-server replay), then
    /// decodes the response bytes back through the descriptor's parser and
    /// formats them with <see cref="JsonFormatter.Default"/>.
    ///
    /// <para>
    /// Server-streaming methods are routed to <see cref="InvokeStreamAsync"/>.
    /// Client-streaming and duplex-streaming aren't part of the TacticalAPI
    /// surface — the upstream commit that added <c>OwnPose</c> and
    /// <c>BlueForceTracking</c> kept to the same two shapes Situation uses,
    /// so the shape-check on this entry point rejects them with a
    /// "wrong-method-shape" hint rather than implementing dead code.
    /// </para>
    /// </remarks>
    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);

        if (!TacticalApiDescriptors.TryResolve(service, method, out var serviceDesc, out var methodDesc, out var resolveError))
            return ErrorResult(resolveError!, "not-found");

        if (methodDesc!.IsClientStreaming || methodDesc.IsServerStreaming)
            return ErrorResult(
                "Use the streaming endpoint for client/server-streaming methods.",
                "wrong-method-shape");

        var requestJson = jsonMessages.FirstOrDefault() ?? "{}";
        IMessage requestMessage;
        try
        {
            requestMessage = JsonParser.Default.Parse(requestJson, methodDesc.InputType);
        }
        // Google.Protobuf raises InvalidJsonException for syntax errors
        // and InvalidProtocolBufferException for shape mismatches; their
        // inheritance was de-coupled in a recent release so catch both.
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            return ErrorResult($"Request JSON does not match {methodDesc.InputType.FullName}: {ex.Message}", "bad-request");
        }

        var grpcMethod = new Method<byte[], byte[]>(
            type: MethodType.Unary,
            serviceName: serviceDesc!.FullName,
            name: methodDesc.Name,
            requestMarshaller: Marshallers.Create(static d => d, static d => d),
            responseMarshaller: Marshallers.Create(static d => d, static d => d));

        var requestBytes = requestMessage.ToByteArray();
        var headers = BuildMetadata(metadata);
        var callOptions = ApplyDeadline(
            new CallOptions(headers: headers, cancellationToken: ct), metadata);

        var address = GrpcTransport.ResolveGrpcAddress(serverUrl);
        GrpcChannelOptions channelOptions;
        try
        {
            channelOptions = GrpcTransport.BuildChannelOptions(metadata);
        }
        catch (TransportConfigurationException ex)
        {
            // The operator's own material did not load — say so before a
            // single byte goes to the server, in the same result shape a
            // malformed request gets.
            return ErrorResult(ex.Message, BadTransportConfigStatus);
        }
        using var channel = GrpcChannel.ForAddress(address, channelOptions);
        var invoker = channel.CreateCallInvoker();
        var sw = Stopwatch.StartNew();
        try
        {
            var responseBytes = await invoker
                .AsyncUnaryCall(grpcMethod, host: null, options: callOptions, request: requestBytes)
                .ConfigureAwait(false);
            sw.Stop();

            var responseMessage = methodDesc.OutputType.Parser.ParseFrom(responseBytes);
            var responseJson = JsonFormatter.Default.Format(responseMessage);

            // The server answered; whether it agreed is in the header (#66).
            // The body stays the response — a refusal's payload is what the
            // operator needs to read — and the refusal's own wording goes into
            // metadata, where it is visible without being hunted for.
            var (refused, refusalMessage) = ReadRefusal(responseMessage);
            var responseMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (refused)
            {
                responseMetadata[RefusalMessageKey] =
                    refusalMessage ?? "The server set success = false without an error_message.";
            }

            return new InvokeResult(
                Response: responseJson,
                DurationMs: sw.ElapsedMilliseconds,
                Status: refused ? RefusedStatus : "OK",
                Metadata: responseMetadata,
                ResponseBinary: responseBytes);
        }
        catch (RpcException ex)
        {
            sw.Stop();
            // Mirror the core gRPC plugin's trailer-namespacing so the
            // mock-server replay path can tell trailers from headers.
            var trailerMetadata = ex.Trailers.ToDictionary(
                e => "_trailer:" + e.Key,
                e => e.Value,
                StringComparer.Ordinal);
            return new InvokeResult(
                Response: ex.Status.Detail,
                DurationMs: sw.ElapsedMilliseconds,
                Status: ex.StatusCode.ToString(),
                Metadata: trailerMetadata);
        }
    }

    private static Metadata BuildMetadata(Dictionary<string, string>? source)
    {
        var headers = new Metadata();
        if (source is null) return headers;
        foreach (var (key, value) in source)
        {
            // _bowire:* keys configure the transport (TLS skip-validation,
            // client-cert path) and must never reach the wire — sending
            // them as gRPC headers would leak configuration intent to the
            // server.
            if (GrpcTransport.IsTransportKey(key))
                continue;
            headers.Add(key, value);
        }
        return headers;
    }

    /// <summary>
    /// Status reported when the server answered, and refused. Public because
    /// it is part of what a caller reads off an <see cref="InvokeResult"/>,
    /// not an implementation detail.
    /// </summary>
    public const string RefusedStatus = "tacticalapi:refused";

    /// <summary>Metadata key carrying the refusal's own wording.</summary>
    public const string RefusalMessageKey = "_tacticalapi:errorMessage";

    /// <summary>Status on the final frame when <c>streamIdleSeconds</c> ended a subscription.</summary>
    internal const string StreamIdleStatus = "tacticalapi:stream-idle";

    /// <summary>Status when the metadata bag's transport configuration (mTLS material) could not be built.</summary>
    internal const string BadTransportConfigStatus = "bad-transport-config";

    /// <summary>
    /// Read the <c>ResponseHeader</c> every TacticalAPI response embeds, and
    /// report whether the server refused the operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>service_types.proto</c> calls <c>ResponseHeader</c> the "Base class
    /// for responses": <c>success</c> plus an optional <c>error_message</c>.
    /// A refusal is an ordinary gRPC success — status OK, a parseable body —
    /// so reading only the transport outcome reports "symbol not found" as a
    /// green call with the reason buried in the payload (#66). Rheinmetall's
    /// own reference client treats the header as the outcome and throws on it,
    /// per unary reply and per streamed frame.
    /// </para>
    /// <para>
    /// Read by descriptor rather than by generated type, because this plugin
    /// dispatches over <c>Method&lt;byte[], byte[]&gt;</c> and never holds a
    /// concrete response class. A response with no <c>header</c> field, or one
    /// the server left unset, counts as success: the contract's older services
    /// and any partial implementation must not start reporting failures.
    /// </para>
    /// </remarks>
    internal static (bool Refused, string? Message) ReadRefusal(IMessage response)
    {
        if (response.Descriptor.FindFieldByName("header") is not { FieldType: FieldType.Message } headerField)
            return (false, null);
        if (headerField.Accessor.GetValue(response) is not IMessage header)
            return (false, null);
        if (header.Descriptor.FindFieldByName("success") is not { FieldType: FieldType.Bool } successField)
            return (false, null);
        if (successField.Accessor.GetValue(header) is not false)
            return (false, null);

        // error_message is a StringValue wrapper, which protobuf's reflection
        // surfaces as the unwrapped string (null when the server left it out).
        var message = header.Descriptor.FindFieldByName("error_message")?.Accessor.GetValue(header) as string;
        return (true, string.IsNullOrWhiteSpace(message) ? null : message);
    }

    private static InvokeResult ErrorResult(string message, string status) =>
        new(
            Response: message,
            DurationMs: 0,
            Status: status,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// If the caller set an <c>invocationDeadlineSeconds</c> metadata
    /// value, fold it into the <see cref="CallOptions"/>. The setting
    /// surface advertises 0 ("no deadline") as the default so the gRPC
    /// stock behaviour stays untouched unless the operator opts in.
    /// </summary>
    /// <remarks>
    /// Unary only. This used to be applied to the streaming call as well,
    /// which made a 30 s deadline set for a slow cold connect end every
    /// subscription after 30 s with <c>DeadlineExceeded</c> — a gRPC deadline
    /// bounds the whole call, and a subscription is a call that is meant to
    /// stay open. The knob for those is <c>streamIdleSeconds</c>, applied per
    /// frame in <see cref="MoveNextWithinIdleAsync"/>.
    /// </remarks>
    private static CallOptions ApplyDeadline(CallOptions opts, Dictionary<string, string>? metadata)
    {
        var seconds = GrpcTransport.ReadPositiveSeconds(metadata, GrpcTransport.InvocationDeadlineSecondsKey);
        return seconds > 0 ? opts.WithDeadline(DateTime.UtcNow.AddSeconds(seconds)) : opts;
    }

    /// <summary>
    /// Wait for the next frame, but no longer than <paramref name="idleSeconds"/>
    /// when that is positive. Throws <see cref="TimeoutException"/> when the
    /// server went quiet for that long.
    /// </summary>
    /// <remarks>
    /// The <c>streamIdleSeconds</c> setting shipped in the settings surface
    /// with a description of exactly this behaviour, and nothing read it —
    /// the same silent no-op <c>allowSelfSignedCerts</c> was until #67. A
    /// timed-out <c>MoveNext</c> is still pending when this throws; disposing
    /// the call faults it, and the continuation observes that fault so it does
    /// not surface as an unobserved task exception on the finalizer thread.
    /// </remarks>
    private static async Task<bool> MoveNextWithinIdleAsync(
        IAsyncStreamReader<byte[]> stream, int idleSeconds, CancellationToken ct)
    {
        var next = stream.MoveNext(ct);
        if (idleSeconds <= 0)
            return await next.ConfigureAwait(false);

        try
        {
            return await next.WaitAsync(TimeSpan.FromSeconds(idleSeconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = next.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Server-streaming twin of <see cref="InvokeAsync"/>: same descriptor-
    /// resolution / Method&lt;byte[],byte[]&gt; / JsonParser-and-Formatter
    /// pipeline, but the byte stream rides through
    /// <see cref="CallInvoker.AsyncServerStreamingCall{TRequest,TResponse}(Method{TRequest,TResponse}, string, CallOptions, TRequest)"/>
    /// and each emitted frame is yielded as a JSON string.
    /// <para>
    /// Client- and duplex-streaming still return the "wrong-method-shape"
    /// hint via <see cref="InvokeAsync"/> — every TacticalAPI streaming RPC
    /// is server-streaming (<c>SubscribeSituationObjectEvents</c>,
    /// <c>SubscribePositionChangedEvents</c>, <c>SubscribeBlueForceEvents</c>),
    /// and nothing on the upstream .proto roadmap suggests that's about to
    /// change. The shape-checks live on the unary entry point so the
    /// streaming entry point can stay narrow.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);

        if (!TacticalApiDescriptors.TryResolve(service, method, out var serviceDesc, out var methodDesc, out var resolveError))
        {
            yield return $$"""{ "error": {{System.Text.Json.JsonSerializer.Serialize(resolveError)}} }""";
            yield break;
        }
        if (!methodDesc!.IsServerStreaming || methodDesc.IsClientStreaming)
        {
            yield return """{ "error": "Use the unary endpoint — client / duplex streaming aren't part of the TacticalAPI surface." }""";
            yield break;
        }

        var requestJson = jsonMessages.FirstOrDefault() ?? "{}";
        IMessage requestMessage;
        string? parseError = null;
        try
        {
            requestMessage = JsonParser.Default.Parse(requestJson, methodDesc.InputType);
        }
        // See InvokeAsync — InvalidJsonException + InvalidProtocolBufferException
        // are no longer a single hierarchy in current Google.Protobuf.
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            requestMessage = methodDesc.InputType.Parser.ParseFrom([]);
            parseError = $"Request JSON does not match {methodDesc.InputType.FullName}: {ex.Message}";
        }
        if (parseError is not null)
        {
            yield return $$"""{ "error": {{System.Text.Json.JsonSerializer.Serialize(parseError)}} }""";
            yield break;
        }

        var grpcMethod = new Method<byte[], byte[]>(
            type: MethodType.ServerStreaming,
            serviceName: serviceDesc!.FullName,
            name: methodDesc.Name,
            requestMarshaller: Marshallers.Create(static d => d, static d => d),
            responseMarshaller: Marshallers.Create(static d => d, static d => d));

        var requestBytes = requestMessage.ToByteArray();
        var headers = BuildMetadata(metadata);
        // No deadline here — see ApplyDeadline. The stream's own bound is the
        // idle timeout, applied between frames rather than to the call.
        var callOptions = new CallOptions(headers: headers, cancellationToken: ct);
        var idleSeconds = GrpcTransport.ReadPositiveSeconds(metadata, GrpcTransport.StreamIdleSecondsKey);

        var address = GrpcTransport.ResolveGrpcAddress(serverUrl);
        GrpcChannelOptions? channelOptions = null;
        string? transportError = null;
        try
        {
            channelOptions = GrpcTransport.BuildChannelOptions(metadata);
        }
        catch (TransportConfigurationException ex)
        {
            transportError = ex.Message;
        }
        if (transportError is not null)
        {
            yield return $$"""{ "error": {{System.Text.Json.JsonSerializer.Serialize(transportError)}}, "status": "{{BadTransportConfigStatus}}" }""";
            yield break;
        }
        using var channel = GrpcChannel.ForAddress(address, channelOptions!);
        using var call = channel.CreateCallInvoker()
            .AsyncServerStreamingCall(grpcMethod, host: null, options: callOptions, request: requestBytes);

        while (true)
        {
            // An iterator cannot yield from inside a catch, so the timeout is
            // carried out of the try as a flag.
            bool more;
            var idled = false;
            try
            {
                more = await MoveNextWithinIdleAsync(call.ResponseStream, idleSeconds, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                more = false;
                idled = true;
            }

            if (idled)
            {
                // Same shape as the refusal frame below: the pump ends with a
                // frame that says why, not with a silent end-of-stream the
                // operator would read as the server having closed.
                var detail = $"No frame for {idleSeconds} s — closing the subscription (streamIdleSeconds).";
                yield return $$"""
                    { "error": {{System.Text.Json.JsonSerializer.Serialize(detail)}}, "status": "{{StreamIdleStatus}}" }
                    """;
                yield break;
            }
            if (!more)
                break;

            var responseBytes = call.ResponseStream.Current;
            var responseMessage = methodDesc.OutputType.Parser.ParseFrom(responseBytes);
            var frameJson = JsonFormatter.Default.Format(responseMessage);

            // Every frame carries its own header, and upstream's reference
            // client checks each one and throws (#66). So a refusal ends the
            // pump rather than flowing on as data: a subscription whose server
            // says "no" is not delivering a situation picture any more, and a
            // frame that merely looks like data would be read as one.
            var (refused, refusalMessage) = ReadRefusal(responseMessage);
            if (refused)
            {
                var detail = refusalMessage ?? "The server set success = false without an error_message.";
                yield return $$"""
                    { "error": {{System.Text.Json.JsonSerializer.Serialize(detail)}}, "status": "{{RefusedStatus}}", "frame": {{frameJson}} }
                    """;
                yield break;
            }

            yield return frameJson;
        }
    }

    /// <inheritdoc />
    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        // Every TacticalAPI streaming method — the Situation, OwnPose and
        // BlueForceTracking subscribe pumps — is server-streaming, not
        // duplex, so there is no interactive channel surface to open.
        return Task.FromResult<IBowireChannel?>(null);
    }
}
