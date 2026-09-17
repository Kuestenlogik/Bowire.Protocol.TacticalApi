// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Tests;

/// <summary>
/// The sample's services, driven through the plugin the way an operator
/// drives them from the workbench. The request bodies are the ones in the
/// sample README — so a README edit that breaks a body breaks a test —
/// and the refusals are the ones it promises to show.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SampleServicesE2ETests : IClassFixture<InProcessSampleServerFixture>
{
    private readonly InProcessSampleServerFixture _server;
    private readonly BowireTacticalApiProtocol _plugin = new();

    public SampleServicesE2ETests(InProcessSampleServerFixture server)
    {
        _server = server;
    }

    // ---- Situation ----------------------------------------------------------

    [Fact]
    public async Task Situation_README_body_places_a_symbol_and_a_sparse_update_touches_only_the_name()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Id = "6f1c2d3e-4a5b-4c6d-8e9f-0a1b2c3d4e5f";

        var placed = await Invoke("Situation", "AddOrUpdateSituationObjects", ReadmeSymbol(Id), ct);
        Assert.Equal("OK", placed.Status);

        var after = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        Assert.Contains("Fähre Holnis", after.Response, StringComparison.Ordinal);
        Assert.Contains("54.86", after.Response, StringComparison.Ordinal);
        Assert.Contains("SNSPXMP---*****", after.Response, StringComparison.Ordinal);

        // Only the name. The location and the symbol identifier were not
        // sent, so they are not touched — "omit the entire property".
        var renamed = await Invoke("Situation", "AddOrUpdateSituationObjects", $$"""
            {
              "situationObjects": [
                {
                  "symbol": {
                    "identity": { "uuidIdentity": "{{Id}}" },
                    "reporter": { "stringIdentity": "TacticalAPI" },
                    "reportingTime": "2026-09-13T10:16:00Z",
                    "name": { "content": "Fähre Holnis II" }
                  }
                }
              ]
            }
            """, ct);
        Assert.Equal("OK", renamed.Status);

        var read = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        Assert.Contains("Fähre Holnis II", read.Response, StringComparison.Ordinal);
        Assert.Contains("54.86", read.Response, StringComparison.Ordinal);
        Assert.Contains("SNSPXMP---*****", read.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Situation_write_without_a_reporter_is_refused_with_the_reason()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Invoke("Situation", "AddOrUpdateSituationObjects", """
            {
              "situationObjects": [
                {
                  "symbol": {
                    "identity": { "uuidIdentity": "0b1c2d3e-0000-4c6d-8e9f-0a1b2c3d4e5f" },
                    "reportingTime": "2026-09-13T10:15:00Z",
                    "name": { "content": "Ohne Absender" }
                  }
                }
              ]
            }
            """, ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, result.Status);
        Assert.Contains("reporter is required", result.Metadata[BowireTacticalApiProtocol.RefusalMessageKey], StringComparison.Ordinal);

        var read = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        Assert.DoesNotContain("Ohne Absender", read.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Situation_delete_retires_the_symbol_and_a_second_delete_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Id = "1b1c2d3e-1111-4c6d-8e9f-0a1b2c3d4e5f";
        Assert.Equal("OK", (await Invoke("Situation", "AddOrUpdateSituationObjects", ReadmeSymbol(Id), ct)).Status);

        var delete = $$"""
            {
              "situationObjects": [
                {
                  "identity": { "uuidIdentity": "{{Id}}" },
                  "reporter": { "stringIdentity": "TacticalAPI" },
                  "reportingTime": "2026-09-13T10:20:00Z"
                }
              ]
            }
            """;
        var first = await Invoke("Situation", "DeleteSituationObjects", delete, ct);
        Assert.Equal("OK", first.Status);

        var read = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        Assert.DoesNotContain(Id, read.Response, StringComparison.Ordinal);

        var second = await Invoke("Situation", "DeleteSituationObjects", delete, ct);
        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, second.Status);
        Assert.Contains("No situation object with identity", second.Metadata[BowireTacticalApiProtocol.RefusalMessageKey], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Situation_expired_symbol_is_retired_by_the_tick_and_comes_through_the_stream_once_deleted()
    {
        // "Expired symbols are automatically marked as deleted." The tick
        // does it; a subscriber sees the symbol once more with is_deleted
        // and the sweep as reporter, and GetSituationObjects no longer has it.
        var ct = TestContext.Current.CancellationToken;
        const string Id = "2b1c2d3e-2222-4c6d-8e9f-0a1b2c3d4e5f";

        var placed = await Invoke("Situation", "AddOrUpdateSituationObjects", $$"""
            {
              "situationObjects": [
                {
                  "symbol": {
                    "identity": { "uuidIdentity": "{{Id}}" },
                    "reporter": { "stringIdentity": "TacticalAPI" },
                    "reportingTime": "2026-09-13T10:15:00Z",
                    "name": { "content": "Kurzlebig" },
                    "expiryTime": { "content": "2026-09-13T10:30:00Z" }
                  }
                }
              ]
            }
            """, ct);
        Assert.Equal("OK", placed.Status);

        await using var frames = _plugin.InvokeStreamAsync(
            _server.ServerUrl, "Situation", "SubscribeSituationObjectEvents",
            jsonMessages: ["{}"], showInternalServices: false, metadata: null, ct: ct)
            .GetAsyncEnumerator(ct);

        // The initial snapshot still carries it, alive.
        Assert.True(await frames.MoveNextAsync());
        Assert.Contains(Id, frames.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("isDeleted", frames.Current, StringComparison.Ordinal);

        // A tick well past the expiry. The elapsed seconds are irrelevant
        // to the sweep; the clock is what it reads.
        _server.Situation.TickAt(elapsedSeconds: 0, nowUtc: new DateTime(2026, 9, 13, 11, 0, 0, DateTimeKind.Utc));

        Assert.True(await frames.MoveNextAsync());
        Assert.Contains(Id, frames.Current, StringComparison.Ordinal);
        Assert.Contains("isDeleted", frames.Current, StringComparison.Ordinal);
        Assert.Contains(SituationServiceImpl.ExpiryReporter.StringIdentity, frames.Current, StringComparison.Ordinal);

        var read = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        Assert.DoesNotContain(Id, read.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Situation_a_frame_is_not_changed_by_the_tick_that_follows_it()
    {
        // The stored objects are mutated in place by the tick; a frame that
        // shared them was serialised outside the lock. Two reads around a
        // tick must differ — and the first must keep the value it had.
        var ct = TestContext.Current.CancellationToken;

        var before = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        _server.Situation.TickAt(elapsedSeconds: 600, nowUtc: DateTime.UtcNow);
        var after = await Invoke("Situation", "GetSituationObjects", "{}", ct);

        Assert.NotEqual(before.Response, after.Response);
        var again = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        Assert.Equal(after.Response, again.Response);
    }

    // ---- BlueForceTracking --------------------------------------------------

    [Fact]
    public async Task BlueForce_README_body_joins_and_the_keep_alive_retires_it()
    {
        var ct = TestContext.Current.CancellationToken;

        var joined = await Invoke("BlueForceTracking", "AddOrUpdateBlueForces", ReadmeBlueForce("sample-uav-9", "Kiebitz 9"), ct);
        Assert.Equal("OK", joined.Status);

        var read = await Invoke("BlueForceTracking", "GetBlueForces", "{}", ct);
        Assert.Contains("Kiebitz 9", read.Response, StringComparison.Ordinal);
        Assert.Contains("MEASUREMENT_CODE_GPS", read.Response, StringComparison.Ordinal);

        // Thirty-one seconds later, without a re-send: gone. The seeded
        // four are re-stamped by the tick, so they stay.
        _server.BlueForces.TickAt(elapsedSeconds: 0, nowUtc: DateTime.UtcNow + OwnPlatform.StaleAfter + TimeSpan.FromSeconds(1));

        var later = await Invoke("BlueForceTracking", "GetBlueForces", "{}", ct);
        Assert.DoesNotContain("Kiebitz 9", later.Response, StringComparison.Ordinal);
        Assert.Contains("Nordstern", later.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlueForce_without_an_identity_is_refused_rather_than_skipped()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Invoke("BlueForceTracking", "AddOrUpdateBlueForces", """
            {
              "blueForcesToUpdates": [
                { "callsign": "Namenlos", "lastContactTime": "2026-09-12T10:15:00Z" }
              ]
            }
            """, ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, result.Status);
        Assert.Contains("identity is required", result.Metadata[BowireTacticalApiProtocol.RefusalMessageKey], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlueForce_without_last_contact_time_is_refused_and_nothing_in_the_request_lands()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Invoke("BlueForceTracking", "AddOrUpdateBlueForces", """
            {
              "blueForcesToUpdates": [
                { "identity": { "stringIdentity": "bf-good" }, "callsign": "Gut", "lastContactTime": "2026-09-12T10:15:00Z" },
                { "identity": { "stringIdentity": "bf-stale" }, "callsign": "Ohne Zeit" }
              ]
            }
            """, ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, result.Status);
        Assert.Contains("last_contact_time is required", result.Metadata[BowireTacticalApiProtocol.RefusalMessageKey], StringComparison.Ordinal);

        // One header per request: the good one did not land either.
        var read = await Invoke("BlueForceTracking", "GetBlueForces", "{}", ct);
        Assert.DoesNotContain("bf-good", read.Response, StringComparison.Ordinal);
    }

    // ---- OwnPose ------------------------------------------------------------

    [Fact]
    public async Task OwnPose_README_body_moves_the_pose_and_a_fix_without_a_source_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var moved = await Invoke("OwnPose", "UpdatePosition", """
            {
              "position": {
                "sourceIdentifier": "Api",
                "pointLocation": {
                  "locationTime": "2026-09-12T10:15:00Z",
                  "geoPoint": {
                    "latitudeCoordinate": 54.52,
                    "longitudeCoordinate": 9.91,
                    "measurementCode": "MEASUREMENT_CODE_ESTIMATE"
                  }
                }
              }
            }
            """, ct);
        Assert.Equal("OK", moved.Status);

        var read = await Invoke("OwnPose", "GetPosition", "{}", ct);
        Assert.Contains("\"Api\"", read.Response, StringComparison.Ordinal);
        Assert.Contains("54.52", read.Response, StringComparison.Ordinal);

        // The mounted UAV and the own vehicle report the same fix.
        var forces = await Invoke("BlueForceTracking", "GetBlueForces", "{}", ct);
        Assert.Equal(2, CountOf(forces.Response!, "54.52"));

        var refused = await Invoke("OwnPose", "UpdatePosition", """
            { "position": { "pointLocation": { "geoPoint": { "latitudeCoordinate": 54.0, "longitudeCoordinate": 9.0 } } } }
            """, ct);
        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, refused.Status);
        Assert.Contains("source_identifier is required", refused.Metadata[BowireTacticalApiProtocol.RefusalMessageKey], StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private Task<InvokeResult> Invoke(string service, string method, string body, CancellationToken ct) =>
        _plugin.InvokeAsync(
            _server.ServerUrl, service, method,
            jsonMessages: [body], showInternalServices: false, metadata: null, ct: ct);

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The README's Situation body, with the identity swapped in.</summary>
    private static string ReadmeSymbol(string id) => $$"""
        {
          "situationObjects": [
            {
              "symbol": {
                "identity": { "uuidIdentity": "{{id}}" },
                "reporter": { "stringIdentity": "TacticalAPI" },
                "reportingTime": "2026-09-13T10:15:00Z",
                "name": { "content": "Fähre Holnis" },
                "symbolIdentifier": {
                  "content": { "symbolCatalog": "SYMBOL_CATALOG_MIL2525_C", "stringIdentifier": "SNSPXMP---*****" }
                },
                "location": {
                  "content": {
                    "point": {
                      "locationTime": "2026-09-13T10:15:00Z",
                      "geoPoint": { "latitudeCoordinate": 54.86, "longitudeCoordinate": 9.58 }
                    }
                  }
                }
              }
            }
          ]
        }
        """;

    /// <summary>The README's BlueForceTracking body.</summary>
    private static string ReadmeBlueForce(string id, string callsign) => $$"""
        {
          "blueForcesToUpdates": [
            {
              "identity": { "stringIdentity": "{{id}}" },
              "callsign": "{{callsign}}",
              "lastContactTime": "2026-09-12T10:15:00Z",
              "blueForceType": { "isUnmanned": true },
              "symbol": { "symbolCatalog": "SYMBOL_CATALOG_MIL2525_C", "stringIdentifier": "SFAPMH----****" },
              "pointLocation": {
                "locationTime": "2026-09-12T10:15:00Z",
                "geoPoint": {
                  "latitudeCoordinate": 54.33,
                  "longitudeCoordinate": 10.14,
                  "measurementCode": "MEASUREMENT_CODE_GPS"
                }
              }
            }
          ]
        }
        """;
}
