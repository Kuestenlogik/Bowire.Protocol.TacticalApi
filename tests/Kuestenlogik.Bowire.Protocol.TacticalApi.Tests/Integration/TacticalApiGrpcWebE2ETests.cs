// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests.Integration;

/// <summary>
/// The plugin against a port that speaks gRPC-Web over HTTP/1.1 and
/// nothing else — TacNet's <c>:4268</c> in miniature (#67).
/// </summary>
/// <remarks>
/// The unit tests on <c>GrpcTransport</c> prove the options object
/// changes shape; these prove the bytes get through. The negative case is
/// here on purpose: without it, a fixture that happened to serve native
/// gRPC on the same socket would make the positive cases pass without
/// the transport switch doing anything.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TacticalApiGrpcWebE2ETests : IClassFixture<InProcessTacticalApiServerFixture>
{
    private readonly InProcessTacticalApiServerFixture _server;

    public TacticalApiGrpcWebE2ETests(InProcessTacticalApiServerFixture server)
    {
        _server = server;
    }

    /// <summary>The plugin setting, as the workbench delivers it.</summary>
    private static Dictionary<string, string> ViaSetting() => new(StringComparer.Ordinal)
    {
        [GrpcTransport.UseGrpcWebKey] = "true",
    };

    /// <summary>The core's shared marker, as a <c>grpcweb@</c> hint leaves it.</summary>
    private static Dictionary<string, string> ViaSharedMarker() => new(StringComparer.Ordinal)
    {
        [GrpcTransport.GrpcTransportMarkerKey] = "web",
    };

    [Fact]
    public async Task Invoke_over_grpc_web_via_the_plugin_setting()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var result = await plugin.InvokeAsync(
            GrpcWebUrl(), "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: ViaSetting(), ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.Contains("test-uuid-1", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_over_grpc_web_via_the_shared_marker()
    {
        // The second seam: a caller that knows the core's vocabulary is not
        // made to learn this plugin's setting as well.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var result = await plugin.InvokeAsync(
            GrpcWebUrl(), "BlueForceTracking", "GetBlueForces",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: ViaSharedMarker(), ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.Contains(
            IntegrationBlueForceTrackingService.SeedCallsign,
            result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_over_grpc_web_yields_frames()
    {
        // Server-streaming is the shape gRPC-Web is often assumed not to
        // carry. It does — what it cannot carry is client- and duplex-
        // streaming, and the TacticalAPI surface has neither.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            GrpcWebUrl(), "OwnPose", "SubscribePositionChangedEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: ViaSetting(), ct: ct).ConfigureAwait(false))
        {
            frames.Add(frame);
            if (frames.Count >= 2) break;
        }

        Assert.Equal(2, frames.Count);
        Assert.Contains("pose-frame-0", frames[0], StringComparison.Ordinal);
        Assert.Contains("pose-frame-1", frames[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_refusal_is_still_read_over_grpc_web()
    {
        // The header check (#66) sits above the transport; switching the
        // wire must not switch it off.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var result = await plugin.InvokeAsync(
            GrpcWebUrl(), "OwnPose", "UpdatePosition",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: ViaSetting(), ct: ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, result.Status);
    }

    [Fact]
    public async Task Discover_with_grpc_web_selected_still_answers()
    {
        // Discovery is served from the bundled schema and never touches
        // the wire, so the transport choice cannot break it — pinned so a
        // future "probe the server first" change has to keep it that way.
        var plugin = new BowireTacticalApiProtocol();
        var metadata = ViaSetting();
        metadata[BowireMetadataKeys.PluginHint] = "tacticalapi";

        var services = await plugin.DiscoverAsync(
            GrpcWebUrl(), false, metadata, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Situation", "OwnPose", "BlueForceTracking"],
            services.Select(s => s.Name));
    }

    [Fact]
    public async Task Invoke_native_grpc_against_the_web_only_port_fails()
    {
        // The control: the default transport cannot reach this port, which is
        // what makes the passes above evidence of a switch rather than of a
        // permissive fixture. It is also the error an operator sees when
        // pointing an unconfigured plugin at :4268.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var result = await plugin.InvokeAsync(
            GrpcWebUrl(), "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct);

        Assert.NotEqual("OK", result.Status);
        Assert.NotEqual(BowireTacticalApiProtocol.RefusedStatus, result.Status);
    }

    /// <summary>The HTTP/1.1 listener, in the shape the workbench feeds the plugin.</summary>
    private string GrpcWebUrl() =>
        _server.GrpcWebServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal);
}
