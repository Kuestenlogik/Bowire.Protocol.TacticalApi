// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Tests;

/// <summary>
/// What the sample README promises about the overlay: one graphic per
/// <c>SymbolLocation</c> case the standard has a symbol for, every code a
/// twenty-digit 2525D control measure in the numeric form, and none of it
/// moving. The overlay is the test data for a multipoint renderer, so
/// the shape a renderer will lean on is pinned here, before it exists.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeededOverlayTests : IClassFixture<InProcessSampleServerFixture>
{
    private readonly InProcessSampleServerFixture _server;
    private readonly BowireTacticalApiProtocol _plugin = new();

    public SeededOverlayTests(InProcessSampleServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public void Overlay_has_one_graphic_per_location_case_and_nothing_else()
    {
        var (objects, motions) = SeededSituation.Build();
        var overlay = objects.Values
            .Where(o => o.Symbol?.SymbolIdentifier?.Content?.SymbolCatalog == SymbolCatalog.Mil2525D)
            .ToList();

        Assert.Equal(7, overlay.Count);

        // Line twice (boundary, phase line), the rest once — and neither
        // of the two cases the README says are left out.
        var cases = overlay
            .Select(o => o.Symbol.Location.Content.LocationCase)
            .OrderBy(c => c)
            .ToList();
        Assert.Equal(
            new[]
            {
                SymbolLocation.LocationOneofCase.Ellipse,
                SymbolLocation.LocationOneofCase.Multipoint,
                SymbolLocation.LocationOneofCase.Corridor,
                SymbolLocation.LocationOneofCase.Line,
                SymbolLocation.LocationOneofCase.Line,
                SymbolLocation.LocationOneofCase.Polygon,
                SymbolLocation.LocationOneofCase.Fan,
            }.OrderBy(c => c),
            cases);

        foreach (var graphic in overlay)
        {
            // No motion — the tick has nothing to apply to a planned graphic.
            Assert.False(motions.ContainsKey(IdentityKeys.Of(graphic.Symbol.Identity)));
            Assert.NotNull(graphic.Symbol.Name?.Content);
            Assert.NotEmpty(graphic.Symbol.Name.Content);
        }
    }

    [Fact]
    public void Overlay_codes_are_twenty_digit_2525D_control_measures()
    {
        var (objects, _) = SeededSituation.Build();
        var codes = objects.Values
            .Select(o => o.Symbol?.SymbolIdentifier?.Content)
            .Where(id => id?.IdentifierCase == SymbolIdentifier.IdentifierOneofCase.NumericIdentifier)
            .Select(id => Reassemble(id!.NumericIdentifier))
            .ToList();

        Assert.Equal(7, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        foreach (var code in codes)
        {
            Assert.Equal(20, code.Length);
            Assert.StartsWith("10", code, StringComparison.Ordinal);          // 2525D
            Assert.Equal('0', code[2]);                                        // reality
            Assert.True(code[3] is '3' or '6', code);                              // friend or hostile
            Assert.Equal("25", code.Substring(4, 2));                          // control measures
        }

        // The README's codes are these constants; a split that drops a
        // digit would fail here before it failed on a map.
        Assert.Equal(SeededOverlay.Sidc.BoundaryBattalion, Reassemble(SeededOverlay.Numeric(SeededOverlay.Sidc.BoundaryBattalion)));
        Assert.Equal(SeededOverlay.Sidc.DefendedAreaEllipseHostile, Reassemble(SeededOverlay.Numeric(SeededOverlay.Sidc.DefendedAreaEllipseHostile)));
        Assert.Throws<ArgumentException>(() => SeededOverlay.Numeric("SFGPUCV---*****"));
    }

    [Fact]
    public async Task Overlay_arrives_in_the_snapshot_and_does_not_move_under_the_tick()
    {
        var ct = TestContext.Current.CancellationToken;

        var before = await Invoke("Situation", "GetSituationObjects", "{}", ct);
        _server.Situation.TickAt(elapsedSeconds: 900, nowUtc: DateTime.UtcNow);
        var after = await Invoke("Situation", "GetSituationObjects", "{}", ct);

        var overlayBefore = OverlayLocations(before.Response!);
        var overlayAfter = OverlayLocations(after.Response!);

        Assert.Equal(7, overlayBefore.Count);
        Assert.Equal(overlayBefore, overlayAfter);

        // The numeric identifier crosses the wire as two int64 halves, the
        // way a real producer sends it — not as the string the tracks use.
        Assert.Contains("\"firstTenDigits\"", before.Response, StringComparison.Ordinal);
        Assert.Contains("SYMBOL_CATALOG_MIL2525_D", before.Response, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private Task<InvokeResult> Invoke(string service, string method, string body, CancellationToken ct) =>
        _plugin.InvokeAsync(
            _server.ServerUrl, service, method,
            jsonMessages: [body], showInternalServices: false, metadata: null, ct: ct);

    private static string Reassemble(NumericIdentifier id) =>
        id.FirstTenDigits.ToString("D10", CultureInfo.InvariantCulture)
        + id.SecondTenDigits.ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>
    /// The overlay's <c>location</c> objects out of a GetSituationObjects
    /// response, keyed by uuid — the graphics are the objects whose
    /// symbol identifier is numeric.
    /// </summary>
    private static Dictionary<string, string> OverlayLocations(string response)
    {
        using var doc = JsonDocument.Parse(response);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var obj in doc.RootElement.GetProperty("situationObjects").EnumerateArray())
        {
            if (!obj.TryGetProperty("symbol", out var symbol)) continue;
            if (!symbol.TryGetProperty("symbolIdentifier", out var sid)
                || !sid.TryGetProperty("content", out var content)
                || !content.TryGetProperty("numericIdentifier", out _))
            {
                continue;
            }
            var uuid = symbol.GetProperty("identity").GetProperty("uuidIdentity").GetString()!;
            result[uuid] = symbol.GetProperty("location").GetProperty("content").GetRawText();
        }
        return result;
    }
}
