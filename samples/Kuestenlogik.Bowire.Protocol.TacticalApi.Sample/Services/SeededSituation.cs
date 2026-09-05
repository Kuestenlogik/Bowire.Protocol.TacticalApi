// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Seeds the demo scenario: thirteen MIL-2525C tracks in five groups,
/// spread across the western Baltic and the Schleswig-Holstein coast.
/// </summary>
/// <remarks>
/// <para>
/// The sample began as three contacts 120° apart on one radar circle,
/// which is the clearest possible way to see that a map is updating.
/// That circle is still here — <c>RadarSweep</c> — but on its own it
/// answers only one question. Everything the workbench does with a
/// stream of positions beyond drawing a dot needs several entities that
/// are actually different from each other: separate trajectories,
/// per-entity grouping, a legend with more than one row, and a colour
/// scheme that has to distinguish things rather than decorate one.
/// </para>
/// <para>
/// So the groups are deliberately unlike each other. They sit in
/// different places, move at different speeds, take different shapes on
/// the map (a circle, straight legs, two lines converging), and carry
/// three different affinities. A bug that merges two tracks, or colours
/// by message type instead of by entity, or drops the second half of a
/// multi-entity frame, shows up here as something visibly wrong rather
/// than as a plausible picture.
/// </para>
/// <para>
/// Every object is one entry in the <c>situationObjects</c> array of a
/// single snapshot message. That is the shape that matters: N entities
/// arrive in ONE frame, so anything grouping them has to resolve an
/// identity per array element. The DIS sample in Bowire.Protocol.Dis is
/// deliberately the opposite — one entity per PDU, grouped across frames.
/// </para>
/// </remarks>
internal static class SeededSituation
{
    /// <summary>Radar centre (lat / lon) — origin of the RadarSweep rotation.</summary>
    public const double CentreLatitude = 54.00;
    public const double CentreLongitude = 11.50;

    /// <summary>RadarSweep track radius, in metres (~6 km).</summary>
    public const double RadiusMetres = 6_600.0;

    /// <summary>One full RadarSweep rotation a minute.</summary>
    public const double SweepDegreesPerSecond = 360.0 / 60.0;

    /// <summary>
    /// Build the scenario: the objects, and how each one moves.
    /// </summary>
    public static (Dictionary<string, SituationObject> Objects,
                   Dictionary<string, TrackMotion> Motions) Build()
    {
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var reporter = new Identity { StringIdentity = "TacticalApi.Scenario" };
        var objects = new Dictionary<string, SituationObject>(StringComparer.Ordinal);
        var motions = new Dictionary<string, TrackMotion>(StringComparer.Ordinal);

        void Add(string uuid, string symbolCode, string name, TrackMotion motion)
        {
            var start = motion.At(0.0);
            objects[uuid] = BuildTrack(uuid, symbolCode, name, start, reporter, now);
            motions[uuid] = motion;
        }

        // --- RadarSweep: the original three, 120° apart on one circle ---
        Add("5a4a5147-9c5d-4c1e-9e9e-2b48d4a35b1a", "SFSP------*****",
            "Patrol Möwe (friendly)",
            new OrbitMotion(CentreLatitude, CentreLongitude, RadiusMetres, 0, SweepDegreesPerSecond));
        Add("f5b3e2a6-9d27-4d4f-93c9-1e7b9f4d0c52", "SHSP------*****",
            "Surface Contact (hostile)",
            new OrbitMotion(CentreLatitude, CentreLongitude, RadiusMetres, 120, SweepDegreesPerSecond));
        Add("9d1f2e0b-c2d4-4a31-89e0-1aef8a8e6021", "SNSP------*****",
            "Cargo Hanse (neutral)",
            new OrbitMotion(CentreLatitude, CentreLongitude, RadiusMetres, 240, SweepDegreesPerSecond));

        // --- Convoy Alpha: three vehicles nose-to-tail heading east ---
        // Inland and well clear of the sweep circle, so the two groups
        // never overlap on screen.
        for (var i = 0; i < 3; i++)
        {
            Add($"c0a1{i:d2}00-1111-4a11-9a11-aaaaaaaa00{i:d2}", "SFGPUCV---*****",
                $"Convoy Alpha {i + 1} (friendly)",
                new LegMotion(54.09, 10.20, BearingDegrees: 90, MetresPerSecond: 11.0,
                              HeadStartMetres: i * 60.0));
        }

        // --- Convoy Bravo: two trucks heading south, a different bearing ---
        for (var i = 0; i < 2; i++)
        {
            Add($"c0b2{i:d2}00-2222-4b22-9b22-bbbbbbbb00{i:d2}", "SFGPUCT---*****",
                $"Convoy Bravo {i + 1} (friendly)",
                new LegMotion(53.86, 11.05, BearingDegrees: 180, MetresPerSecond: 16.0,
                              HeadStartMetres: i * 80.0));
        }

        // --- Kite: a UAV orbiting the Bay of Lübeck, faster and tighter ---
        // A second circle at a different centre, radius and rate, so a
        // grouping that keys on "looks like a circle" cannot pass.
        Add("d40e0000-3333-4c33-9c33-cccccccc0001", "SFAPMFQ---*****",
            "UAV Kite 07 (friendly)",
            new OrbitMotion(54.02, 11.50, RadiusMetres: 2_500.0,
                            InitialBearingDegrees: 0, DegreesPerSecond: 360.0 / 90.0));

        // --- Engagement: two pairs closing head-on across open ground ---
        // The only hostile ground tracks in the scenario. Their legs run
        // towards each other, so the trajectories cross — which is where
        // a grouping bug stops being subtle.
        for (var i = 0; i < 2; i++)
        {
            Add($"e1b1{i:d2}00-4444-4d44-9d44-dddddddd00{i:d2}", "SFGPUCA---*****",
                $"Blau {i + 1} (friendly)",
                new LegMotion(54.30, 10.75, BearingDegrees: 135, MetresPerSecond: 9.0,
                              HeadStartMetres: i * 90.0));
            Add($"e1r1{i:d2}00-5555-4e55-9e55-eeeeeeee00{i:d2}", "SHGPUCA---*****",
                $"Rot {i + 1} (hostile)",
                new LegMotion(54.24, 10.83, BearingDegrees: 315, MetresPerSecond: 9.0,
                              HeadStartMetres: i * 90.0));
        }

        return (objects, motions);
    }

    private static SituationObject BuildTrack(
        string uuid, string symbolCode, string name,
        (double Latitude, double Longitude) start,
        Identity reporter, Timestamp now)
    {
        var identity = new Identity { UuidIdentity = uuid };
        var creationMeta = new CreationMetaData
        {
            CreationTime = now,
            CreatorIdentity = reporter,
        };

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
                            LatitudeCoordinate = start.Latitude,
                            LongitudeCoordinate = start.Longitude,
                        },
                    },
                },
            },
        };
        return new SituationObject { Symbol = symbol };
    }
}
