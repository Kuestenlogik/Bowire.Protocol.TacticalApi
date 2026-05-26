// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests;

/// <summary>
/// Broker-free unit coverage for <see cref="TacticalApiMockEmitter"/>.
/// The wire-level "does the gRPC call actually hit the server?" path
/// is exercised in the Testcontainers integration suite; here we
/// cover the recording-step filter + payload-decode precedence that's
/// the same regardless of server reachability.
/// </summary>
public sealed class TacticalApiMockEmitterTests
{
    [Fact]
    public async Task CanEmit_TrueWhenRecordingHasTacticalApiStep()
    {
        await using var emitter = new TacticalApiMockEmitter();
        var rec = new BowireRecording
        {
            Steps =
            {
                new BowireRecordingStep { Protocol = "rest" },
                new BowireRecordingStep { Protocol = "tacticalapi", ServerUrl = "tacticalapi@localhost:5118" },
            },
        };
        Assert.True(emitter.CanEmit(rec));
    }

    [Fact]
    public async Task CanEmit_FalseWhenRecordingHasNoTacticalApiStep()
    {
        await using var emitter = new TacticalApiMockEmitter();
        var rec = new BowireRecording
        {
            Steps =
            {
                new BowireRecordingStep { Protocol = "grpc" },
                new BowireRecordingStep { Protocol = "kafka" },
            },
        };
        Assert.False(emitter.CanEmit(rec));
    }

    [Fact]
    public async Task Id_is_tacticalapi()
    {
        await using var emitter = new TacticalApiMockEmitter();
        Assert.Equal("tacticalapi", emitter.Id);
    }

    [Fact]
    public void DecodeRequestBytes_prefers_RequestBinary_over_Body()
    {
        // Use the GetSituationObjectsRequest method as a concrete input
        // type for the descriptor.
        Assert.True(TacticalApiDescriptors.TryResolve(
            "Situation", "GetSituationObjects",
            out _, out var methodDesc));

        // Seed both fields. RequestBinary must win.
        var step = new BowireRecordingStep
        {
            Id = "s1",
            Protocol = "tacticalapi",
            Service = "Situation",
            Method = "GetSituationObjects",
            // Empty protobuf body — '{}' equivalent, which is what an
            // empty GetSituationObjectsRequest serializes to (0 bytes
            // since all fields are optional).
            RequestBinary = Convert.ToBase64String(Array.Empty<byte>()),
            Body = "{}", // ignored when RequestBinary is present
        };

        var bytes = TacticalApiMockEmitter.DecodeRequestBytes(step, methodDesc!, NullLogger.Instance);
        Assert.NotNull(bytes);
        Assert.Empty(bytes!); // matches the RequestBinary, not a re-parse of Body
    }

    [Fact]
    public void DecodeRequestBytes_falls_back_to_Body_via_JsonParser()
    {
        Assert.True(TacticalApiDescriptors.TryResolve(
            "Situation", "GetSituationObjects",
            out _, out var methodDesc));

        var step = new BowireRecordingStep
        {
            Id = "s2",
            Protocol = "tacticalapi",
            Service = "Situation",
            Method = "GetSituationObjects",
            Body = "{}",
        };

        var bytes = TacticalApiMockEmitter.DecodeRequestBytes(step, methodDesc!, NullLogger.Instance);
        // '{}' parses cleanly into the empty GetSituationObjectsRequest.
        Assert.NotNull(bytes);
    }

    [Fact]
    public void DecodeRequestBytes_returns_null_for_malformed_json_body()
    {
        Assert.True(TacticalApiDescriptors.TryResolve(
            "Situation", "GetSituationObjects",
            out _, out var methodDesc));

        var step = new BowireRecordingStep
        {
            Id = "s3",
            Protocol = "tacticalapi",
            Service = "Situation",
            Method = "GetSituationObjects",
            Body = "{not-json",
        };

        var bytes = TacticalApiMockEmitter.DecodeRequestBytes(step, methodDesc!, NullLogger.Instance);
        Assert.Null(bytes);
    }

    [Fact]
    public void DecodeRequestBytes_returns_null_when_both_payload_sources_missing()
    {
        Assert.True(TacticalApiDescriptors.TryResolve(
            "Situation", "GetSituationObjects",
            out _, out var methodDesc));

        var step = new BowireRecordingStep
        {
            Id = "empty",
            Protocol = "tacticalapi",
            Service = "Situation",
            Method = "GetSituationObjects",
        };

        var bytes = TacticalApiMockEmitter.DecodeRequestBytes(step, methodDesc!, NullLogger.Instance);
        Assert.Null(bytes);
    }

    // -------- CanEmit edge cases --------

    [Fact]
    public async Task CanEmit_ProtocolCasingIsIgnored()
    {
        await using var emitter = new TacticalApiMockEmitter();
        var rec = new BowireRecording
        {
            Steps = { new BowireRecordingStep { Protocol = "TacticalApi", ServerUrl = "grpc://localhost:5118" } },
        };
        Assert.True(emitter.CanEmit(rec));
    }

    [Fact]
    public async Task CanEmit_NullRecording_Throws()
    {
        await using var emitter = new TacticalApiMockEmitter();
        Assert.Throws<ArgumentNullException>(() => emitter.CanEmit(null!));
    }

    [Fact]
    public async Task Id_pins_to_protocol_constant()
    {
        await using var emitter = new TacticalApiMockEmitter();
        Assert.Equal(BowireTacticalApiProtocol.ProtocolId, emitter.Id);
    }

    // -------- StartAsync early-return paths --------

    [Fact]
    public async Task StartAsync_EmptyRecording_NoServerContact()
    {
        // No tacticalapi steps → StartAsync returns before opening a
        // GrpcChannel. Pass: completes without throwing or hanging.
        await using var emitter = new TacticalApiMockEmitter();
        var rec = new BowireRecording { Steps = { new BowireRecordingStep { Protocol = "rest" } } };

        await emitter.StartAsync(
            rec, new MockEmitterOptions(), NullLogger.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_NullRecording_Throws()
    {
        await using var emitter = new TacticalApiMockEmitter();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => emitter.StartAsync(null!, new MockEmitterOptions(), NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_NullOptions_Throws()
    {
        await using var emitter = new TacticalApiMockEmitter();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => emitter.StartAsync(new BowireRecording(), null!, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_NullLogger_Throws()
    {
        await using var emitter = new TacticalApiMockEmitter();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => emitter.StartAsync(new BowireRecording(), new MockEmitterOptions(), null!, CancellationToken.None));
    }

    // -------- DisposeAsync paths --------

    [Fact]
    public async Task DisposeAsync_BeforeStart_IsNoOp()
    {
        var emitter = new TacticalApiMockEmitter();
        await emitter.DisposeAsync();
        // Double-dispose stays a no-op via the _disposed guard.
        await emitter.DisposeAsync();
    }
}
