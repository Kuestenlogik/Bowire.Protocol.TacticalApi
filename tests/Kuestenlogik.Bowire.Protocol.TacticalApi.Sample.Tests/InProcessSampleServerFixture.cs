// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Tests;

/// <summary>
/// The sample's own three services, hosted in-process on an ephemeral
/// h2c port. The plugin's suite hosts stubs written for the plugin's
/// sake; this one hosts what <c>dotnet run --project samples/…</c>
/// serves, so the behaviour the sample README promises — sparse updates,
/// the envelope refusals, expiry, the keep-alive — is pinned by a test
/// rather than by a smoke run somebody did once.
/// </summary>
/// <remarks>
/// No <c>ScenarioTicker</c>: the tests drive <c>TickAt</c> themselves with
/// whatever clock they need, so an expiry thirty seconds out takes no
/// thirty seconds and no frame arrives that a test did not ask for.
/// </remarks>
public sealed class InProcessSampleServerFixture : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>The <c>http://...</c> URL the plugin can use as serverUrl.</summary>
    public string ServerUrl { get; private set; } = string.Empty;

    internal SituationServiceImpl Situation { get; private set; } = null!;
    internal BlueForceTrackingServiceImpl BlueForces { get; private set; } = null!;
    internal OwnPoseServiceImpl OwnPose { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // Same registrations as the sample's Program.cs, minus the
        // workbench and the ticker.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<ExerciseClock>();
        builder.Services.AddSingleton<OwnPlatform>();
        builder.Services.AddSingleton<SituationServiceImpl>();
        builder.Services.AddSingleton<OwnPoseServiceImpl>();
        builder.Services.AddSingleton<BlueForceTrackingServiceImpl>();
        builder.WebHost.UseKestrel(o =>
            o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2));

        var app = builder.Build();
        app.MapGrpcService<SituationServiceImpl>();
        app.MapGrpcService<OwnPoseServiceImpl>();
        app.MapGrpcService<BlueForceTrackingServiceImpl>();
        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        ServerUrl = addresses!.Addresses.First()
            .Replace("[::]", "localhost", StringComparison.Ordinal)
            .Replace("0.0.0.0", "localhost", StringComparison.Ordinal);

        Situation = app.Services.GetRequiredService<SituationServiceImpl>();
        BlueForces = app.Services.GetRequiredService<BlueForceTrackingServiceImpl>();
        OwnPose = app.Services.GetRequiredService<OwnPoseServiceImpl>();
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
