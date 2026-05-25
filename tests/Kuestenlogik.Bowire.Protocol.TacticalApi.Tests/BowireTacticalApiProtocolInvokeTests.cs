// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests;

/// <summary>
/// Exercises the failure paths of InvokeAsync / InvokeStreamAsync that
/// can be reached without an actual gRPC server — descriptor resolution,
/// method-shape validation, JSON parsing. The unreachable-server case is
/// covered separately and uses a deliberately invalid loopback address so
/// the test doesn't depend on a real Situation backend being available.
/// </summary>
public sealed class BowireTacticalApiProtocolInvokeTests
{
    private const string Url = "tacticalapi@127.0.0.1:50051";

    [Fact]
    public async Task Invoke_UnknownService_ReturnsNotFound()
    {
        var plugin = new BowireTacticalApiProtocol();

        var result = await plugin.InvokeAsync(
            Url, "UnknownService", "DoSomething",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("not-found", result.Status);
        Assert.Contains("UnknownService", result.Response, StringComparison.Ordinal);
        Assert.Contains("Situation", result.Response, StringComparison.Ordinal); // tells caller which services are real
    }

    [Fact]
    public async Task Invoke_UnknownMethod_ReturnsNotFound()
    {
        var plugin = new BowireTacticalApiProtocol();

        var result = await plugin.InvokeAsync(
            Url, "Situation", "DoNothing",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("not-found", result.Status);
        Assert.Contains("DoNothing", result.Response, StringComparison.Ordinal);
        // Error message lists the four real methods.
        Assert.Contains("SubscribeSituationObjectEvents", result.Response, StringComparison.Ordinal);
        Assert.Contains("GetSituationObjects", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_StreamingMethodOnUnary_ReturnsWrongShape()
    {
        // SubscribeSituationObjectEvents is server-streaming; calling it
        // through InvokeAsync (the unary entry point) must reject before
        // any network call so the operator gets a hint to use the stream
        // endpoint instead.
        var plugin = new BowireTacticalApiProtocol();

        var result = await plugin.InvokeAsync(
            Url, "Situation", "SubscribeSituationObjectEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("wrong-method-shape", result.Status);
        Assert.Contains("streaming", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invoke_MalformedJson_ReturnsBadRequest()
    {
        var plugin = new BowireTacticalApiProtocol();

        var result = await plugin.InvokeAsync(
            Url, "Situation", "GetSituationObjects",
            jsonMessages: ["{ this is not valid json"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("bad-request", result.Status);
        // Error names the expected input type so the operator can fix the
        // request shape.
        Assert.Contains("rheinmetall.tactical_api.v0.GetSituationObjectsRequest", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_UnreachableServer_SurfacesRpcException()
    {
        // 127.0.0.1:1 has no listener; the gRPC client should fail fast
        // and return an InvokeResult with the underlying StatusCode in
        // the .Status field rather than throwing.
        var plugin = new BowireTacticalApiProtocol();

        // Bound the connect attempt via the new invocationDeadlineSeconds
        // setting so the failure surface stays in our hands: gRPC's
        // exponential reconnect backoff can otherwise stretch a refused
        // connection into a 30+ second wait that looks like a hang.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // safety net for CI

        var result = await plugin.InvokeAsync(
            "tacticalapi@127.0.0.1:1", "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["invocationDeadlineSeconds"] = "3",
            },
            ct: cts.Token);

        // gRPC's failure for a connection-refused maps to Unavailable;
        // when the deadline trips first it surfaces as DeadlineExceeded.
        // Both are valid here — what we pin is that the failure is
        // visibly a gRPC-status failure, not silent 'OK' or one of our
        // pre-network sentinel strings. Allowlist explicit so a future
        // Grpc.Net.Client release that surfaces something else trips
        // the test on purpose.
        string[] expectedFailureStatuses =
        [
            "Unavailable", "DeadlineExceeded", "Cancelled", "Internal",
        ];
        Assert.Contains(result.Status, expectedFailureStatuses);
    }

    [Fact]
    public async Task InvokeStream_UnaryMethod_YieldsErrorAndStops()
    {
        var plugin = new BowireTacticalApiProtocol();

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            Url, "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        var only = Assert.Single(frames);
        Assert.Contains("error", only, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unary", only, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeStream_UnknownService_YieldsErrorAndStops()
    {
        var plugin = new BowireTacticalApiProtocol();

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            Url, "Mystery", "Tick",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        var only = Assert.Single(frames);
        Assert.Contains("error", only, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mystery", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_MalformedJson_YieldsErrorAndStops()
    {
        var plugin = new BowireTacticalApiProtocol();

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            Url, "Situation", "SubscribeSituationObjectEvents",
            jsonMessages: ["not json at all"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        var only = Assert.Single(frames);
        Assert.Contains("error", only, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rheinmetall.tactical_api.v0", only, StringComparison.Ordinal);
    }
}
