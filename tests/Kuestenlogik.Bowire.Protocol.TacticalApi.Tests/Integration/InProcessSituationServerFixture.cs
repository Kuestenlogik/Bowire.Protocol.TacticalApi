// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests.Integration;

/// <summary>
/// In-process gRPC server hosting Rheinmetall's <c>Situation</c>
/// service for the integration suite. The Bowire.Samples.TacticalApi
/// sample isn't published as a container image (the source is in a
/// separate repo and we don't want CI to chain across repos), so the
/// integration tests host an equivalent service in-process and dial
/// it from the plugin via a real HTTP/2 connection on the loopback.
/// </summary>
/// <remarks>
/// Carries <c>[Trait("Category", "Integration")]</c> instead of
/// <c>Docker</c> — the suite runs anywhere with .NET 10 and a free
/// ephemeral port, no Docker daemon required.
/// </remarks>
public sealed class InProcessSituationServerFixture : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>The <c>http://...</c> URL the plugin can use as serverUrl.</summary>
    public string ServerUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Bind to an ephemeral port; the OS hands us one we then
        // read back via the IServerAddressesFeature. Parallel test
        // runs can't collide on a hard-coded number this way.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<IntegrationSituationService>();
        builder.WebHost.UseKestrel(o =>
        {
            // Listen on 127.0.0.1 specifically — Kestrel rejects
            // dynamic-port binding against "localhost" (see the
            // InvalidOperationException it throws). 127.0.0.1 + port=0
            // gives us an ephemeral port the OS picks.
            o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
        });

        var app = builder.Build();
        app.MapGrpcService<IntegrationSituationService>();
        await app.StartAsync().ConfigureAwait(false);

        // Read the actual port back; ServerUrl uses http:// (plain
        // HTTP/2) because in-process loopback doesn't need TLS — the
        // plugin's URL normaliser accepts grpc:// and translates to
        // http://, which is exactly what we want here.
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var address = addresses!.Addresses.First();
        ServerUrl = address.Replace("[::]", "localhost", StringComparison.Ordinal)
            .Replace("0.0.0.0", "localhost", StringComparison.Ordinal);

        _host = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }
    }
}

/// <summary>
/// Minimal in-process <c>Situation</c> implementation. Echoes the
/// SeededSituation shape the Bowire.Samples sample uses but skips
/// the background mover — the integration tests want a deterministic
/// snapshot to assert against.
/// </summary>
internal sealed class IntegrationSituationService : Situation.SituationBase
{
    public override Task<GetSituationObjectsResponse> GetSituationObjects(
        GetSituationObjectsRequest request, ServerCallContext context)
    {
        var resp = new GetSituationObjectsResponse
        {
            Header = new ResponseHeader { Success = true },
        };
        resp.SituationObjects.Add(new SituationObject
        {
            Symbol = new Symbol
            {
                Identity = new Identity { UuidIdentity = "test-uuid-1" },
            },
        });
        return Task.FromResult(resp);
    }

    public override async Task SubscribeSituationObjectEvents(
        SubscribeSituationObjectEventsRequest request,
        IServerStreamWriter<SubscribeSituationObjectEventsResponse> responseStream,
        ServerCallContext context)
    {
        // Emit two frames so the integration test can assert the
        // streaming path actually iterates, then close cleanly.
        for (var i = 0; i < 2; i++)
        {
            var frame = new SubscribeSituationObjectEventsResponse
            {
                Header = new ResponseHeader { Success = true },
            };
            frame.SituationObjects.Add(new SituationObject
            {
                Symbol = new Symbol
                {
                    Identity = new Identity { UuidIdentity = $"stream-frame-{i}" },
                },
            });
            await responseStream.WriteAsync(frame, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
