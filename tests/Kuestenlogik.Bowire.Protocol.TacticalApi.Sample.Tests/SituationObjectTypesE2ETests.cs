// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Tests;

/// <summary>
/// Every type in the <c>UpdateSituationObject</c> oneof written through
/// the plugin and read back. One test per type would say the same thing
/// eleven times; a theory says it once with eleven rows, and each row
/// carries the one property that is specific to its type — the enum, the
/// bytes, the references, the nested objects — so the mirror the mapper
/// walks is proven on every shape of property the contract has.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SituationObjectTypesE2ETests : IClassFixture<InProcessSampleServerFixture>
{
    private readonly InProcessSampleServerFixture _server;
    private readonly BowireTacticalApiProtocol _plugin = new();

    public SituationObjectTypesE2ETests(InProcessSampleServerFixture server)
    {
        _server = server;
    }

    public static TheoryData<string, string, string, string, string> EveryType => new()
    {
        // case name, identity, type-specific property (JSON), served key to find, value to find
        { "symbol", "type-symbol", """ "quantity": { "content": 4 } """, "quantity", "4" },
        { "actionTask", "type-task", """ "actionTaskStatus": { "content": "ACTION_TASK_STATUS_TYPE_ABORTED" }, "completionRatio": { "content": 40 }, "actionTaskEffects": { "contents": [ { "stringIdentity": "effect-1" }, { "stringIdentity": "effect-2" } ] } """, "actionTaskStatus", "ACTION_TASK_STATUS_TYPE_ABORTED" },
        { "actionEvent", "type-event", """ "actionEventType": { "content": "ACTION_EVENT_TYPE_ACCIDENT" }, "threatLevel": { "content": 3 } """, "threatLevel", "3" },
        { "organizationUnit", "type-unit", """ "unitDesignation": { "content": "UNIT_DESIGNATION_SECTION" }, "organizationUnitColor": { "content": { "red": 10, "green": 20, "blue": 30 } } """, "unitDesignation", "UNIT_DESIGNATION_SECTION" },
        { "route", "type-route", """ "routeType": { "content": "ROUTE_TYPE_ADVISORY_ROUTE" }, "lineStyle": { "content": "LINE_STYLE_SOLID" }, "marchSpeed": { "content": 12 }, "location": { "content": { "routeLocation": { "wayPoints": [ { "latitudeCoordinate": 54.1, "longitudeCoordinate": 10.1 }, { "latitudeCoordinate": 54.2, "longitudeCoordinate": 10.2 } ] } } } """, "routeType", "ROUTE_TYPE_ADVISORY_ROUTE" },
        { "textDocument", "type-text", """ "content": { "content": "<p>Lagemeldung</p>" }, "plainContent": { "content": "Lagemeldung" }, "messageCategory": { "content": "MESSAGE_CATEGORY_TYPE_NORMAL" } """, "plainContent", "Lagemeldung" },
        { "pictureDocument", "type-picture", """ "pictureData": { "content": "iVBORw0KGgo=", "type": "image/png" }, "directionOfView": { "content": 270 } """, "directionOfView", "270" },
        { "voiceMessageDocument", "type-voice", """ "soundFile": { "content": "UklGRg==", "type": "audio/wav" }, "messagePrecedence": { "content": "MESSAGE_PRECEDENCE_TYPE_ROUTINE" } """, "messagePrecedence", "MESSAGE_PRECEDENCE_TYPE_ROUTINE" },
        { "natoMessageDocument", "type-nato", """ "mtfMessageData": { "content": "MSGID/OWNSITREP/..." } """, "mtfMessageData", "MSGID/OWNSITREP/..." },
        { "overlayDocument", "type-overlay", """ "tag": { "content": "phase-2" }, "overlayData": { "contents": [ { "symbol": { "identity": { "stringIdentity": "nested-1" }, "reporter": { "stringIdentity": "TacticalAPI" }, "reportingTime": "2026-09-19T10:00:00Z", "name": { "content": "Im Overlay" } } } ] } """, "overlayData", "Im Overlay" },
        { "sketchDocument", "type-sketch", """ "messageCategory": { "content": "MESSAGE_CATEGORY_TYPE_NORMAL" }, "location": { "content": { "line": { "points": [ { "latitudeCoordinate": 54.3, "longitudeCoordinate": 10.3 }, { "latitudeCoordinate": 54.4, "longitudeCoordinate": 10.4 } ] } } } """, "messageCategory", "MESSAGE_CATEGORY_TYPE_NORMAL" },
    };

    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task Every_object_type_is_written_and_read_back_with_its_own_property(
        string caseName, string id, string property, string servedKey, string servedValue)
    {
        var ct = TestContext.Current.CancellationToken;

        var write = await Invoke("Situation", "AddOrUpdateSituationObjects", $$"""
            {
              "situationObjects": [
                {
                  "{{caseName}}": {
                    "identity": { "stringIdentity": "{{id}}" },
                    "reporter": { "stringIdentity": "TacticalAPI" },
                    "reportingTime": "2026-09-19T10:00:00Z",
                    "name": { "content": "Objekt {{caseName}}" },
                    {{property}}
                  }
                }
              ]
            }
            """, ct);
        Assert.Equal("OK", write.Status);

        var read = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        Assert.Equal("OK", read.Status);

        using var doc = JsonDocument.Parse(read.Response!);
        // The seeded objects carry uuid identities; only ours carry strings.
        var served = doc.RootElement.GetProperty("situationObjects").EnumerateArray()
            .Single(o => o.TryGetProperty(caseName, out var typed)
                && typed.GetProperty("identity").TryGetProperty("stringIdentity", out var s)
                && s.GetString() == id);
        var typedObject = served.GetProperty(caseName);

        // The served object is of the update's type, carries the envelope as
        // creation metadata, the name, and the type-specific property.
        Assert.Equal("TacticalAPI", typedObject.GetProperty("creationMetaData").GetProperty("creatorIdentity").GetProperty("stringIdentity").GetString());
        Assert.Equal($"Objekt {caseName}", typedObject.GetProperty("name").GetProperty("content").GetString());
        Assert.Contains(servedValue, typedObject.GetProperty(servedKey).GetRawText(), StringComparison.Ordinal);
        // Every property the write touched is stamped with who wrote it.
        Assert.Equal("TacticalAPI", typedObject.GetProperty(servedKey).GetProperty("creationMetaData").GetProperty("creatorIdentity").GetProperty("stringIdentity").GetString());
    }

    [Fact]
    public async Task A_route_updated_sparsely_keeps_its_waypoints_and_changes_its_speed()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("OK", (await Invoke("Situation", "AddOrUpdateSituationObjects", """
            {
              "situationObjects": [ { "route": {
                "identity": { "stringIdentity": "sparse-route" },
                "reporter": { "stringIdentity": "TacticalAPI" },
                "reportingTime": "2026-09-19T10:00:00Z",
                "marchSpeed": { "content": 12 },
                "location": { "content": { "routeLocation": { "wayPoints": [ { "latitudeCoordinate": 54.1, "longitudeCoordinate": 10.1 } ] } } }
              } } ]
            }
            """, ct)).Status);

        Assert.Equal("OK", (await Invoke("Situation", "AddOrUpdateSituationObjects", """
            {
              "situationObjects": [ { "route": {
                "identity": { "stringIdentity": "sparse-route" },
                "reporter": { "stringIdentity": "Planer" },
                "reportingTime": "2026-09-19T10:05:00Z",
                "marchSpeed": { "content": 30 }
              } } ]
            }
            """, ct)).Status);

        var read = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        using var doc = JsonDocument.Parse(read.Response!);
        var route = doc.RootElement.GetProperty("situationObjects").EnumerateArray()
            .Single(o => o.TryGetProperty("route", out var r) && r.GetProperty("identity").GetProperty("stringIdentity").GetString() == "sparse-route")
            .GetProperty("route");

        Assert.Equal(30, route.GetProperty("marchSpeed").GetProperty("content").GetInt32());
        Assert.Equal("Planer", route.GetProperty("marchSpeed").GetProperty("creationMetaData").GetProperty("creatorIdentity").GetProperty("stringIdentity").GetString());
        // Untouched: the waypoints, still stamped by the first writer.
        Assert.Equal(54.1, route.GetProperty("location").GetProperty("content").GetProperty("routeLocation").GetProperty("wayPoints")[0].GetProperty("latitudeCoordinate").GetDouble());
        Assert.Equal("TacticalAPI", route.GetProperty("location").GetProperty("creationMetaData").GetProperty("creatorIdentity").GetProperty("stringIdentity").GetString());
        // The object itself keeps its creator.
        Assert.Equal("TacticalAPI", route.GetProperty("creationMetaData").GetProperty("creatorIdentity").GetProperty("stringIdentity").GetString());
    }

    [Fact]
    public async Task An_object_keeps_its_type_and_an_update_as_another_type_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("OK", (await Invoke("Situation", "AddOrUpdateSituationObjects", """
            {
              "situationObjects": [ { "symbol": {
                "identity": { "stringIdentity": "was-a-symbol" },
                "reporter": { "stringIdentity": "TacticalAPI" },
                "reportingTime": "2026-09-19T10:00:00Z"
              } } ]
            }
            """, ct)).Status);

        var asRoute = await Invoke("Situation", "AddOrUpdateSituationObjects", """
            {
              "situationObjects": [ { "route": {
                "identity": { "stringIdentity": "was-a-symbol" },
                "reporter": { "stringIdentity": "TacticalAPI" },
                "reportingTime": "2026-09-19T10:01:00Z",
                "marchSpeed": { "content": 5 }
              } } ]
            }
            """, ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, asRoute.Status);
        var reason = asRoute.Metadata[BowireTacticalApiProtocol.RefusalMessageKey];
        Assert.Contains("is a symbol, not a route", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_key_lands_in_the_map_under_its_source_on_any_type()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("OK", (await Invoke("Situation", "AddOrUpdateSituationObjects", """
            {
              "situationObjects": [ { "actionTask": {
                "identity": { "stringIdentity": "task-with-key" },
                "reporter": { "stringIdentity": "TacticalAPI" },
                "reportingTime": "2026-09-19T10:00:00Z",
                "foreignKey": { "content": { "stringIdentity": "BMS-4711" }, "source": "BMS" }
              } } ]
            }
            """, ct)).Status);

        var read = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        using var doc = JsonDocument.Parse(read.Response!);
        var task = doc.RootElement.GetProperty("situationObjects").EnumerateArray()
            .Single(o => o.TryGetProperty("actionTask", out var t) && t.GetProperty("identity").GetProperty("stringIdentity").GetString() == "task-with-key")
            .GetProperty("actionTask");

        var key = task.GetProperty("foreignKeys").GetProperty("BMS");
        Assert.Equal("BMS-4711", key.GetProperty("content").GetProperty("stringIdentity").GetString());
        Assert.Equal("BMS", key.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_document_without_any_type_set_is_refused_with_every_type_named()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Invoke("Situation", "AddOrUpdateSituationObjects", """{ "situationObjects": [ { } ] }""", ct);

        Assert.Equal(BowireTacticalApiProtocol.RefusedStatus, result.Status);
        Assert.Contains("set exactly one of the situation object types", result.Metadata[BowireTacticalApiProtocol.RefusalMessageKey], StringComparison.Ordinal);
    }

    private Task<InvokeResult> Invoke(string service, string method, string body, CancellationToken ct) =>
        _plugin.InvokeAsync(
            _server.ServerUrl, service, method,
            jsonMessages: [body], showInternalServices: false, metadata: null, ct: ct);
}
