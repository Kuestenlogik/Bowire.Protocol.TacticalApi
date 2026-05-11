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
    public async Task Discover_ReturnsBundledSituationService()
    {
        var plugin = new BowireTacticalApiProtocol();

        var services = await plugin.DiscoverAsync(
            "tacticalapi@localhost:50051", false, TestContext.Current.CancellationToken);

        var situation = Assert.Single(services);
        Assert.Equal("Situation", situation.Name);
        Assert.Equal("rheinmetall.tactical_api.v0", situation.Package);
        Assert.Equal("proto", situation.Source);
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
