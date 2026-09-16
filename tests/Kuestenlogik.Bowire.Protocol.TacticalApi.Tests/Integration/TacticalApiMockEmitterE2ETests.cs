// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;
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

        // Completion is the scheduler task itself: it ends when the last
        // step has gone out, so the test waits for a fact, not a delay.
        await emitter.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await emitter.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await emitter.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await emitter.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var plugin = new BowireTacticalApiProtocol();
        var read = await plugin.InvokeAsync(
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "BlueForceTracking", "GetBlueForces",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", read.Status);
        Assert.Contains("Wiederholer", read.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_CountsAReplayTheServerRefused()
    {
        // gRPC answers OK and the body carries success = false. The emitter
        // used to log that as a call that took N ms; now it is a warning
        // with the server's reason, and the count is readable so a
        // recording refused on every loop is not a secret (#66 for replay).
        var recording = new BowireRecording
        {
            Steps =
            {
                MakeStep("refused", DateTimeOffset.UtcNow, _server.ServerUrl,
                    service: "Situation", method: "AddOrUpdateSituationObjects",
                    body: """
                          {
                            "situationObjects": [
                              { "symbol": { "identity": { "uuidIdentity": "replay-no-reporter" } } }
                            ]
                          }
                          """),
                MakeUnaryStep("fine", DateTimeOffset.UtcNow.AddMilliseconds(10), _server.ServerUrl),
            },
        };

        var log = new CapturingLogger();
        await using var emitter = new TacticalApiMockEmitter();
        await emitter.StartAsync(
            recording, new MockEmitterOptions { ReplaySpeed = 10.0 }, log, CancellationToken.None);
        await emitter.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, emitter.RefusedSteps);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(IntegrationSituationService.MissingReporterMessage, warning.Message, StringComparison.Ordinal);
        Assert.Contains("refused", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_StopsDrainingAtARefusingFrame()
    {
        var recording = new BowireRecording
        {
            Steps =
            {
                MakeStep("refused-stream", DateTimeOffset.UtcNow, _server.ServerUrl,
                    service: "OwnPose", method: "SubscribePositionChangedEvents", body: "{}",
                    metadata: new Dictionary<string, string> { [IntegrationOwnPoseService.RefuseHeader] = "1" }),
            },
        };

        var log = new CapturingLogger();
        await using var emitter = new TacticalApiMockEmitter();
        await emitter.StartAsync(
            recording, new MockEmitterOptions { ReplaySpeed = 10.0 }, log, CancellationToken.None);
        await emitter.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, emitter.RefusedSteps);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(IntegrationOwnPoseService.RefusalMessage, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_ReplaysOverGrpcWeb_WhenTheRecordingWasCapturedThatWay()
    {
        // A recording captured against TacNet's :4268 carries useGrpcWeb in
        // its metadata. The emitter used to open a bare channel regardless,
        // so every step failed on the HTTP/1.1 port before reaching a
        // service. The write landing on the web-only port proves the
        // channel took the recorded transport.
        var recording = new BowireRecording
        {
            Steps =
            {
                MakeStep("bf-web", DateTimeOffset.UtcNow, _server.GrpcWebServerUrl,
                    service: "BlueForceTracking", method: "AddOrUpdateBlueForces",
                    body: """
                          {
                            "blueForcesToUpdates": [
                              { "identity": { "stringIdentity": "bf-replayed-web" }, "callsign": "Webwiederholer" }
                            ]
                          }
                          """,
                    metadata: new Dictionary<string, string> { [GrpcTransport.UseGrpcWebKey] = "true" }),
            },
        };

        var log = new CapturingLogger();
        await using var emitter = new TacticalApiMockEmitter();
        await emitter.StartAsync(
            recording, new MockEmitterOptions { ReplaySpeed = 10.0 }, log, CancellationToken.None);
        await emitter.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(0, emitter.RefusedSteps);

        var plugin = new BowireTacticalApiProtocol();
        var read = await plugin.InvokeAsync(
            _server.ServerUrl.Replace("http://", "grpc://", StringComparison.Ordinal),
            "BlueForceTracking", "GetBlueForces",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", read.Status);
        Assert.Contains("Webwiederholer", read.Response, StringComparison.Ordinal);
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
        string service, string method, string body,
        Dictionary<string, string>? metadata = null)
        => new()
        {
            Id = id,
            Protocol = BowireTacticalApiProtocol.ProtocolId,
            ServerUrl = serverUrl,
            Service = service,
            Method = method,
            Body = body,
            Metadata = metadata,
            CapturedAt = capturedAt.ToUnixTimeMilliseconds(),
        };

    /// <summary>
    /// The emitter reports through its logger and nowhere else, so the
    /// tests read the log. Warnings are what the refusal path writes.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
