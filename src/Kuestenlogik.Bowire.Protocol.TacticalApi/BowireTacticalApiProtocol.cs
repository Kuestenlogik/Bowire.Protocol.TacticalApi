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
/// MVP scope (0.1.0): discovery is served from the bundled descriptors;
/// invocation walks the generated <see cref="ServiceDescriptor"/> graph and
/// dispatches over <c>Grpc.Net.Client</c>. <see cref="OpenChannelAsync"/>
/// returns <c>null</c> because the only streaming method on TacticalAPI is
/// server-streaming, not duplex.
/// </para>
/// </summary>
public sealed class BowireTacticalApiProtocol : IBowireProtocol
{
    /// <summary>Protocol identifier used in Bowire URLs (<c>tacticalapi@host:port</c>).</summary>
    public const string ProtocolId = "tacticalapi";

    /// <summary>Display name for the Bowire sidebar tab.</summary>
    public const string DisplayName = "TacticalAPI";

    /// <inheritdoc />
    public string Name => DisplayName;

    /// <inheritdoc />
    public string Id => ProtocolId;

    /// <inheritdoc />
    public string IconSvg =>
        // Generic radar-sweep glyph — three concentric arcs with a sweep
        // line. Drawn from scratch to avoid lifting Rheinmetall's brand
        // marks; situational-awareness systems are a broad domain and
        // a radar icon is the universal shorthand for them.
        """<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="10"/><circle cx="12" cy="12" r="6"/><circle cx="12" cy="12" r="2"/><path d="M12 12 L20 6"/></svg>""";

    /// <inheritdoc />
    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        // The bundled .proto schema is the source of truth — the plugin's
        // whole reason to exist is that the user can ask for typed
        // discovery against any TacticalAPI endpoint without the server
        // having to expose Server Reflection.
        var services = TacticalApiDescriptors.BuildServiceInfos();
        return Task.FromResult(services);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unary path uses the bundled <see cref="SituationServiceReflection.Descriptor"/>
    /// to resolve the (service, method) tuple, parses the request JSON into a
    /// typed <see cref="IMessage"/> via the descriptor's parser, serializes to
    /// the protobuf wire format, dispatches over <see cref="CallInvoker"/> with
    /// a passthrough <see cref="Method{TRequest,TResponse}"/> (same pattern as
    /// Bowire's core gRPC plugin so the wire bytes also land in
    /// <see cref="InvokeResult.ResponseBinary"/> for mock-server replay), then
    /// decodes the response bytes back through the descriptor's parser and
    /// formats them with <see cref="JsonFormatter.Default"/>.
    ///
    /// <para>
    /// Client / server / duplex streaming still falls through to <see cref="InvokeStreamAsync"/>
    /// (0.2.0 work) — for those a streaming-call <c>CallInvoker</c> is needed
    /// and the result shape changes from "one response" to "stream of responses".
    /// </para>
    /// </remarks>
    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);

        if (!TryResolveMethod(service, method, out var serviceDesc, out var methodDesc, out var resolveError))
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
        catch (InvalidProtocolBufferException ex)
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
        var callOptions = new CallOptions(headers: headers, cancellationToken: ct);

        using var channel = GrpcChannel.ForAddress(serverUrl);
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

            return new InvokeResult(
                Response: responseJson,
                DurationMs: sw.ElapsedMilliseconds,
                Status: "OK",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal),
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

    private static bool TryResolveMethod(
        string service, string method,
        out ServiceDescriptor? serviceDescriptor,
        out MethodDescriptor? methodDescriptor,
        out string? error)
    {
        var file = SituationServiceReflection.Descriptor;
        serviceDescriptor = file.Services.FirstOrDefault(s =>
            string.Equals(s.FullName, service, StringComparison.Ordinal) ||
            string.Equals(s.Name, service, StringComparison.Ordinal));
        if (serviceDescriptor is null)
        {
            methodDescriptor = null;
            error = $"Service '{service}' is not part of the bundled TacticalAPI descriptors. " +
                    $"Known: {string.Join(", ", file.Services.Select(s => s.FullName))}.";
            return false;
        }

        methodDescriptor = serviceDescriptor.Methods.FirstOrDefault(m =>
            string.Equals(m.Name, method, StringComparison.Ordinal));
        if (methodDescriptor is null)
        {
            error = $"Method '{method}' not declared on '{serviceDescriptor.FullName}'. " +
                    $"Known: {string.Join(", ", serviceDescriptor.Methods.Select(m => m.Name))}.";
            return false;
        }

        error = null;
        return true;
    }

    private static Metadata BuildMetadata(Dictionary<string, string>? source)
    {
        var headers = new Metadata();
        if (source is null) return headers;
        foreach (var (key, value) in source)
            headers.Add(key, value);
        return headers;
    }

    private static InvokeResult ErrorResult(string message, string status) =>
        new(
            Response: message,
            DurationMs: 0,
            Status: status,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    /// <inheritdoc />
    /// <remarks>
    /// Server-streaming twin of <see cref="InvokeAsync"/>: same descriptor-
    /// resolution / Method&lt;byte[],byte[]&gt; / JsonParser-and-Formatter
    /// pipeline, but the byte stream rides through
    /// <see cref="CallInvoker.AsyncServerStreamingCall{TRequest,TResponse}(Method{TRequest,TResponse}, string, CallOptions, TRequest)"/>
    /// and each emitted frame is yielded as a JSON string.
    /// <para>
    /// Client- and duplex-streaming still return the "wrong-method-shape"
    /// hint via <see cref="InvokeAsync"/> — TacticalAPI's only streaming
    /// RPC today is server-streaming (SubscribeSituationObjectEvents), and
    /// nothing on the upstream .proto roadmap suggests that's about to
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

        if (!TryResolveMethod(service, method, out var serviceDesc, out var methodDesc, out var resolveError))
        {
            yield return $$"""{ "error": {{System.Text.Json.JsonSerializer.Serialize(resolveError)}} }""";
            yield break;
        }
        if (!methodDesc!.IsServerStreaming || methodDesc.IsClientStreaming)
        {
            yield return """{ "error": "Use the unary endpoint — client / duplex streaming aren't part of the TacticalAPI surface in 0.x." }""";
            yield break;
        }

        var requestJson = jsonMessages.FirstOrDefault() ?? "{}";
        IMessage requestMessage;
        string? parseError = null;
        try
        {
            requestMessage = JsonParser.Default.Parse(requestJson, methodDesc.InputType);
        }
        catch (InvalidProtocolBufferException ex)
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
        var callOptions = new CallOptions(headers: headers, cancellationToken: ct);

        using var channel = GrpcChannel.ForAddress(serverUrl);
        using var call = channel.CreateCallInvoker()
            .AsyncServerStreamingCall(grpcMethod, host: null, options: callOptions, request: requestBytes);

        while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
        {
            var responseBytes = call.ResponseStream.Current;
            var responseMessage = methodDesc.OutputType.Parser.ParseFrom(responseBytes);
            yield return JsonFormatter.Default.Format(responseMessage);
        }
    }

    /// <inheritdoc />
    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        // TacticalAPI's only streaming method (SubscribeSituationObjectEvents)
        // is server-streaming, not duplex — no interactive channel surface.
        return Task.FromResult<IBowireChannel?>(null);
    }
}
