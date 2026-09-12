// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests;

public sealed class BowireTacticalApiProtocolTests
{
    /// <summary>
    /// Discovery the way production performs it: the raw URL through
    /// <see cref="BowireServerUrl.Parse"/>, the hint it splits off into the
    /// probe, and the probe's merged metadata into the plugin.
    /// </summary>
    /// <remarks>
    /// These tests used to hand <c>"tacticalapi@localhost:50051"</c> straight
    /// to <c>DiscoverAsync</c> — a prefix production never delivers, because
    /// Parse consumes it before any plugin is reached. They asserted a
    /// contract that does not exist on the wire and passed while discovery
    /// returned nothing at all (#61). Going through the probe is what makes
    /// that impossible to repeat.
    /// </remarks>
    /// <remarks>
    /// The URL carries a scheme because <see cref="BowireServerUrl.Parse"/>
    /// only reads <c>hint@rest</c> as a hint when <c>rest</c> has one —
    /// <c>tacticalapi@localhost:50051</c> is not a hinted URL at all, which is
    /// the other half of why the old tests could not have caught this.
    /// </remarks>
    private static async Task<List<BowireServiceInfo>> DiscoverAsProductionDoes(
        string rawServerUrl, CancellationToken ct)
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new BowireTacticalApiProtocol());

        var (hint, url) = BowireServerUrl.Parse(rawServerUrl);
        var result = await BowireDiscoveryProbe.RunAsync(
            registry,
            url,
            hint,
            showInternalServices: false,
            perProbeCeiling: TimeSpan.FromSeconds(5),
            ct: ct);
        return result.Services;
    }

    [Fact]
    public void Identity_MatchesBowireConventions()
    {
        var plugin = new BowireTacticalApiProtocol();

        Assert.Equal("tacticalapi", plugin.Id);
        Assert.Equal("TacticalAPI", plugin.Name);
        Assert.False(string.IsNullOrWhiteSpace(plugin.IconSvg));
        Assert.Contains("svg", plugin.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_ReturnsEveryBundledService()
    {
        var services = await DiscoverAsProductionDoes(
            "tacticalapi@http://localhost:5192", TestContext.Current.CancellationToken);

        // Every service-bearing .proto the fetch target downloads has to
        // reach the sidebar. Pinning the exact set is what catches an
        // upstream service that was downloaded but never registered in
        // TacticalApiDescriptors.ServiceFiles — a miss that otherwise
        // shows up as a service quietly absent from the tree.
        Assert.Equal(
            ["Situation", "OwnPose", "BlueForceTracking"],
            services.Select(s => s.Name));
        Assert.All(services, s => Assert.Equal("rheinmetall.tactical_api.v0", s.Package));

        // What the workbench actually receives: the probe stamps the owning
        // plugin onto Source and the bare URL onto OriginUrl, over the
        // plugin's own "proto" marker (asserted at plugin level below). The
        // old test never saw this because it never went through the probe.
        Assert.All(services, s => Assert.Equal("tacticalapi", s.Source));
        Assert.All(services, s => Assert.Equal("http://localhost:5192", s.OriginUrl));
    }

    [Fact]
    public async Task Discover_SituationCarriesItsFourMethodShapes()
    {
        var services = await DiscoverAsProductionDoes(
            "tacticalapi@http://localhost:5192", TestContext.Current.CancellationToken);

        var situation = services.Single(s => s.Name == "Situation");
        Assert.Equal(4, situation.Methods.Count);

        var subscribe = situation.Methods.Single(m => m.Name == "SubscribeSituationObjectEvents");
        Assert.True(subscribe.ServerStreaming);
        Assert.False(subscribe.ClientStreaming);
        Assert.Equal("ServerStreaming", subscribe.MethodType);

        var getObjects = situation.Methods.Single(m => m.Name == "GetSituationObjects");
        Assert.False(getObjects.ServerStreaming);
        Assert.False(getObjects.ClientStreaming);
        Assert.Equal("Unary", getObjects.MethodType);
    }

    [Fact]
    public async Task Discover_OwnPoseCarriesItsThreeMethodShapes()
    {
        var services = await DiscoverAsProductionDoes(
            "tacticalapi@http://localhost:5192", TestContext.Current.CancellationToken);

        var ownPose = services.Single(s => s.Name == "OwnPose");
        Assert.Equal(
            ["SubscribePositionChangedEvents", "GetPosition", "UpdatePosition"],
            ownPose.Methods.Select(m => m.Name));

        Assert.Equal(
            "ServerStreaming",
            ownPose.Methods.Single(m => m.Name == "SubscribePositionChangedEvents").MethodType);
        Assert.Equal(
            "Unary",
            ownPose.Methods.Single(m => m.Name == "UpdatePosition").MethodType);

        // The field projection is what the invoke pane builds its request
        // form from — an empty field list there is an unfillable form.
        var update = ownPose.Methods.Single(m => m.Name == "UpdatePosition");
        Assert.Equal("rheinmetall.tactical_api.v0.UpdatePositionRequest", update.InputType.FullName);
        Assert.Contains(update.InputType.Fields, f => f.Name == "position");
    }

    [Fact]
    public async Task Discover_BlueForceTrackingCarriesItsThreeMethodShapes()
    {
        var services = await DiscoverAsProductionDoes(
            "tacticalapi@http://localhost:5192", TestContext.Current.CancellationToken);

        var tracking = services.Single(s => s.Name == "BlueForceTracking");
        Assert.Equal(
            ["SubscribeBlueForceEvents", "GetBlueForces", "AddOrUpdateBlueForces"],
            tracking.Methods.Select(m => m.Name));

        Assert.Equal(
            "ServerStreaming",
            tracking.Methods.Single(m => m.Name == "SubscribeBlueForceEvents").MethodType);

        // blue_forces_to_updates is repeated; the sidebar has to say so or
        // the operator gets a single-object form for a list field.
        var addOrUpdate = tracking.Methods.Single(m => m.Name == "AddOrUpdateBlueForces");
        var field = addOrUpdate.InputType.Fields.Single(f => f.Name == "blue_forces_to_updates");
        Assert.True(field.IsRepeated);
        Assert.Equal("repeated", field.Label);
    }

    [Fact]
    public async Task Discover_WithoutAPluginHint_ReturnsNothing()
    {
        // The bundled schema must not leak onto unrelated source URLs —
        // a plain REST or gRPC endpoint would otherwise show TacticalAPI
        // services that aren't on the wire. This is the reason the gate
        // exists, so it is asserted through the same path as the positive
        // case: no hint in the URL, no marker in the metadata, nothing back.
        var services = await DiscoverAsProductionDoes(
            "https://petstore3.swagger.io", TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_WithTheBarePrefixShape_StillAnswers()
    {
        // `tacticalapi@host:port` — the shape this repo's README documents and
        // GrpcTransport normalises for invoke. Parse leaves it alone (no
        // `://`), so no marker is merged and the prefix arrives intact: the
        // plugin has to recognise its own prefix or discovery stays silent for
        // every operator who typed the documented form.
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "tacticalapi@localhost:50051", false, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Situation", "OwnPose", "BlueForceTracking"],
            services.Select(s => s.Name));

        // Straight out of the plugin the schema is the source; the probe is
        // what relabels it with the plugin id on the way to the sidebar.
        Assert.All(services, s => Assert.Equal("proto", s.Source));
    }

    [Fact]
    public async Task Discover_WithoutAMarkerOrAPrefix_ReturnsNothing()
    {
        // Nothing names this plugin: no marker in the bag, no prefix on the
        // URL. This is the leak the gate exists to stop.
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "grpc://localhost:50051", false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_WhenAnotherPluginWasPinned_ReturnsNothing()
    {
        // The marker is shared across every plugin, so its value decides —
        // reading "is the key present?" instead of "is it mine?" would put
        // TacticalAPI services under `grpc@…` and `rest@…` alike.
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "localhost:50051",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BowireMetadataKeys.PluginHint] = "grpc",
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task OpenChannel_ReturnsNull_BecauseTacticalApiHasNoDuplex()
    {
        var plugin = new BowireTacticalApiProtocol();

        var channel = await plugin.OpenChannelAsync(
            "tacticalapi@localhost:50051",
            "Situation",
            "SubscribeSituationObjectEvents",
            false, null, TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }
}
