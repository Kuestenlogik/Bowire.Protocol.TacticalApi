// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Rheinmetall.TacticalApi.V0;

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
        // Tells the caller which services are real — all of them, not
        // just the first .proto the plugin happens to look at.
        Assert.Contains("Situation", result.Response, StringComparison.Ordinal);
        Assert.Contains("OwnPose", result.Response, StringComparison.Ordinal);
        Assert.Contains("BlueForceTracking", result.Response, StringComparison.Ordinal);
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

    [Fact]
    public async Task Invoke_UnknownMethodOnBlueForceTracking_ReturnsNotFound()
    {
        // Resolution has to descend into the right service: the method
        // named here exists on OwnPose, not on BlueForceTracking, so a
        // resolver that searched every service's methods flat would
        // wrongly accept it.
        var plugin = new BowireTacticalApiProtocol();

        var result = await plugin.InvokeAsync(
            Url, "BlueForceTracking", "GetPosition",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("not-found", result.Status);
        Assert.Contains("GetBlueForces", result.Response, StringComparison.Ordinal);
        Assert.Contains("AddOrUpdateBlueForces", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_SubscribePositionChangedEventsOnUnary_ReturnsWrongShape()
    {
        // Same shape-check as Situation's subscribe pump, on the service
        // that arrived with the position / blue-force upstream commit.
        var plugin = new BowireTacticalApiProtocol();

        var result = await plugin.InvokeAsync(
            Url, "OwnPose", "SubscribePositionChangedEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("wrong-method-shape", result.Status);
        Assert.Contains("streaming", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invoke_MalformedJsonForUpdatePosition_NamesTheRequestType()
    {
        var plugin = new BowireTacticalApiProtocol();

        var result = await plugin.InvokeAsync(
            Url, "OwnPose", "UpdatePosition",
            jsonMessages: ["{ still not json"], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("bad-request", result.Status);
        Assert.Contains(
            "rheinmetall.tactical_api.v0.UpdatePositionRequest",
            result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_AddOrUpdateBlueForces_YieldsErrorAndStops()
    {
        // Unary method on the streaming entry point — the mirror of
        // Invoke_SubscribePositionChangedEventsOnUnary.
        var plugin = new BowireTacticalApiProtocol();

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            Url, "BlueForceTracking", "AddOrUpdateBlueForces",
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
    public async Task Invoke_UnloadableMtlsMarker_IsReportedBeforeAnyDial()
    {
        // The operator's own PEM material did not load. That is a
        // configuration error and the result has to say so — not
        // "Unavailable" from a dial that never had a certificate to present.
        var plugin = new BowireTacticalApiProtocol();
        var result = await plugin.InvokeAsync(
            Url, "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: BrokenMarker(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(BowireTacticalApiProtocol.BadTransportConfigStatus, result.Status);
        Assert.Contains(Kuestenlogik.Bowire.Auth.MtlsConfig.MtlsMarkerKey, result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_UnloadableMtlsMarker_YieldsOneErrorFrame()
    {
        var plugin = new BowireTacticalApiProtocol();
        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            Url, "Situation", "SubscribeSituationObjectEvents",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: BrokenMarker(), ct: TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        var only = Assert.Single(frames);
        Assert.Contains(BowireTacticalApiProtocol.BadTransportConfigStatus, only, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> BrokenMarker() => new(StringComparer.Ordinal)
    {
        [Kuestenlogik.Bowire.Auth.MtlsConfig.MtlsMarkerKey] =
            """{ "certificate": "-----BEGIN CERTIFICATE-----\nkaputt\n-----END CERTIFICATE-----", "privateKey": "-----BEGIN PRIVATE KEY-----\nkaputt\n-----END PRIVATE KEY-----" }""",
    };

    [Fact]
    public void CountObjects_sums_the_repeated_fields_and_is_null_without_any()
    {
        var three = new GetSituationObjectsResponse();
        three.SituationObjects.Add(new SituationObject());
        three.SituationObjects.Add(new SituationObject());
        three.SituationObjects.Add(new SituationObject());
        Assert.Equal(3, BowireTacticalApiProtocol.CountObjects(three));

        // A repeated field with nothing in it is a count of zero, not an
        // absent key: "the server has no objects" is an answer.
        Assert.Equal(0, BowireTacticalApiProtocol.CountObjects(new GetBlueForcesResponse()));

        // No repeated field at all — header and nothing else.
        Assert.Null(BowireTacticalApiProtocol.CountObjects(new UpdatePositionResponse()));
    }

    [Fact]
    public async Task Invoke_says_so_on_the_result_when_certificate_validation_is_off()
    {
        // Nothing listens on 127.0.0.1:1, so the call fails — and the
        // warning is on that result too: it is about what the call was
        // willing to accept, not about what it got.
        var plugin = new BowireTacticalApiProtocol();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var result = await plugin.InvokeAsync(
            "tacticalapi@127.0.0.1:1", "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [GrpcTransport.AllowSelfSignedCertsKey] = "true",
                [GrpcTransport.InvocationDeadlineSecondsKey] = "3",
            },
            ct: cts.Token);

        Assert.NotEqual("OK", result.Status);
        var warning = result.Metadata[BowireTacticalApiProtocol.WarningKey];
        Assert.Contains("127.0.0.1", warning, StringComparison.Ordinal);
        Assert.Contains("any certificate is accepted", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_stays_quiet_about_certificates_on_a_plaintext_url()
    {
        // grpc:// is cleartext — there is no certificate to validate, so a
        // warning about not validating one would be noise.
        var plugin = new BowireTacticalApiProtocol();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var result = await plugin.InvokeAsync(
            "grpc://127.0.0.1:1", "Situation", "GetSituationObjects",
            jsonMessages: ["{}"], showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [GrpcTransport.AllowSelfSignedCertsKey] = "true",
                [GrpcTransport.InvocationDeadlineSecondsKey] = "3",
            },
            ct: cts.Token);

        Assert.DoesNotContain(BowireTacticalApiProtocol.WarningKey, result.Metadata.Keys);
    }
}
