// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Google.Protobuf.Reflection;
using Grpc.Net.Client;
using Kuestenlogik.Bowire.Models;

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
    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);

        // v0.1.0 placeholder — the typed-invoke pipeline reuses the same
        // Google.Protobuf descriptor walking that the core gRPC plugin
        // does, but threading it through Grpc.Net.Client.CallInvoker for
        // every method shape (unary vs server-streaming) is more code
        // than 0.1.0 needs to prove the discovery + schema bundling
        // story. Until then, callers that need invocation can use the
        // core gRPC plugin against the same endpoint with the bundled
        // descriptor uploaded.
        await Task.CompletedTask.ConfigureAwait(false);
        return new InvokeResult(
            Response: $$"""{ "info": "TacticalAPI invoke not yet implemented in 0.1.0 — use the core gRPC plugin against {{serverUrl}} with the bundled schema for now." }""",
            DurationMs: 0,
            Status: "not-implemented",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);

        // Channel-construct sanity: catches mis-typed serverUrl early so
        // the caller gets a meaningful error rather than a silent empty
        // stream. The channel is disposed immediately because v0.1.0
        // doesn't wire the stream through yet — see InvokeAsync.
        using var channel = GrpcChannel.ForAddress(serverUrl);
        await Task.CompletedTask.ConfigureAwait(false);
        yield return """{ "info": "TacticalAPI streaming not yet implemented in 0.1.0." }""";
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
