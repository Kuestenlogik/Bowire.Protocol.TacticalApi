// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Rheinmetall.TacticalApi.V0;

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
public sealed class TacticalApiRoundTripE2ETests : IClassFixture<InProcessTacticalApiServerFixture>
{
    private readonly InProcessTacticalApiServerFixture _server;

    public TacticalApiRoundTripE2ETests(InProcessTacticalApiServerFixture server)
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
    public async Task Invoke_refusal_in_the_response_header_reports_a_non_ok_status()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        // An empty source_identifier is refused by the fixture the way a
        // TacticalAPI server refuses: gRPC says OK, the header says no. Before
        // #66 this came back as Status "OK" with the reason buried in the body.
        var result = await plugin.InvokeAsync(
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "OwnPose", "UpdatePosition",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, result.Status);

        // The wording the server chose is surfaced, not hunted for …
        Assert.Equal(
            IntegrationOwnPoseService.RefusalMessage,
            result.Metadata[BowireTacticalApiProtocol.RefusalMessageKey]);

        // … and the body is still the response, because a refusal's payload is
        // what an operator reads next. Asserted on the message rather than on
        // "success": protobuf's JSON formatter omits default values, so a
        // refusal's header serialises as the error_message alone.
        Assert.Contains(
            IntegrationOwnPoseService.RefusalMessage, result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_AddOrUpdateSituationObjects_without_a_reporter_is_refused()
    {
        // The refusal the issue names: every Situation write marks `reporter`
        // as Required, and the server says no in the header (#66). The rest
        // of the envelope is present, so this is the one field's absence.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var result = await plugin.InvokeAsync(
            GrpcUrl(), "Situation", "AddOrUpdateSituationObjects",
            jsonMessages:
            [
                """
                {
                  "situationObjects": [
                    {
                      "symbol": {
                        "identity": { "uuidIdentity": "e2e-no-reporter" },
                        "reportingTime": "2026-09-13T10:00:00Z",
                        "name": { "content": "Nameless" }
                      }
                    }
                  ]
                }
                """,
            ],
            showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, result.Status);
        Assert.Equal(
            IntegrationSituationService.MissingReporterMessage,
            result.Metadata[BowireTacticalApiProtocol.RefusalMessageKey]);

        // A refused write changed nothing.
        var read = await plugin.InvokeAsync(
            GrpcUrl(), "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false, metadata: null, ct: ct);
        Assert.DoesNotContain("e2e-no-reporter", read.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_AddOrUpdateSituationObjects_with_the_full_envelope_lands()
    {
        // The envelope upstream's reference client sets on every write —
        // identity, reporter "TacticalAPI", reporting_time — is accepted,
        // and the header check leaves no refusal trace on a good write.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var write = await plugin.InvokeAsync(
            GrpcUrl(), "Situation", "AddOrUpdateSituationObjects",
            jsonMessages:
            [
                """
                {
                  "situationObjects": [
                    {
                      "symbol": {
                        "identity": { "uuidIdentity": "e2e-symbol-1" },
                        "reporter": { "stringIdentity": "TacticalAPI" },
                        "reportingTime": "2026-09-13T10:00:00Z",
                        "name": { "content": "Testfalke" }
                      }
                    }
                  ]
                }
                """,
            ],
            showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal("OK", write.Status);
        Assert.DoesNotContain(BowireTacticalApiProtocol.RefusalMessageKey, write.Metadata.Keys);

        var read = await plugin.InvokeAsync(
            GrpcUrl(), "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal("OK", read.Status);
        Assert.Contains("e2e-symbol-1", read.Response, StringComparison.Ordinal);
        // The reporter travels with the object: it is the creator identity
        // on the read side, which is the provenance the issue is about.
        Assert.Contains("TacticalAPI", read.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_refusing_frame_ends_the_pump_as_an_error()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "OwnPose", "SubscribePositionChangedEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [IntegrationOwnPoseService.RefuseHeader] = "1",
            },
            ct: ct).ConfigureAwait(false))
        {
            frames.Add(frame);
        }

        // One frame, and it says error rather than looking like a position.
        var single = Assert.Single(frames);
        Assert.Contains(BowireTacticalApiProtocol.RefusedStatus, single, StringComparison.Ordinal);
        Assert.Contains(
            IntegrationOwnPoseService.RefusalMessage,
            single, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadRefusal_treats_a_response_without_a_header_as_success()
    {
        // Not every message in the contract embeds ResponseHeader, and a
        // partial server may leave it unset. Neither case may start reporting
        // failures — the check has to be additive.
        var noHeader = new GeoPoint { LatitudeCoordinate = 54.44, LongitudeCoordinate = 9.80 };
        var headerUnset = new GetPositionResponse();

        Assert.False(BowireTacticalApiProtocol.ReadRefusal(noHeader).Refused);
        Assert.False(BowireTacticalApiProtocol.ReadRefusal(headerUnset).Refused);
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

    [Fact]
    public async Task Invoke_OwnPose_reads_the_seed_then_reflects_a_write()
    {
        // Read, write, read — in one test rather than three, because the
        // fixture's position is a single mutable cell: split across
        // [Fact]s, whichever ran second would be asserting against the
        // other one's leftovers.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;
        var url = GrpcUrl();

        var before = await plugin.InvokeAsync(
            url, "OwnPose", "GetPosition",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct);

        Assert.Equal("OK", before.Status);
        Assert.Contains(
            IntegrationOwnPoseService.SeedSource, before.Response, StringComparison.Ordinal);

        // The write RPC the Situation service has no equivalent for. The
        // request is a nested message, so this also covers JsonParser
        // descending past the top level on the way to the wire.
        var write = await plugin.InvokeAsync(
            url, "OwnPose", "UpdatePosition",
            jsonMessages:
            [
                """
                {
                  "position": {
                    "sourceIdentifier": "e2e-writer",
                    "pointLocation": {
                      "geoPoint": {
                        "latitudeCoordinate": 54.52,
                        "longitudeCoordinate": 9.91
                      }
                    }
                  }
                }
                """,
            ],
            showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal("OK", write.Status);
        Assert.Contains("true", write.Response, StringComparison.Ordinal); // header.success

        // The header check must not turn a good write into a failure, and must
        // leave no refusal trace behind (#66). Asserted here rather than in a
        // [Fact] of its own: the fixture's position is one mutable cell, and a
        // second writer would be asserting against this test's leftovers.
        Assert.DoesNotContain(BowireTacticalApiProtocol.RefusalMessageKey, write.Metadata.Keys);

        var after = await plugin.InvokeAsync(
            url, "OwnPose", "GetPosition",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct);

        Assert.Equal("OK", after.Status);
        Assert.Contains("e2e-writer", after.Response, StringComparison.Ordinal);
        Assert.Contains("54.52", after.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_SubscribePositionChangedEvents_yields_frames()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            GrpcUrl(), "OwnPose", "SubscribePositionChangedEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct).ConfigureAwait(false))
        {
            frames.Add(frame);
            if (frames.Count >= 2) break;
        }

        Assert.Equal(2, frames.Count);
        Assert.Contains("pose-frame-0", frames[0], StringComparison.Ordinal);
        Assert.Contains("pose-frame-1", frames[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_GetBlueForces_round_trips_through_plugin()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var result = await plugin.InvokeAsync(
            GrpcUrl(), "BlueForceTracking", "GetBlueForces",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.Contains(
            IntegrationBlueForceTrackingService.SeedCallsign,
            result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_AddOrUpdateBlueForces_lands_in_the_next_read()
    {
        // A repeated field of nested messages — the shape most likely to
        // be mangled between the workbench's JSON and the wire, and the
        // one the sidebar renders as a list form.
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;
        var url = GrpcUrl();

        var write = await plugin.InvokeAsync(
            url, "BlueForceTracking", "AddOrUpdateBlueForces",
            jsonMessages:
            [
                """
                {
                  "blueForcesToUpdates": [
                    {
                      "identity": { "stringIdentity": "bf-e2e-1" },
                      "callsign": "Testfalke",
                      "blueForceType": { "isUnmanned": true }
                    }
                  ]
                }
                """,
            ],
            showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal("OK", write.Status);

        var read = await plugin.InvokeAsync(
            url, "BlueForceTracking", "GetBlueForces",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct);

        Assert.Equal("OK", read.Status);
        Assert.Contains("Testfalke", read.Response, StringComparison.Ordinal);
        Assert.Contains("bf-e2e-1", read.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_SubscribeBlueForceEvents_yields_frames()
    {
        var plugin = new BowireTacticalApiProtocol();
        var ct = TestContext.Current.CancellationToken;

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            GrpcUrl(), "BlueForceTracking", "SubscribeBlueForceEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: ct).ConfigureAwait(false))
        {
            frames.Add(frame);
            if (frames.Count >= 2) break;
        }

        Assert.Equal(2, frames.Count);
        Assert.Contains("bf-frame-0", frames[0], StringComparison.Ordinal);
        Assert.Contains("bf-frame-1", frames[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixture's loopback address in the shape the workbench feeds
    /// the plugin — grpc:// so the URL normaliser is exercised too.
    /// </summary>
    private string GrpcUrl() =>
        _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal);
}
