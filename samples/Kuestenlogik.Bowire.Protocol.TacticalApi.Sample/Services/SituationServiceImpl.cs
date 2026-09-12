// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Read-only TacticalAPI demo backend. Thirteen tracks in five groups
/// (see <see cref="SeededSituation"/>) move under their own motion
/// models, broadcast as a fresh snapshot on every exercise tick to every
/// active subscriber.
///
/// Implements two RPCs from the upstream <c>Situation</c> service:
///   - <see cref="GetSituationObjects"/> — current snapshot, unary.
///   - <see cref="SubscribeSituationObjectEvents"/> — server-stream
///     of snapshots, one per tick while the subscriber holds the
///     channel open.
///
/// The mutation RPCs (AddOrUpdate / Delete) stay deliberately
/// unimplemented — the Harbor sample covers those, and the write path
/// this sample does demonstrate lives on <see cref="OwnPoseServiceImpl"/>
/// and <see cref="BlueForceTrackingServiceImpl"/>, where a single field
/// has a consequence you can watch.
/// </summary>
internal sealed class SituationServiceImpl : Situation.SituationBase, IScenarioTick
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SituationObject> _objects;
    private readonly Dictionary<string, TrackMotion> _motions;
    private readonly SubscriberFanout<SubscribeSituationObjectEventsResponse> _fanout = new();

    public SituationServiceImpl()
    {
        // Each track carries its own motion now, so nothing has to be
        // recovered from the seeded coordinate: the old code re-derived
        // a bearing from the starting position, which only worked while
        // every track was on the same circle around the same centre.
        (_objects, _motions) = SeededSituation.Build();
    }

    public override Task<GetSituationObjectsResponse> GetSituationObjects(
        GetSituationObjectsRequest request, ServerCallContext context)
    {
        var response = new GetSituationObjectsResponse { Header = OkHeader() };
        lock (_gate)
        {
            foreach (var obj in _objects.Values) response.SituationObjects.Add(obj);
        }
        return Task.FromResult(response);
    }

    public override async Task SubscribeSituationObjectEvents(
        SubscribeSituationObjectEventsRequest request,
        IServerStreamWriter<SubscribeSituationObjectEventsResponse> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        using var subscription = _fanout.Subscribe();

        // Spec: "all non-deleted existing situation objects are returned
        // for every call". Emit the current snapshot synchronously so
        // the subscriber has data before the next tick.
        await responseStream.WriteAsync(BuildSnapshot()).ConfigureAwait(false);

        try
        {
            await foreach (var update in subscription.Reader
                .ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(update).ConfigureAwait(false);
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
        AdvanceTracks(elapsedSeconds);
        _fanout.Broadcast(BuildSnapshot());
    }

    private void AdvanceTracks(double elapsedSeconds)
    {
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        lock (_gate)
        {
            foreach (var (id, obj) in _objects)
            {
                if (!_motions.TryGetValue(id, out var motion)) continue;
                var point = obj.Symbol?.Location?.Content?.Point;
                if (point?.GeoPoint is null) continue;

                // Evaluated from elapsed time, not integrated per tick:
                // a subscriber joining late sees the same positions as
                // one listening from the start, and a missed tick cannot
                // walk a convoy off its road.
                var (latitude, longitude) = motion.At(elapsedSeconds);
                point.GeoPoint.LatitudeCoordinate = latitude;
                point.GeoPoint.LongitudeCoordinate = longitude;
                point.LocationTime = now;
            }
        }
    }

    private SubscribeSituationObjectEventsResponse BuildSnapshot()
    {
        var snapshot = new SubscribeSituationObjectEventsResponse { Header = OkHeader() };
        lock (_gate)
        {
            foreach (var obj in _objects.Values) snapshot.SituationObjects.Add(obj);
        }
        return snapshot;
    }

    private static ResponseHeader OkHeader() => new() { Success = true };
}
