// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// The upstream <c>BlueForceTracking</c> service: friendly participants
/// that report themselves, seeded from <see cref="SeededBlueForces"/>
/// and joinable from the workbench via <c>AddOrUpdateBlueForces</c>.
/// </summary>
/// <remarks>
/// <para>
/// The upstream contract has a rule the <c>Situation</c> service does
/// not: a blue force must be re-sent at least every thirty seconds, and
/// is deleted implicitly when it stops. The sample implements that
/// rather than describing it. Add a blue force from the workbench, leave
/// it alone, and in half a minute it comes back through the subscription
/// once with <c>is_deleted</c> set and is then gone from
/// <c>GetBlueForces</c>. The seeded four are re-stamped on every tick,
/// so they stay.
/// </para>
/// <para>
/// That is the only behaviour in the sample a client can get wrong
/// silently. A client that ignores <c>is_deleted</c> keeps drawing a
/// force nobody is reporting any more — which on a map is not a stale
/// pixel but a friendly unit that is not there.
/// </para>
/// </remarks>
internal sealed class BlueForceTrackingServiceImpl(OwnPlatform platform)
    : BlueForceTracking.BlueForceTrackingBase, IScenarioTick
{
    private readonly SubscriberFanout<SubscribeBlueForceEventsResponse> _fanout = new();
    private readonly IReadOnlyList<SeededBlueForce> _seeded = SeededBlueForces.Build();

    /// <summary>Forces reported by a caller, with the time of their last keep-alive.</summary>
    private readonly Dictionary<string, ReportedBlueForce> _reported =
        new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private double _elapsedSeconds;

    public override Task<GetBlueForcesResponse> GetBlueForces(
        GetBlueForcesRequest request, ServerCallContext context)
    {
        var response = new GetBlueForcesResponse { Header = OkHeader() };
        response.BlueForces.Add(LiveForces());
        return Task.FromResult(response);
    }

    public override Task<AddOrUpdateBlueForcesResponse> AddOrUpdateBlueForces(
        AddOrUpdateBlueForcesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        var accepted = new List<BlueForce>();
        lock (_gate)
        {
            foreach (var update in request.BlueForcesToUpdates)
            {
                var key = KeyOf(update.Identity);
                if (key.Length == 0) continue;

                // "All fields must be filled in every call" upstream, so
                // the update replaces rather than merges — the only thing
                // carried over is the arrival time that keeps it alive.
                var force = ToBlueForce(update);
                _reported[key] = new ReportedBlueForce(force, now);
                accepted.Add(force);
            }
        }

        // Anyone holding a subscription should see a joining force at
        // once; waiting for the tick makes a successful write look like
        // a failed one for up to two seconds.
        if (accepted.Count > 0)
        {
            var frame = new SubscribeBlueForceEventsResponse { Header = OkHeader() };
            frame.UpdatedBlueForces.Add(accepted);
            _fanout.Broadcast(frame);
        }

        return Task.FromResult(new AddOrUpdateBlueForcesResponse { Header = OkHeader() });
    }

    public override async Task SubscribeBlueForceEvents(
        SubscribeBlueForceEventsRequest request,
        IServerStreamWriter<SubscribeBlueForceEventsResponse> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        using var subscription = _fanout.Subscribe();

        // "Initially, all existing blue forces are returned for every
        // call" — emit the snapshot before the first tick can arrive.
        var initial = new SubscribeBlueForceEventsResponse { Header = OkHeader() };
        initial.UpdatedBlueForces.Add(LiveForces());
        await responseStream.WriteAsync(initial).ConfigureAwait(false);

        try
        {
            await foreach (var frame in subscription.Reader
                .ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Subscriber closed the call — a clean exit, not a fault.
        }
    }

    /// <inheritdoc />
    public void Tick(double elapsedSeconds)
    {
        List<BlueForce> expired;
        lock (_gate)
        {
            _elapsedSeconds = elapsedSeconds;
            expired = SweepExpiredLocked(DateTime.UtcNow);
        }

        var frame = new SubscribeBlueForceEventsResponse { Header = OkHeader() };
        frame.UpdatedBlueForces.Add(LiveForces());
        frame.UpdatedBlueForces.Add(expired);
        _fanout.Broadcast(frame);
    }

    /// <summary>
    /// Drop reported forces whose keep-alive has run out and hand them
    /// back once, flagged deleted, so subscribers can retire them.
    /// </summary>
    private List<BlueForce> SweepExpiredLocked(DateTime nowUtc)
    {
        var expired = new List<BlueForce>();
        foreach (var (key, entry) in _reported)
        {
            if (nowUtc - entry.LastContactUtc <= OwnPlatform.StaleAfter) continue;

            var gone = entry.Force.Clone();
            gone.IsDeleted = true;
            expired.Add(gone);
        }
        foreach (var gone in expired)
        {
            _reported.Remove(KeyOf(gone.Identity));
        }
        return expired;
    }

    /// <summary>
    /// The seeded four at the current tick, plus every reported force
    /// still inside its keep-alive window. A reported force that shares
    /// an identity with a seeded one wins — the caller is telling us
    /// something newer than the script.
    /// </summary>
    private List<BlueForce> LiveForces()
    {
        double elapsedSeconds;
        ReportedBlueForce[] reported;
        lock (_gate)
        {
            elapsedSeconds = _elapsedSeconds;
            reported = [.. _reported.Values];
        }

        var byKey = new Dictionary<string, BlueForce>(StringComparer.Ordinal);
        foreach (var seed in _seeded)
        {
            byKey["str:" + seed.Id] = BuildSeeded(seed, elapsedSeconds);
        }
        foreach (var entry in reported)
        {
            byKey[KeyOf(entry.Force.Identity)] = entry.Force;
        }
        return [.. byKey.Values];
    }

    private BlueForce BuildSeeded(SeededBlueForce seed, double elapsedSeconds)
    {
        // A seeded force without a motion rides the host platform, so
        // both it and the host's own pose come from the same place —
        // an operator fix moves the pair together, and a mounted UAV
        // cannot end up a few metres from the vehicle carrying it.
        var point = seed.Motion is null
            ? platform.Current().PointLocation
            : Locate(seed.Motion, elapsedSeconds);

        var force = new BlueForce
        {
            Identity = new Identity { StringIdentity = seed.Id },
            // Re-stamped every tick: these four are the ones that are
            // supposed to survive the keep-alive sweep.
            LastContactTime = Timestamp.FromDateTime(DateTime.UtcNow),
            Callsign = seed.Callsign,
            Symbol = new SymbolIdentifier
            {
                SymbolCatalog = SymbolCatalog.Mil2525C,
                StringIdentifier = seed.SymbolCode,
            },
            BlueForceType = seed.Type,
            OwnBlueForce = seed.IsOwn,
            PointLocation = point,
        };
        if (seed.MountHostId is not null)
        {
            force.MountHost = new Identity { StringIdentity = seed.MountHostId };
        }
        if (seed.OrganizationUnitId is not null)
        {
            force.AssociatedOrganizationUnitIdentity =
                new Identity { StringIdentity = seed.OrganizationUnitId };
        }
        return force;
    }

    private static Point Locate(TrackMotion motion, double elapsedSeconds)
    {
        var (latitude, longitude) = motion.At(elapsedSeconds);
        return OwnPlatform.NewPoint(latitude, longitude);
    }

    /// <summary>
    /// A blue force as reported by a caller, and when it last checked in.
    /// </summary>
    private sealed record ReportedBlueForce(BlueForce Force, DateTime LastContactUtc);

    private static BlueForce ToBlueForce(UpdateBlueForce update) =>
        new()
        {
            Identity = update.Identity,
            LastContactTime = update.LastContactTime,
            Callsign = update.Callsign,
            Symbol = update.Symbol,
            BlueForceType = update.BlueForceType,
            PointLocation = update.PointLocation,
            MountHost = update.MountHost,
        };

    /// <summary>
    /// Flatten the identity oneof into a dictionary key. Two forces with
    /// the same digits in different identity types are different forces,
    /// so the case is part of the key.
    /// </summary>
    private static string KeyOf(Identity? identity) => identity?.TypeCase switch
    {
        Identity.TypeOneofCase.UuidIdentity => "uuid:" + identity.UuidIdentity,
        Identity.TypeOneofCase.StringIdentity => "str:" + identity.StringIdentity,
        Identity.TypeOneofCase.Int32Identity =>
            "i32:" + identity.Int32Identity.ToString(CultureInfo.InvariantCulture),
        Identity.TypeOneofCase.Int64Identity =>
            "i64:" + identity.Int64Identity.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static ResponseHeader OkHeader() => new() { Success = true };
}
