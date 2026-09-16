// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Seeds the overlay: seven MIL-STD-2525D control measures — lines, areas,
/// an arrow, a corridor, a sector, an ellipse — laid over the tracks of
/// <see cref="SeededSituation"/>.
/// </summary>
/// <remarks>
/// <para>
/// The tracks are points, and a point is the one shape a symbol renderer
/// can draw from the code alone. Everything else the standard calls a
/// tactical graphic is drawn from its geometry: a boundary follows its
/// vertices, an axis of advance needs a path and a width, a range fan is
/// a vertex with two ranges and two azimuths. The TacticalAPI schema has
/// a <c>SymbolLocation</c> case for each of those, and until this file the
/// sample used exactly one of them. A renderer for the others cannot be
/// built against data that does not exist, so this is the data.
/// </para>
/// <para>
/// One graphic per location case the standard has a symbol for — Line,
/// Polygon, Multipoint, Corridor, Fan, Ellipse — so a consumer that
/// handles the cases one at a time can tell which one it has not reached
/// yet. <c>RouteLocation</c> and <c>SketchLocation</c> are left out: a
/// route is its own situation-object type upstream, and a sketch carries
/// its own colour and line style instead of a symbol code.
/// </para>
/// <para>
/// The codes are 2525D, as the twenty-digit numeric form the schema
/// carries in <c>NumericIdentifier</c>, not the fifteen-letter 2525C
/// strings the tracks use. The multipoint renderer the Bowire side has
/// in view (mil-sym-ts) speaks 2525D and later, not C, and a
/// twenty-digit code split into two ten-digit halves is what a real
/// TacticalAPI producer sends — so the sample exercises the numeric path
/// too, which the tracks never did.
/// </para>
/// <para>
/// Nothing here moves. Control measures are planned, not observed; a
/// phase line that drifts is as wrong as a command post that does. The
/// tick skips them because they carry no <c>TrackMotion</c> and no
/// <c>Point</c> location, and both are true on purpose.
/// </para>
/// </remarks>
internal static class SeededOverlay
{
    /// <summary>
    /// The 2525D codes used here, twenty digits each. Version <c>10</c>
    /// (2525D), reality context, symbol set <c>25</c> (control measures),
    /// status present. Digits 9–10 carry the echelon where the graphic
    /// has one, digits 11–16 the entity code.
    /// </summary>
    internal static class Sidc
    {
        /// <summary>Boundary (110100), friendly, battalion echelon (16).</summary>
        public const string BoundaryBattalion = "10032500161101000000";
        /// <summary>Phase Line (140300), friendly.</summary>
        public const string PhaseLine = "10032500001403000000";
        /// <summary>Assembly Area (150200), friendly.</summary>
        public const string AssemblyArea = "10032500001502000000";
        /// <summary>Axis of Advance, Main Attack (151403), friendly.</summary>
        public const string AxisOfAdvanceMainAttack = "10032500001514030000";
        /// <summary>Air Corridor (170100), friendly.</summary>
        public const string AirCorridor = "10032500001701000000";
        /// <summary>Weapon/Sensor Range Fan, Sector (242200), friendly.</summary>
        public const string SensorRangeFanSector = "10032500002422000000";
        /// <summary>Defended Area, Ellipse/Circle (200201), hostile.</summary>
        public const string DefendedAreaEllipseHostile = "10062500002002010000";
    }

