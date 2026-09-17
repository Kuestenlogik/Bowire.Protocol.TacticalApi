// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// One seeded blue force: what it is, and how it gets about.
/// </summary>
/// <param name="Id">String identity — blue forces are keyed by whoever created them, and short ids are what low-bandwidth links carry.</param>
/// <param name="Callsign">How the force reads on a map label.</param>
/// <param name="SymbolCode">MIL-2525C 15-character SIDC.</param>
/// <param name="Type">Vehicle / unmanned / leader — all three are independent flags upstream, not one enum.</param>
/// <param name="Motion">How it moves, or <c>null</c> when it rides the host platform (see <paramref name="MountHostId"/>).</param>
/// <param name="MountHostId">The force this one is carried by, if any.</param>
/// <param name="OrganizationUnitId">The <c>Situation</c>-side organisation unit it belongs to, if any.</param>
/// <param name="IsOwn">True for the force this host reports as itself.</param>
internal sealed record SeededBlueForce(
    string Id,
    string Callsign,
    string SymbolCode,
    BlueForceType Type,
    TrackMotion? Motion,
    string? MountHostId = null,
    string? OrganizationUnitId = null,
    bool IsOwn = false);

/// <summary>
/// The blue-force side of the scenario: four friendly participants on
/// the Schleswig coast, north of everything the <c>Situation</c> service
/// is showing.
/// </summary>
/// <remarks>
/// <para>
/// Blue forces are not situation objects with a different name. A
/// situation object is something a system reports *about*; a blue force
/// reports *itself*, keeps itself alive with a keep-alive, and vanishes
/// when it stops. So the four here are picked to make that difference
/// visible rather than to fill a list: a command post that never moves,
/// a vehicle that is also this host's own position, a UAV bolted to that
/// vehicle and therefore always exactly where it is, and a dismounted
/// section walking a different way at walking pace.
/// </para>
/// <para>
/// The mounted UAV is the one worth watching. It has no motion of its
/// own — it reports its host's position, because that is what being
/// mounted means — so a client that draws <c>mount_host</c> as a
/// relationship and a client that draws two unrelated dots both look
/// right until the vehicle is told to move. Then only one of them
/// stays right.
/// </para>
/// <para>
/// They sit around 54.45°N 9.85°E, clear of the RadarSweep circle, both
/// convoys, the Bay of Lübeck orbit and the engagement — five groups
/// that already overlap nothing, and now a sixth that overlaps them.
/// </para>
/// </remarks>
internal static class SeededBlueForces
{
    /// <summary>The organisation unit the coastal detachment belongs to.</summary>
    public const string DetachmentId = "OU-KUESTENZUG";

    /// <summary>Build the seeded set. Order is display order.</summary>
    public static IReadOnlyList<SeededBlueForce> Build() =>
    [
        // Static: a command post is a position that should not drift.
        // If it does, something is integrating instead of evaluating.
        // An infantry platoon's headquarters: UCI with the HQ indicator
        // and the platoon echelon in the symbol modifier.
        new SeededBlueForce(
            Id: "BF-NORDSTERN",
            Callsign: "Nordstern",
            SymbolCode: "SFGPUCI---HD***",
            Type: new BlueForceType { IsLeader = true },
            Motion: new LegMotion(54.47, 9.86, BearingDegrees: 0, MetresPerSecond: 0.0),
            OrganizationUnitId: DetachmentId),

        // This host. Its position comes from OwnPlatform, so an operator
        // fix sent to OwnPose.UpdatePosition moves it. A utility vehicle
        // (EVU) — the one the UAV below is mounted on.
        new SeededBlueForce(
            Id: OwnPlatform.OwnBlueForceId,
            Callsign: "Gecko 21",
            SymbolCode: "SFGPEVU---*****",
            Type: new BlueForceType { IsVehicle = true },
            Motion: null,
            OrganizationUnitId: DetachmentId,
            IsOwn: true),

        // Mounted on Gecko 21: no motion of its own, reports the host's.
        new SeededBlueForce(
            Id: "BF-KIEBITZ-1",
            Callsign: "Kiebitz 1",
            SymbolCode: "SFAPMFQR--*****",
            Type: new BlueForceType { IsUnmanned = true },
            Motion: null,
            MountHostId: OwnPlatform.OwnBlueForceId,
            OrganizationUnitId: DetachmentId),

        // Dismounted, walking pace, a different bearing — the slowest
        // thing on the map, and the one that shows whether a client's
        // history trail has any resolution at all.
        new SeededBlueForce(
            Id: "BF-MOEWE-3",
            Callsign: "Möwe 3",
            SymbolCode: "SFGPUCI----C***",
            Type: new BlueForceType { IsLeader = true },
            Motion: new LegMotion(54.46, 9.92, BearingDegrees: 200, MetresPerSecond: 1.4),
            OrganizationUnitId: DetachmentId),
    ];
}
