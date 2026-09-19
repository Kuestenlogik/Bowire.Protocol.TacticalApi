// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// The upstream <c>Situation</c> service. Thirteen tracks in five groups
/// (see <see cref="SeededSituation"/>) move under their own motion
/// models, eight control measures (see <see cref="SeededOverlay"/>) lie
/// still over them, and all of it is broadcast as a fresh snapshot on
/// every exercise tick to every active subscriber — and the operator can
/// add, change and delete symbols beside them.
///
/// All four RPCs are implemented:
///   - <see cref="GetSituationObjects"/> — current snapshot, unary.
///   - <see cref="SubscribeSituationObjectEvents"/> — server-stream
///     of snapshots, one per tick while the subscriber holds the
///     channel open.
///   - <see cref="AddOrUpdateSituationObjects"/> — create a symbol, or
///     change some of an existing one's properties. Sparse, the way the
///     contract says: only the properties sent are touched.
///   - <see cref="DeleteSituationObjects"/> — retire a symbol; it goes
///     out once more flagged <c>is_deleted</c> and is then gone.
/// </summary>
/// <remarks>
/// <para>
/// The write side exists mostly for what it refuses. Every write and
/// delete message in the contract marks <c>identity</c>, <c>reporter</c>
/// and <c>reporting_time</c> as <c>Required:</c>, and a real server
/// answers a request missing one of them with an ordinary gRPC
/// <c>OK</c> whose header says <c>success = false</c>. This server does
/// the same (#66, #69), so an operator who copies the request bodies in
/// the README and drops the envelope sees the refusal here rather than
/// on the day it counts.
/// </para>
/// <para>
/// All eleven object types in the <c>UpdateSituationObject</c> oneof are
/// written the same way — symbols, action tasks and events, routes,
/// organisation units, the six document kinds. Not through eleven copies
/// of the same code: the contract's update and served messages are mirror
/// images, and <see cref="SituationObjectMapper"/> walks that mirror once,
/// by protobuf reflection. What an update means — sparse, only the
/// properties sent — is the same for every type.
/// </para>
/// <para>
/// <c>expiry_time</c> is honoured: "Expired symbols are automatically
/// marked as deleted", says the contract, and the tick does that — an
/// expired symbol goes out once more flagged <c>is_deleted</c>, stamped
/// by <see cref="ExpiryReporter"/>, and is then gone, the same path a
/// delete takes. Every frame carries clones of the served objects: the
/// tick mutates the stored ones under the lock, and a frame that held
/// the same instances was serialised outside it, so a subscriber could
/// see a convoy with half its coordinates from the last tick.
/// </para>
/// </remarks>
internal sealed class SituationServiceImpl : Situation.SituationBase, IScenarioTick
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SituationObject> _objects;
    private readonly Dictionary<string, TrackMotion> _motions;
    private readonly SubscriberFanout<SubscribeSituationObjectEventsResponse> _fanout = new();

    /// <summary>Who an expiry sweep reports as having retired a symbol.</summary>
    internal static readonly Identity ExpiryReporter = new() { StringIdentity = "Sample.Expiry" };

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
            foreach (var obj in _objects.Values) response.SituationObjects.Add(obj.Clone());
        }
        return Task.FromResult(response);
    }

    public override Task<AddOrUpdateSituationObjectsResponse> AddOrUpdateSituationObjects(
        AddOrUpdateSituationObjectsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refusal = TryAddOrUpdate(request.SituationObjects);
        if (refusal is null)
        {
            // Straight away as well as on the next tick: the operator who
            // just sent the write is looking at the stream pane, and a
            // two-second gap there reads as "ignored".
            _fanout.Broadcast(BuildSnapshot());
        }

        return Task.FromResult(new AddOrUpdateSituationObjectsResponse
        {
            Header = refusal is null ? OkHeader() : RefusedHeader(refusal),
        });
    }

    public override Task<DeleteSituationObjectsResponse> DeleteSituationObjects(
        DeleteSituationObjectsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refusal = TryDelete(request.SituationObjects, out var retired);
        if (refusal is null)
        {
            // "Marks a situation object as deleted": subscribers see it once
            // more with the flag set, and never again. A client that ignores
            // the flag keeps drawing a contact nobody is reporting.
            var frame = BuildSnapshot();
            frame.SituationObjects.Add(retired);
            _fanout.Broadcast(frame);
        }

        return Task.FromResult(new DeleteSituationObjectsResponse
        {
            Header = refusal is null ? OkHeader() : RefusedHeader(refusal),
        });
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
    public void Tick(double elapsedSeconds) => TickAt(elapsedSeconds, DateTime.UtcNow);

    /// <summary>
    /// <see cref="Tick"/> with the clock passed in, so a test can expire a
    /// symbol without waiting for its <c>expiry_time</c> to come round.
    /// </summary>
    internal void TickAt(double elapsedSeconds, DateTime nowUtc)
    {
        AdvanceTracks(elapsedSeconds, nowUtc);
        var frame = BuildSnapshot();
        frame.SituationObjects.Add(SweepExpired(nowUtc));
        _fanout.Broadcast(frame);
    }

    /// <summary>
    /// Retire every symbol whose <c>expiry_time</c> has passed, and hand
    /// the retired ones back flagged <c>is_deleted</c> so the frame can
    /// carry them once. Same shape as a delete, with the sweep as reporter.
    /// </summary>
    private List<SituationObject> SweepExpired(DateTime nowUtc)
    {
        var retired = new List<SituationObject>();
        lock (_gate)
        {
            foreach (var (key, obj) in _objects)
            {
                if (SituationObjectMapper.ExpiryOf(obj) is not { } expiry) continue;
                if (expiry.ToDateTime() > nowUtc) continue;
                retired.Add(obj);
                _motions.Remove(key);
            }
            foreach (var gone in retired)
            {
                _objects.Remove(IdentityKeys.Of(SituationObjectMapper.IdentityOf(gone)));
                gone.IsDeleted = new DataPropertyBool
                {
                    CreationMetaData = new CreationMetaData
                    {
                        CreationTime = Timestamp.FromDateTime(nowUtc),
                        CreatorIdentity = ExpiryReporter,
                    },
                    Content = true,
                };
            }
        }
        return retired;
    }

    // ---- the write side ------------------------------------------------------

    /// <summary>
    /// Apply the updates, or say why not. Everything is checked before
    /// anything is applied: a request that is half-applied and then refused
    /// leaves the operator unsure which half landed, and the contract has no
    /// partial-success shape — one header per request.
    /// </summary>
    /// <returns>The refusal to send, or <c>null</c> when every update landed.</returns>
    private string? TryAddOrUpdate(RepeatedField<UpdateSituationObject> requested)
    {
        var updates = new List<(string CaseName, IMessage Payload, SituationObjectMapper.Envelope Envelope)>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            var at = $"situation_objects[{i}]";

            // "Note: only one of the situation object types is supported at
            // a time!" — protobuf already guarantees at most one; none at
            // all is the case a hand-written request produces.
            if (SituationObjectMapper.Unwrap(requested[i]) is not var (caseName, payload))
                return $"{at}: set exactly one of the situation object types (symbol, action_task, route, organization_unit, …).";

            var envelope = SituationObjectMapper.EnvelopeOf(payload);
            if (CheckEnvelope(at, envelope.Identity, envelope.Reporter, envelope.ReportingTime) is { } incomplete)
                return incomplete;
            updates.Add((caseName, payload, envelope));
        }

        lock (_gate)
        {
            // The identity checks need the store, so they run under the lock
            // — still before the first property is written.
            foreach (var (caseName, _, envelope) in updates)
            {
                var key = IdentityKeys.Of(envelope.Identity);
                if (_objects.TryGetValue(key, out var existing))
                {
                    // One type per object, for its whole life: a symbol does
                    // not become a route by being updated as one.
                    if (SituationObjectMapper.CaseName(existing) is { } existingCase && existingCase != caseName)
                        return $"{Describe(envelope.Identity)} is a {existingCase}, not a {caseName} — the contract allows one type per object.";
                }
                else if (IdentityKeys.IsInternalOnly(envelope.Identity))
                {
                    // types.proto: the int32 / int64 identities are "not for
                    // external use to create new objects".
                    return $"No situation object with identity {Describe(envelope.Identity)}, and an int32 / int64 identity cannot create one — use a uuid_identity or string_identity.";
                }
            }

            foreach (var (caseName, payload, envelope) in updates)
            {
                var key = IdentityKeys.Of(envelope.Identity);
                if (!_objects.TryGetValue(key, out var target))
                {
                    target = SituationObjectMapper.CreateServed(caseName, payload);
                    _objects[key] = target;
                }

                SituationObjectMapper.ApplySparse(target, payload);

                // An operator who places a seeded track somewhere else has
                // taken it over, the same way an own-pose fix takes over
                // from dead reckoning. Leaving the motion in place would
                // move the symbol straight back on the next tick.
                if (SituationObjectMapper.Sets(payload, "location")) _motions.Remove(key);
            }
        }

        return null;
    }

    /// <summary>
    /// Retire the named objects, or say why not. The retired objects come
    /// back flagged <c>is_deleted</c> — stamped with who retired them and
    /// when — so the caller can hand them to subscribers one last time.
    /// </summary>
    private string? TryDelete(
        RepeatedField<DeleteSituationObject> requested, out List<SituationObject> retired)
    {
        retired = [];
        for (var i = 0; i < requested.Count; i++)
        {
            var delete = requested[i];
            if (CheckEnvelope($"situation_objects[{i}]", delete.Identity, delete.Reporter, delete.ReportingTime) is { } incomplete)
                return incomplete;
        }

        lock (_gate)
        {
            foreach (var delete in requested)
            {
                if (!_objects.ContainsKey(IdentityKeys.Of(delete.Identity)))
                    return $"No situation object with identity {Describe(delete.Identity)}.";
            }

            foreach (var delete in requested)
            {
                var key = IdentityKeys.Of(delete.Identity);
                _objects.Remove(key, out var gone);
                _motions.Remove(key);

                gone!.IsDeleted = new DataPropertyBool
                {
                    CreationMetaData = new CreationMetaData
                    {
                        CreationTime = delete.ReportingTime,
                        CreatorIdentity = delete.Reporter,
                    },
                    Content = true,
                };
                retired.Add(gone);
            }
        }

        return null;
    }

    /// <summary>
    /// The three fields every write and delete message marks
    /// <c>Required:</c>. Returns the refusal, or <c>null</c> when the
    /// envelope is complete.
    /// </summary>
    private static string? CheckEnvelope(
        string at, Identity? identity, Identity? reporter, Timestamp? reportingTime)
    {
        if (IdentityKeys.Of(identity).Length == 0)
            return $"{at}: identity is required — the object's own uuid_identity or string_identity.";
        if (IdentityKeys.Of(reporter).Length == 0)
            return $"{at}: reporter is required — the ID of who generated the change. Use \"TacticalAPI\" if in doubt.";
        if (reportingTime is null)
            return $"{at}: reporting_time is required — the UTC time of the change.";
        return null;
    }

    private static string Describe(Identity? identity) =>
        identity?.TypeCase switch
        {
            Identity.TypeOneofCase.UuidIdentity => $"uuid '{identity.UuidIdentity}'",
            Identity.TypeOneofCase.StringIdentity => $"string '{identity.StringIdentity}'",
            Identity.TypeOneofCase.Int32Identity => $"int32 {identity.Int32Identity}",
            Identity.TypeOneofCase.Int64Identity => $"int64 {identity.Int64Identity}",
            _ => "(none)",
        };

    private void AdvanceTracks(double elapsedSeconds, DateTime nowUtc)
    {
        var now = Timestamp.FromDateTime(nowUtc);
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

    /// <summary>
    /// The served objects as they stand, cloned under the lock. The frame
    /// is serialised by the subscriber pumps outside it, and the tick
    /// writes coordinates into the stored objects in place — the same
    /// instance in both would let a frame carry a position from two ticks.
    /// </summary>
    private SubscribeSituationObjectEventsResponse BuildSnapshot()
    {
        var snapshot = new SubscribeSituationObjectEventsResponse { Header = OkHeader() };
        lock (_gate)
        {
            foreach (var obj in _objects.Values) snapshot.SituationObjects.Add(obj.Clone());
        }
        return snapshot;
    }

    private static ResponseHeader OkHeader() => new() { Success = true };

    /// <summary>
    /// The shape a TacticalAPI server uses to say no: a gRPC-level success
    /// whose header carries <c>success = false</c> and the reason.
    /// </summary>
    private static ResponseHeader RefusedHeader(string why) =>
        new() { Success = false, ErrorMessage = why };
}
