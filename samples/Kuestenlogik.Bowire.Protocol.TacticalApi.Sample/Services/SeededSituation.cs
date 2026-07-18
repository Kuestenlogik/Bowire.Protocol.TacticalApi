// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Seeds three MIL-2525C contacts arranged 120° apart around a single
/// radar centre at 54.00°N / 11.50°E (north of Insel Poel, off the
/// German coast). The mover in <see cref="SituationServiceImpl"/>
/// rotates the trio around that centre clockwise, giving the workbench
/// a deterministic, visually-obvious radar-sweep pattern instead of
/// the random-nudge motion the Harbor sample uses.
/// </summary>
internal static class SeededSituation
{
    /// <summary>Radar centre (lat / lon) — origin of the rotation.</summary>
    public const double CentreLatitude = 54.00;
    public const double CentreLongitude = 11.50;

    /// <summary>Track radius in degrees (~6 km at this latitude).</summary>
    public const double RadiusDegrees = 0.06;

    public static Dictionary<string, SituationObject> Build()
    {
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var reporter = new Identity { StringIdentity = "TacticalApi.RadarSweep" };
        var result = new Dictionary<string, SituationObject>(StringComparer.Ordinal);

        // Three tracks at 0° / 120° / 240° around the centre. The
        // bearing matches the order Bowire renders them (top of the
        // circle first, clockwise from there).
        Add(result, BuildTrack(
            uuid: "5a4a5147-9c5d-4c1e-9e9e-2b48d4a35b1a",
            symbolCode: "SFSP------*****",   // Sea / Friend / Present surface vessel
            name: "Patrol Möwe (friendly)",
            initialBearingDegrees: 0,
            reporter, now));
        Add(result, BuildTrack(
            uuid: "f5b3e2a6-9d27-4d4f-93c9-1e7b9f4d0c52",
            symbolCode: "SHSP------*****",   // Sea / Hostile / Present surface contact
            name: "Surface Contact (hostile)",
            initialBearingDegrees: 120,
            reporter, now));
        Add(result, BuildTrack(
            uuid: "9d1f2e0b-c2d4-4a31-89e0-1aef8a8e6021",
            symbolCode: "SNSP------*****",   // Sea / Neutral / Present surface vessel
            name: "Cargo Hanse (neutral)",
            initialBearingDegrees: 240,
            reporter, now));
        return result;
    }

    private static void Add(Dictionary<string, SituationObject> dict, (string id, SituationObject obj) entry)
        => dict[entry.id] = entry.obj;

    private static (string id, SituationObject obj) BuildTrack(
        string uuid, string symbolCode, string name,
        double initialBearingDegrees,
        Identity reporter, Timestamp now)
    {
        var identity = new Identity { UuidIdentity = uuid };
        var creationMeta = new CreationMetaData
        {
            CreationTime = now,
            CreatorIdentity = reporter,
        };
        // Convert bearing → lat/lon offset around centre. Small-angle
        // approximation is fine at radar scale — no need to bother
        // with the proper haversine.
        var bearingRad = initialBearingDegrees * Math.PI / 180.0;
        var lat = CentreLatitude + RadiusDegrees * Math.Cos(bearingRad);
        var lon = CentreLongitude + RadiusDegrees * Math.Sin(bearingRad);

        var symbol = new Symbol
        {
            Identity = identity,
            CreationMetaData = creationMeta,
            Name = new DataPropertyString { CreationMetaData = creationMeta, Content = name },
            SymbolIdentifier = new DataPropertySymbolIdentifier
            {
                CreationMetaData = creationMeta,
                Content = new SymbolIdentifier
                {
                    SymbolCatalog = SymbolCatalog.Mil2525C,
                    StringIdentifier = symbolCode,
                },
            },
            Location = new DataPropertyLocation
            {
                CreationMetaData = creationMeta,
                Content = new SymbolLocation
                {
                    Point = new Point
                    {
                        LocationTime = now,
                        GeoPoint = new GeoPoint
                        {
                            LatitudeCoordinate = lat,
                            LongitudeCoordinate = lon,
                        },
                    },
                },
            },
        };
        return (uuid, new SituationObject { Symbol = symbol });
    }
}
