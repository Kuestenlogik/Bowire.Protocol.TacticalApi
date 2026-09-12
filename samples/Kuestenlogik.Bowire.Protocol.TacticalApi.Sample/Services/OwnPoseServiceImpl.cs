// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// The upstream <c>OwnPose</c> service: where this host is, and a way to
/// tell it otherwise.
/// </summary>
/// <remarks>
/// <para>
/// All three RPCs are implemented, including the write. <c>Situation</c>
/// deliberately leaves its mutations to the harbour demo, so until now
/// nothing in this sample could be changed from the workbench — every
/// button was a read. <c>UpdatePosition</c> is a single field and one
/// obvious consequence, which makes it the cheapest honest write the
/// sample can offer: send a fix, and the open
/// <c>SubscribePositionChangedEvents</c> stream reports it on the next
/// tick, along with the blue force this host reports as its own.
/// </para>
/// <para>
/// The stream emits the current position immediately on subscribe — the
/// schema says "initially, the current position is returned" — and then
/// one frame per exercise tick rather than only on change. A subscriber
/// that has to wait for movement to learn where something is cannot tell
/// a stationary platform from a broken feed.
/// </para>
/// </remarks>
internal sealed class OwnPoseServiceImpl(OwnPlatform platform)
    : OwnPose.OwnPoseBase, IScenarioTick
{
    private readonly SubscriberFanout<SubscribePositionEventsResponse> _fanout = new();

    public override Task<GetPositionResponse> GetPosition(
        GetPositionRequest request, ServerCallContext context)
        => Task.FromResult(new GetPositionResponse
        {
            Header = OkHeader(),
            Position = platform.Current(),
        });

    public override Task<UpdatePositionResponse> UpdatePosition(
        UpdatePositionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A refusal the sample can actually produce. TacticalAPI reports a
        // rejected operation in the response header, not as a gRPC error
        // (service_types.proto: success + error_message), and upstream's
        // reference client sets `SourceIdentifier` on every own-pose write.
        // Without a path like this one, nothing here ever answers
        // `success = false` and the whole refusal branch goes untested — which
        // is how the plugin came to report refusals as OK (#66).
        if (string.IsNullOrWhiteSpace(request.Position?.SourceIdentifier))
        {
            return Task.FromResult(new UpdatePositionResponse
            {
                Header = RefusedHeader(
                    "position.source_identifier is required — name the system reporting this fix."),
            });
        }

        platform.Apply(request.Position);

        // Push straight away as well as on the next tick: the operator
        // who just sent the fix is usually looking at the stream pane,
        // and a two-second gap there reads as "the write was ignored".
        _fanout.Broadcast(BuildFrame());
        return Task.FromResult(new UpdatePositionResponse { Header = OkHeader() });
    }

    public override async Task SubscribePositionChangedEvents(
        SubscribePositionEventsRequest request,
        IServerStreamWriter<SubscribePositionEventsResponse> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        using var subscription = _fanout.Subscribe();
        await responseStream.WriteAsync(BuildFrame()).ConfigureAwait(false);

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
    public void Tick(double elapsedSeconds) => _fanout.Broadcast(BuildFrame());

    private SubscribePositionEventsResponse BuildFrame() =>
        new() { Header = OkHeader(), Position = platform.Current() };

    private static ResponseHeader OkHeader() => new() { Success = true };

    /// <summary>
    /// The shape a TacticalAPI server uses to say no: a gRPC-level success
    /// whose header carries <c>success = false</c> and the reason.
    /// </summary>
    private static ResponseHeader RefusedHeader(string why) =>
        new() { Success = false, ErrorMessage = why };
}
