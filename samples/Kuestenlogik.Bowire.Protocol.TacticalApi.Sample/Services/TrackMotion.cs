// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Where a seeded track is at a given point in the exercise.
/// </summary>
/// <remarks>
/// <para>
/// The sample used to have exactly one motion — every object rotating
/// around one centre — so the rule lived in the service as a single
/// formula and each track was described by nothing more than its initial
/// bearing. Once the scenario grew a convoy driving a road, a drone
/// orbiting a bay and two groups closing on each other, that stopped
/// being a formula and became a property of the track.
/// </para>
/// <para>
/// Deterministic on elapsed time rather than integrated per tick: a
/// subscriber joining late sees the same positions as one that has been
/// listening since the start, and a missed tick cannot make a convoy
/// drift off its road.
/// </para>
/// </remarks>
internal abstract record TrackMotion
{
    /// <summary>Position after <paramref name="elapsedSeconds"/> of exercise.</summary>
    public abstract (double Latitude, double Longitude) At(double elapsedSeconds);

    /// <summary>Mean earth radius, in metres — enough for a demo scenario.</summary>
    protected const double EarthRadiusMetres = 6_371_000.0;

    /// <summary>
    /// Great-circle destination from a point, given a bearing and a
    /// distance. Straight legs stay straight on the map, which the
    /// lat/lon-offset approximation the sample started with does not
    /// manage once a leg runs more than a few kilometres.
    /// </summary>
    protected static (double Latitude, double Longitude) Destination(
        double latitude, double longitude, double bearingDegrees, double metres)
    {
        var bearing = bearingDegrees * Math.PI / 180.0;
        var lat1 = latitude * Math.PI / 180.0;
        var lon1 = longitude * Math.PI / 180.0;
        var angular = metres / EarthRadiusMetres;

        var lat2 = Math.Asin(
            (Math.Sin(lat1) * Math.Cos(angular))
            + (Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing)));
        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1),
            Math.Cos(angular) - (Math.Sin(lat1) * Math.Sin(lat2)));

        return (lat2 * 180.0 / Math.PI, lon2 * 180.0 / Math.PI);
    }
}

/// <summary>
/// A track circling a centre — the original RadarSweep motion, kept
/// because it is the clearest thing to look at while checking that a map
/// is updating at all.
/// </summary>
internal sealed record OrbitMotion(
    double CentreLatitude,
    double CentreLongitude,
    double RadiusMetres,
    double InitialBearingDegrees,
    double DegreesPerSecond) : TrackMotion
{
    public override (double Latitude, double Longitude) At(double elapsedSeconds)
        => Destination(
            CentreLatitude, CentreLongitude,
            InitialBearingDegrees + (DegreesPerSecond * elapsedSeconds),
            RadiusMetres);
}

/// <summary>
/// A track driving a straight leg at constant speed.
/// </summary>
/// <remarks>
/// <paramref name="HeadStartMetres"/> is subtracted rather than clamped
/// at zero: a convoy's members are spread along their road from the
/// first frame, not stacked on the leader until it has pulled far enough
/// ahead. The first frames are exactly when someone is checking whether
/// per-entity grouping works, so starting them on one point would hide
/// the thing the scenario exists to show.
/// </remarks>
internal sealed record LegMotion(
    double OriginLatitude,
    double OriginLongitude,
    double BearingDegrees,
    double MetresPerSecond,
    double HeadStartMetres = 0.0) : TrackMotion
{
    public override (double Latitude, double Longitude) At(double elapsedSeconds)
        => Destination(
            OriginLatitude, OriginLongitude, BearingDegrees,
            (MetresPerSecond * elapsedSeconds) - HeadStartMetres);
}
