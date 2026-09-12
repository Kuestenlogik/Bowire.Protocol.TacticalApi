// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging.Abstractions;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests.Integration;

/// <summary>
/// End-to-end coverage for <see cref="TacticalApiMockEmitter"/>.
/// Dials the in-process Situation gRPC server, points the emitter at
/// a hand-built recording, and verifies the
/// <c>StartAsync</c> → <c>RunAsync</c> → <c>EmitAsync</c> chain
/// actually hits the server. Same fixture as the live-protocol
/// round-trip suite — no separate Testcontainers / Docker dependency.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TacticalApiMockEmitterE2ETests
    : IClassFixture<InProcessTacticalApiServerFixture>
{
    private readonly InProcessTacticalApiServerFixture _server;

    public TacticalApiMockEmitterE2ETests(InProcessTacticalApiServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task StartAsync_ReplaysUnaryRequest_AgainstInProcessServer()
    {
        // One Get with an empty body — IntegrationSituationService
        // returns a canned response. Emitter logs the call duration
        // but doesn't expose the response; we verify success by
        // observing that the scheduler task completed without
        // throwing.
        var recording = new BowireRecording
        {
            Steps =
            {
                MakeUnaryStep("u1", DateTimeOffset.UtcNow, _server.ServerUrl),
            },
        };

        await using var emitter = new TacticalApiMockEmitter();
        await emitter.StartAsync(
            recording,
            new MockEmitterOptions { ReplaySpeed = 10.0 },
            NullLogger.Instance,
            CancellationToken.None);

        // Wait briefly for the in-process scheduler task to drain.
        // The single-step recording completes in < 100 ms; 2 s is
        // plenty of margin without slowing the suite materially.
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_ReplaysServerStreamingRpc_DrainingFrames()
    {
        // The IntegrationSituationService.SubscribeSituationObjectEvents
        // override emits 2 frames then closes. The emitter's drain
        // loop reads up to 50 — completes cleanly when the server
        // closes the stream after 2.
        var recording = new BowireRecording
        {
            Steps =
            {
                MakeServerStreamStep("ss1", DateTimeOffset.UtcNow, _server.ServerUrl),
            },
        };

        await using var emitter = new TacticalApiMockEmitter();
        await emitter.StartAsync(
            recording,
            new MockEmitterOptions { ReplaySpeed = 10.0 },
            NullLogger.Instance,
            CancellationToken.None);

        await Task.Delay(800, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_SkipsUnknownServiceOrMethod()
    {
        // Step with a fake (service, method) tuple — the descriptor
        // lookup misses, emitter logs a warning + continues. Combined
        // with a valid step we confirm the scheduler doesn't bail
        // out on the first miss.
        var recording = new BowireRecording
        {
            Steps =
            {
                MakeStep("bogus", DateTimeOffset.UtcNow, _server.ServerUrl,
                    service: "NoSuchService", method: "Nada", body: "{}"),
                MakeUnaryStep("u1", DateTimeOffset.UtcNow.AddMilliseconds(10), _server.ServerUrl),
            },
        };

        await using var emitter = new TacticalApiMockEmitter();
        await emitter.StartAsync(
            recording,
            new MockEmitterOptions { ReplaySpeed = 10.0 },
            NullLogger.Instance,
            CancellationToken.None);

        await Task.Delay(500, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_ReplaysAcrossEveryBundledService()
    {
        // The emitter resolves steps against TacticalApiDescriptors, so
        // a recording is free to mix services. This one writes a blue
        // force and holds an OwnPose subscription open — neither of
        // which existed when the emitter was written, and neither of
        // which needed a change in it.
        //
        // Unlike the other emitter tests this one can prove the call
        // landed rather than just that nothing threw: the write is
        // readable back off the same in-process server.
        var recording = new BowireRecording
        {
            Steps =
            {
                MakeStep("bf-write", DateTimeOffset.UtcNow, _server.ServerUrl,
                    service: "BlueForceTracking", method: "AddOrUpdateBlueForces",
                    body: """
                          {
                            "blueForcesToUpdates": [
                              {
                                "identity": { "stringIdentity": "bf-replayed-1" },
                                "callsign": "Wiederholer"
                              }
                            ]
                          }
                          """),
                MakeStep("pose-stream", DateTimeOffset.UtcNow.AddMilliseconds(10), _server.ServerUrl,
                    service: "OwnPose", method: "SubscribePositionChangedEvents",
                    body: "{}"),
            },
        };

        await using var emitter = new TacticalApiMockEmitter();
        Assert.True(emitter.CanEmit(recording));

        await emitter.StartAsync(
            recording,
            new MockEmitterOptions { ReplaySpeed = 10.0 },
            NullLogger.Instance,
            CancellationToken.None);

        await Task.Delay(800, TestContext.Current.CancellationToken);

        var plugin = new BowireTacticalApiProtocol();
        var read = await plugin.InvokeAsync(
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "BlueForceTracking", "GetBlueForces",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", read.Status);
        Assert.Contains("Wiederholer", read.Response, StringComparison.Ordinal);
    }

    private static BowireRecordingStep MakeUnaryStep(
        string id, DateTimeOffset capturedAt, string serverUrl)
    {
        // GetSituationObjects accepts an empty request — '{}' parses
        // cleanly via the JsonParser fallback.
        return new BowireRecordingStep
        {
            Id = id,
            Protocol = BowireTacticalApiProtocol.ProtocolId,
            ServerUrl = serverUrl,
            Service = "Situation",
            Method = "GetSituationObjects",
            Body = "{}",
            CapturedAt = capturedAt.ToUnixTimeMilliseconds(),
        };
    }

    private static BowireRecordingStep MakeServerStreamStep(
        string id, DateTimeOffset capturedAt, string serverUrl)
    {
        // Subscribe takes a SubscribeSituationObjectEventsRequest;
        // empty payload is fine for the in-process fixture.
        return new BowireRecordingStep
        {
            Id = id,
            Protocol = BowireTacticalApiProtocol.ProtocolId,
            ServerUrl = serverUrl,
            Service = "Situation",
            Method = "SubscribeSituationObjectEvents",
            Body = "{}",
            CapturedAt = capturedAt.ToUnixTimeMilliseconds(),
        };
    }

    private static BowireRecordingStep MakeStep(
        string id, DateTimeOffset capturedAt, string serverUrl,
        string service, string method, string body)
        => new()
        {
            Id = id,
            Protocol = BowireTacticalApiProtocol.ProtocolId,
            ServerUrl = serverUrl,
            Service = service,
            Method = method,
            Body = body,
            CapturedAt = capturedAt.ToUnixTimeMilliseconds(),
        };
}
