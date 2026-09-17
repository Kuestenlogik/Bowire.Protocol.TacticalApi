// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests;

/// <summary>
/// The four settings this plugin declares arrive in the metadata bag,
/// which is where every transport knob in this plugin is read from.
/// </summary>
/// <remarks>
/// <c>docs/protocol.md</c> named <c>useGrpcWeb</c> (Settings → TacticalAPI)
/// as the way to switch the wire to gRPC-Web. It was written to the
/// workspace, it persisted across reloads, and
/// <see cref="GrpcTransport.WantsGrpcWeb"/> never saw it, because the core
/// merges plugin settings into nothing. These tests pin the merge.
/// </remarks>
public sealed class TacticalApiPluginSettingsTests
{
    private static BowireTacticalApiProtocol WithSettings(params (string Key, string Value)[] values)
    {
        var plugin = new BowireTacticalApiProtocol();
        plugin.Initialize(new FakePluginSettings(values));
        return plugin;
    }

    [Fact]
    public void Without_a_settings_store_the_bag_is_handed_back_untouched()
    {
        var plugin = new BowireTacticalApiProtocol();
        var bag = new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "1" };

        Assert.Same(bag, plugin.WithSettingDefaults(bag));
        Assert.Null(plugin.WithSettingDefaults(null));
    }

    [Fact]
    public void The_workspaces_grpc_web_choice_is_what_the_transport_reads()
    {
        var plugin = WithSettings((GrpcTransport.UseGrpcWebKey, "true"));

        var merged = plugin.WithSettingDefaults(null);

        Assert.NotNull(merged);
        Assert.True(GrpcTransport.WantsGrpcWeb(merged));
    }

    [Fact]
    public void The_workspaces_self_signed_choice_is_what_the_channel_reads()
    {
        var plugin = WithSettings((GrpcTransport.AllowSelfSignedCertsKey, "true"));

        Assert.True(GrpcTransport.AcceptsAnyServerCertificate(plugin.WithSettingDefaults(null)));
    }

    [Fact]
    public void Per_call_metadata_wins_over_the_workspace()
    {
        var plugin = WithSettings((GrpcTransport.UseGrpcWebKey, "true"));
        var bag = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GrpcTransport.UseGrpcWebKey] = "false",
        };

        // The narrower statement: this call, over native gRPC, whatever
        // the workspace prefers.
        Assert.False(GrpcTransport.WantsGrpcWeb(plugin.WithSettingDefaults(bag)));
    }

    [Fact]
    public void The_callers_other_keys_survive_the_merge()
    {
        var plugin = WithSettings((GrpcTransport.UseGrpcWebKey, "true"));
        var bag = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorization"] = "Bearer t" };

        var merged = plugin.WithSettingDefaults(bag);

        Assert.NotNull(merged);
        Assert.Equal("Bearer t", merged["authorization"]);
        Assert.True(GrpcTransport.WantsGrpcWeb(merged));
        // The caller's own dictionary is not mutated under them.
        Assert.False(bag.ContainsKey(GrpcTransport.UseGrpcWebKey));
    }

    [Fact]
    public void Both_deadline_settings_arrive_as_the_transport_spells_them()
    {
        var plugin = WithSettings(
            (GrpcTransport.InvocationDeadlineSecondsKey, "20"),
            (GrpcTransport.StreamIdleSecondsKey, "45"));

        var merged = plugin.WithSettingDefaults(null);

        Assert.Equal(20, GrpcTransport.ReadPositiveSeconds(merged, GrpcTransport.InvocationDeadlineSecondsKey));
        Assert.Equal(45, GrpcTransport.ReadPositiveSeconds(merged, GrpcTransport.StreamIdleSecondsKey));
    }

    [Fact]
    public void A_setting_left_blank_does_not_enter_the_bag()
    {
        var plugin = WithSettings((GrpcTransport.UseGrpcWebKey, "   "));

        // Nothing was configured, so nothing was copied — and the bag
        // stays the very one the caller passed.
        Assert.Null(plugin.WithSettingDefaults(null));
    }
}
