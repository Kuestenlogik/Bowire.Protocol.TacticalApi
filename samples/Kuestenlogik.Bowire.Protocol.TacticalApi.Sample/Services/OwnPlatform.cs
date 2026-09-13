// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Where this host thinks it is. The one position both new services
/// read: <see cref="OwnPoseServiceImpl"/> reports it, and
/// <see cref="BlueForceTrackingServiceImpl"/> puts the same coordinates
/// on the blue force flagged <c>own_blue_force</c> — and on the drone
/// mounted to it.
/// </summary>
/// <remarks>
/// <para>
/// The sample host plays <c>Gecko 21</c>, a command vehicle driving the
/// coast road north-east of Eckernförde. Left alone it follows its leg
/// like any other track. Call <c>OwnPose.UpdatePosition</c> and the
/// operator's fix takes over instead — which is the point of having the
/// two services in one host: one write moves the own pose, the own blue
/// force, and the UAV riding on it, and all three are visible in
/// different places in the workbench at once. A sample where a write
/// changes nothing anyone can see is a sample that cannot tell you
/// whether the write worked.
/// </para>
/// <para>
/// An operator fix that is never refreshed is not discarded: the schema
/// says a position may keep being used after it goes stale, flagged
/// <c>is_invalid_or_expired</c>, and that is what happens here after
/// <see cref="StaleAfter"/>. The flag is the interesting half — it is
/// easy to model a position as present or absent, and wrong.
/// </para>
/// </remarks>
internal sealed class OwnPlatform(ExerciseClock clock)
{
    /// <summary>The blue-force identity this host reports as its own.</summary>
    public const string OwnBlueForceId = "BF-GECKO-21";

    /// <summary>Source identifier reported while the platform follows its own leg.</summary>
    private const string DeadReckoningSource = "Sample.DeadReckoning";

    /// <summary>
    /// How long an operator fix stays fresh. The upstream
    /// <c>BlueForceTracking</c> contract uses the same 30 s for
    /// keep-alives, so the two halves of the sample expire in step.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>The leg Gecko 21 drives while nobody is steering it.</summary>
    private static readonly TrackMotion DefaultMotion =
        new LegMotion(54.44, 9.80, BearingDegrees: 45, MetresPerSecond: 9.0);

    private readonly object _gate = new();
    private UpdatePosition? _operatorFix;
    private DateTime _operatorFixAtUtc;

    /// <summary>The position as it stands right now.</summary>
    public Position Current()
    {
        UpdatePosition? fix;
        DateTime fixAtUtc;
        lock (_gate)
        {
            fix = _operatorFix;
            fixAtUtc = _operatorFixAtUtc;
        }

        if (fix?.PointLocation is not null)
        {
            return new Position
            {
                SourceIdentifier = fix.SourceIdentifier,
                PointLocation = fix.PointLocation.Clone(),
                IsInvalidOrExpired = DateTime.UtcNow - fixAtUtc > StaleAfter,
            };
        }

        var (latitude, longitude) = DefaultMotion.At(clock.ElapsedSeconds);
        return new Position
        {
            SourceIdentifier = DeadReckoningSource,
            PointLocation = NewPoint(latitude, longitude),
            IsInvalidOrExpired = false,
        };
    }

    /// <summary>
    /// Take an operator fix from <c>OwnPose.UpdatePosition</c>. Replaces
    /// the dead-reckoned leg from here on; a fix with no point clears the
    /// override and hands the platform back to its leg, which is the only
    /// way back without restarting the host.
    /// </summary>
    public void Apply(UpdatePosition? update)
    {
        lock (_gate)
        {
            _operatorFix = update?.PointLocation is null ? null : update.Clone();
            _operatorFixAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// A WGS84 point stamped with the current time and how it was obtained.
    /// </summary>
    /// <remarks>
    /// The default is <c>Estimate</c>, which is what upstream's reference
    /// client sends for an own-pose update; a position that came off a
    /// receiver is <c>Gps</c>, which is what it sends for a tracked blue
    /// force. The code is part of the position, not decoration — a
    /// consumer fusing tracks weights the two differently.
    /// </remarks>
    public static Point NewPoint(
        double latitude, double longitude, MeasurementCode measured = MeasurementCode.Estimate) =>
        new()
        {
            LocationTime = Timestamp.FromDateTime(DateTime.UtcNow),
            GeoPoint = new GeoPoint
            {
                LatitudeCoordinate = latitude,
                LongitudeCoordinate = longitude,
                MeasurementCode = measured,
            },
        };
}
