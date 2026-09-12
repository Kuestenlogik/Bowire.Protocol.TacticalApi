// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests;

public sealed class BowireTacticalApiProtocolTests
{
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
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "tacticalapi@localhost:50051", false, TestContext.Current.CancellationToken);

        // Every service-bearing .proto the fetch target downloads has to
        // reach the sidebar. Pinning the exact set is what catches an
        // upstream service that was downloaded but never registered in
        // TacticalApiDescriptors.ServiceFiles — a miss that otherwise
        // shows up as a service quietly absent from the tree.
        Assert.Equal(
            ["Situation", "OwnPose", "BlueForceTracking"],
            services.Select(s => s.Name));
        Assert.All(services, s => Assert.Equal("rheinmetall.tactical_api.v0", s.Package));
        Assert.All(services, s => Assert.Equal("proto", s.Source));
    }

    [Fact]
    public async Task Discover_SituationCarriesItsFourMethodShapes()
    {
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "tacticalapi@localhost:50051", false, TestContext.Current.CancellationToken);

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
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "tacticalapi@localhost:50051", false, TestContext.Current.CancellationToken);

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
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "tacticalapi@localhost:50051", false, TestContext.Current.CancellationToken);

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
    public async Task Discover_WithoutTheSchemePrefix_ReturnsNothing()
    {
        // The bundled schema must not leak onto unrelated source URLs —
        // a plain REST or gRPC endpoint would otherwise show TacticalAPI
        // services that aren't on the wire.
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "https://petstore3.swagger.io", false, TestContext.Current.CancellationToken);

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