    /// <summary>Add the overlay to <paramref name="objects"/>, keyed the way the service keys everything.</summary>
    public static void AddTo(Dictionary<string, SituationObject> objects, Identity reporter, Timestamp now)
    {
        void Add(string uuid, string sidc, string name, SymbolLocation location)
        {
            var graphic = BuildGraphic(uuid, sidc, name, location, reporter, now);
            objects[IdentityKeys.Of(graphic.Symbol.Identity)] = graphic;
        }

        // --- Boundary: north–south between the convoys' ground and the
        // engagement, a battalion on either side. Line, four vertices.
        Add("0be10100-6666-4f66-9f66-ffffffff0001", Sidc.BoundaryBattalion,
            "Boundary Alpha/Bravo",
            new SymbolLocation
            {
                Line = new Line
                {
                    LocationTime = now,
                    Points = { P(54.36, 10.55), P(54.28, 10.58), P(54.20, 10.53), P(54.12, 10.56) },
                },
            });

        // --- Phase line: east–west across the engagement's line of
        // advance, the objective Blau is closing on. Line, four vertices.
        Add("0be10100-6666-4f66-9f66-ffffffff0002", Sidc.PhaseLine,
            "PL HANSE",
            new SymbolLocation
            {
                Line = new Line
                {
                    LocationTime = now,
                    Points = { P(54.29, 10.62), P(54.27, 10.72), P(54.26, 10.82), P(54.25, 10.92) },
                },
            });

        // --- Assembly area: the ground Convoy Alpha leaves from. Polygon,
        // five vertices around the convoy's origin at 54.09°N 10.20°E.
        Add("0be10100-6666-4f66-9f66-ffffffff0003", Sidc.AssemblyArea,
            "AA BUCHE",
            new SymbolLocation
            {
                Polygon = new Polygon
                {
                    LocationTime = now,
                    Points = { P(54.11, 10.16), P(54.115, 10.22), P(54.09, 10.25), P(54.065, 10.21), P(54.07, 10.16) },
                },
            });

        // --- Axis of advance: Blau's main attack, south-east onto Rot.
        // Multipoint, because the standard fixes what each point means:
        // the first three are the centreline, the last one sets the width
        // of the arrow — it is not a vertex of the shape.
        Add("0be10100-6666-4f66-9f66-ffffffff0004", Sidc.AxisOfAdvanceMainAttack,
            "AXIS BLAU",
            new SymbolLocation
            {
                Multipoint = new Multipoint
                {
                    LocationTime = now,
                    Points = { P(54.32, 10.72), P(54.28, 10.78), P(54.245, 10.835), P(54.235, 10.815) },
                },
            });

        // --- Air corridor: the UAV's way from the coast out to its orbit
        // over the bay. Corridor, a centreline and a width in metres.
        Add("0be10100-6666-4f66-9f66-ffffffff0005", Sidc.AirCorridor,
            "AC KITE",
            new SymbolLocation
            {
                Corridor = new Corridor
                {
                    LocationTime = now,
                    Points = { P(54.20, 11.20), P(54.10, 11.35), P(54.04, 11.46) },
                    Width = 2_000,
                },
            });

        // --- Sensor range fan: the sector the radar at the sweep centre
        // watches, out over the water. Fan: a vertex, two ranges, an
        // orientation (clockwise from north to the left edge) and a width.
        Add("0be10100-6666-4f66-9f66-ffffffff0006", Sidc.SensorRangeFanSector,
            "Radar Wismar",
            new SymbolLocation
            {
                Fan = new Fan
                {
                    LocationTime = now,
                    VertexPoint = P(SeededSituation.CentreLatitude, SeededSituation.CentreLongitude),
                    MinimumRangeDimension = 1_000,
                    MaximumRangeDimension = 12_000,
                    OrientationAngle = 300,
                    SectorSizeAngle = 90,
                },
            });

        // --- Defended area: hostile, offshore to the north-east, so the
        // overlay has one graphic in red. Ellipse: a centre and one point
        // on each axis.
        Add("0be10100-6666-4f66-9f66-ffffffff0007", Sidc.DefendedAreaEllipseHostile,
            "Defended Area (hostile)",
            new SymbolLocation
            {
                Ellipse = new Ellipse
                {
                    LocationTime = now,
                    CenterPoint = P(54.12, 11.70),
                    FirstConjugateDiameterPoint = P(54.12, 11.76),
                    SecondConjugateDiameterPoint = P(54.145, 11.70),
                },
            });
    }

    /// <summary>
    /// Split a twenty-digit 2525D code into the two ten-digit halves the
    /// schema carries. The first half always starts with the version
    /// digits, so neither half loses a leading zero on the way through an
    /// integer — a consumer reassembles the code with <c>D10</c> on each.
    /// </summary>
    internal static NumericIdentifier Numeric(string sidc)
    {
        if (sidc.Length != 20)
            throw new ArgumentException($"A 2525D code has twenty digits, got {sidc.Length}: '{sidc}'.", nameof(sidc));
        return new NumericIdentifier
        {
            FirstTenDigits = long.Parse(sidc.AsSpan(0, 10), CultureInfo.InvariantCulture),
            SecondTenDigits = long.Parse(sidc.AsSpan(10, 10), CultureInfo.InvariantCulture),
        };
    }

    private static GeoPoint P(double latitude, double longitude) =>
        new() { LatitudeCoordinate = latitude, LongitudeCoordinate = longitude };

    private static SituationObject BuildGraphic(
        string uuid, string sidc, string name, SymbolLocation location,
        Identity reporter, Timestamp now)
    {
        var creationMeta = new CreationMetaData
        {
            CreationTime = now,
            CreatorIdentity = reporter,
        };

        var symbol = new Symbol
        {
            Identity = new Identity { UuidIdentity = uuid },
            CreationMetaData = creationMeta,
            Name = new DataPropertyString { CreationMetaData = creationMeta, Content = name },
            SymbolIdentifier = new DataPropertySymbolIdentifier
            {
                CreationMetaData = creationMeta,
                Content = new SymbolIdentifier
                {
                    SymbolCatalog = SymbolCatalog.Mil2525D,
                    NumericIdentifier = Numeric(sidc),
                },
            },
            Location = new DataPropertyLocation
            {
                CreationMetaData = creationMeta,
                Content = location,
            },
        };
        return new SituationObject { Symbol = symbol };
    }
}
