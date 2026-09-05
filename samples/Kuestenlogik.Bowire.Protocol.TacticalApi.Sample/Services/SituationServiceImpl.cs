// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Read-only TacticalAPI demo backend. Thirteen tracks in five groups
/// (see <see cref="SeededSituation"/>) move under their own motion
/// models, broadcast as a fresh snapshot every two seconds to every
/// active subscriber.
///
/// Implements two RPCs from the upstream <c>Situation</c> service:
///   - <see cref="GetSituationObjects"/> — current snapshot, unary.
///   - <see cref="SubscribeSituationObjectEvents"/> — server-stream
///     of snapshots, one every ~2 s while the subscriber holds the
///     channel open.
///
/// The mutation RPCs (AddOrUpdate / Delete) stay deliberately
/// unimplemented — the Harbor sample covers those; the
/// RadarSweep canonical-demo's job is to show the minimal
/// read-side surface the plugin probes for.
/// </summary>
internal sealed class SituationServiceImpl : Situation.SituationBase, IDisposable
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, SituationObject> _objects;
    private readonly Dictionary<string, TrackMotion> _motions;
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly List<ChannelWriter<SubscribeSituationObjectEventsResponse>> _subscribers = [];
    private readonly CancellationTokenSource _moverCts = new();
    private readonly Task _moverTask;

    public SituationServiceImpl()
    {
        // Each track carries its own motion now, so nothing has to be
        // recovered from the seeded coordinate: the old code re-derived
        // a bearing from the starting position, which only worked while
        // every track was on the same circle around the same centre.
        (_objects, _motions) = SeededSituation.Build();
        _moverTask = Task.Run(() => RunMoverAsync(_moverCts.Token));
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
        var channel = Channel.CreateUnbounded<SubscribeSituationObjectEventsResponse>(
            new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) { _subscribers.Add(channel.Writer); }

        // Spec: "all non-deleted existing situation objects are returned
        // for every call". Emit the current snapshot synchronously so
        // the subscriber has data before the mover's next tick.
        await responseStream.WriteAsync(BuildSnapshotLocked()).ConfigureAwait(false);

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(update).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* subscriber closed — clean exit */ }
        finally
        {
            lock (_gate) { _subscribers.Remove(channel.Writer); }
            channel.Writer.TryComplete();
        }
    }

    private async Task RunMoverAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TickPeriod, ct).ConfigureAwait(false);
                AdvanceTracks();
                BroadcastSnapshot();
            }
        }
        catch (OperationCanceledException) { /* service shutdown */ }
    }

    private void AdvanceTracks()
    {
        var elapsedSeconds = (DateTime.UtcNow - _startUtc).TotalSeconds;
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

    private SubscribeSituationObjectEventsResponse BuildSnapshotLocked()
    {
        var snapshot = new SubscribeSituationObjectEventsResponse { Header = OkHeader() };
        foreach (var obj in _objects.Values) snapshot.SituationObjects.Add(obj);
        return snapshot;
    }

    private void BroadcastSnapshot()
    {
        SubscribeSituationObjectEventsResponse frame;
        lock (_gate) { frame = BuildSnapshotLocked(); }
        ChannelWriter<SubscribeSituationObjectEventsResponse>[] snapshot;
        lock (_gate) { snapshot = [.. _subscribers]; }
        foreach (var writer in snapshot)
        {
            // Non-blocking; closed channels (subscriber cancelled
            // mid-call) get cleaned up by SubscribeSituationObjectEvents'
            // finally block.
            writer.TryWrite(frame);
        }
    }

    public void Dispose()
    {
        _moverCts.Cancel();
        try { _moverTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { /* expected */ }
        catch (AggregateException) { /* swallow — shutdown */ }
        _moverCts.Dispose();
    }

    private static ResponseHeader OkHeader() => new() { Success = true };
}
