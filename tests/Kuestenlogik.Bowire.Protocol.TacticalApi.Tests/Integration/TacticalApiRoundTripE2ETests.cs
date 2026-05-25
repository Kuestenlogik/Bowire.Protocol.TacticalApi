// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests.Integration;

/// <summary>
/// End-to-end checks for the TacticalAPI plugin against a real gRPC
/// server hosted in-process. Closes the audit's biggest test gap —
/// the previous suite covered argument validation only.
/// </summary>
/// <remarks>
/// Marked <c>[Trait("Category", "Integration")]</c> rather than
/// <c>Docker</c>: the test host hands us an in-process Kestrel
/// instead of a container, so the suite runs anywhere with .NET 10
/// without a Docker daemon. Same end-to-end shape (HTTP/2 over
/// loopback, real gRPC channel, full descriptor roundtrip).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TacticalApiRoundTripE2ETests : IClassFixture<InProcessSituationServerFixture>
{
    private readonly InProcessSituationServerFixture _server;

    public TacticalApiRoundTripE2ETests(InProcessSituationServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Invoke_GetSituationObjects_round_trips_through_plugin()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        // The integration server publishes one seeded object via
        // GetSituationObjects — the plugin's invoke path resolves the
        // bundled descriptor, marshals the empty request, dispatches
        // over the real gRPC channel, decodes the response back to
        // JSON. End-to-end coverage of the unary pipeline.
        var result = await plugin.InvokeAsync(
            // 'tacticalapi@' prefix + http:// hands the URL normaliser
            // the same shape the workbench feeds in real usage.
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.Contains("test-uuid-1", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_SubscribeSituationObjectEvents_yields_frames()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "Situation", "SubscribeSituationObjectEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct).ConfigureAwait(false))
        {
            frames.Add(frame);
            if (frames.Count >= 2) break;
        }

        Assert.Equal(2, frames.Count);
        Assert.Contains("stream-frame-0", frames[0], StringComparison.Ordinal);
        Assert.Contains("stream-frame-1", frames[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_with_invocationDeadlineSeconds_metadata_applies_the_deadline()
    {
        // Pin the new Settings-property knob to a concrete behaviour:
        // a too-short deadline against a working server still completes
        // (the in-process call is faster than 10s), but the option
        // must travel through the plugin without error.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var result = await plugin.InvokeAsync(
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["invocationDeadlineSeconds"] = "10",
            },
            ct: ct);

        Assert.Equal("OK", result.Status);
    }
}
