// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Google.Protobuf.WellKnownTypes;
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
/// In-process gRPC server hosting all three Rheinmetall services —
/// <c>Situation</c>, <c>OwnPose</c> and <c>BlueForceTracking</c> — for
/// the integration suite. The sample server isn't published as a
/// container image, so the integration tests host equivalent stubs
/// in-process and dial them from the plugin via a real HTTP/2
/// connection on the loopback.
/// </summary>
/// <remarks>
/// Carries <c>[Trait("Category", "Integration")]</c> instead of
/// <c>Docker</c> — the suite runs anywhere with .NET 10 and a free
/// ephemeral port, no Docker daemon required.
/// </remarks>
public sealed class InProcessTacticalApiServerFixture : IAsyncLifetime
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
        builder.Services.AddSingleton<IntegrationOwnPoseService>();
        builder.Services.AddSingleton<IntegrationBlueForceTrackingService>();
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
        app.MapGrpcService<IntegrationOwnPoseService>();
        app.MapGrpcService<IntegrationBlueForceTrackingService>();
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
/// SeededSituation shape the sample uses but skips the background
/// mover — the integration tests want a deterministic snapshot to
/// assert against.
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

/// <summary>
/// Minimal in-process <c>OwnPose</c> implementation. Holds one position
/// so a test can drive the write RPC and read the result back through
/// the plugin in the same call sequence.
/// </summary>
internal sealed class IntegrationOwnPoseService : OwnPose.OwnPoseBase
{
    /// <summary>Source identifier the fixture starts with, before any write.</summary>
    internal const string SeedSource = "fixture-gnss";

    private readonly object _gate = new();
    private Position _position = new()
    {
        SourceIdentifier = SeedSource,
        PointLocation = NewPoint(54.44, 9.80),
        IsInvalidOrExpired = false,
    };

    public override Task<GetPositionResponse> GetPosition(
        GetPositionRequest request, ServerCallContext context)
    {
        lock (_gate)
        {
            return Task.FromResult(new GetPositionResponse
            {
                Header = new ResponseHeader { Success = true },
                Position = _position.Clone(),
            });
        }
    }

    public override Task<UpdatePositionResponse> UpdatePosition(
        UpdatePositionRequest request, ServerCallContext context)
    {
        lock (_gate)
        {
            _position = new Position
            {
                SourceIdentifier = request.Position?.SourceIdentifier,
                PointLocation = request.Position?.PointLocation,
                IsInvalidOrExpired = false,
            };
        }
        return Task.FromResult(new UpdatePositionResponse
        {
            Header = new ResponseHeader { Success = true },
        });
    }

    public override async Task SubscribePositionChangedEvents(
        SubscribePositionEventsRequest request,
        IServerStreamWriter<SubscribePositionEventsResponse> responseStream,
        ServerCallContext context)
    {
        for (var i = 0; i < 2; i++)
        {
            await responseStream.WriteAsync(
                new SubscribePositionEventsResponse
                {
                    Header = new ResponseHeader { Success = true },
                    Position = new Position
                    {
                        SourceIdentifier = $"pose-frame-{i}",
                        PointLocation = NewPoint(54.44 + (i * 0.01), 9.80),
                    },
                },
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    internal static Point NewPoint(double latitude, double longitude) =>
        new()
        {
            LocationTime = Timestamp.FromDateTime(DateTime.UtcNow),
            GeoPoint = new GeoPoint
            {
                LatitudeCoordinate = latitude,
                LongitudeCoordinate = longitude,
            },
        };
}

/// <summary>
/// Minimal in-process <c>BlueForceTracking</c> implementation. Keeps
/// whatever <c>AddOrUpdateBlueForces</c> is given so a test can write a
/// force through the plugin and read the same force back.
/// </summary>
internal sealed class IntegrationBlueForceTrackingService : BlueForceTracking.BlueForceTrackingBase
{
    /// <summary>Callsign of the force the fixture is seeded with.</summary>
    internal const string SeedCallsign = "fixture-bravo";

    private readonly object _gate = new();
    private readonly List<BlueForce> _forces =
    [
        new BlueForce
        {
            Identity = new Identity { StringIdentity = "bf-seed-1" },
            Callsign = SeedCallsign,
        },
    ];

    public override Task<GetBlueForcesResponse> GetBlueForces(
        GetBlueForcesRequest request, ServerCallContext context)
    {
        var resp = new GetBlueForcesResponse
        {
            Header = new ResponseHeader { Success = true },
        };
        lock (_gate)
        {
            resp.BlueForces.Add(_forces.Select(f => f.Clone()));
        }
        return Task.FromResult(resp);
    }

    public override Task<AddOrUpdateBlueForcesResponse> AddOrUpdateBlueForces(
        AddOrUpdateBlueForcesRequest request, ServerCallContext context)
    {
        lock (_gate)
        {
            foreach (var update in request.BlueForcesToUpdates)
            {
                _forces.Add(new BlueForce
                {
                    Identity = update.Identity,
                    Callsign = update.Callsign,
                    LastContactTime = update.LastContactTime,
                    PointLocation = update.PointLocation,
                    BlueForceType = update.BlueForceType,
                });
            }
        }
        return Task.FromResult(new AddOrUpdateBlueForcesResponse
        {
            Header = new ResponseHeader { Success = true },
        });
    }

    public override async Task SubscribeBlueForceEvents(
        SubscribeBlueForceEventsRequest request,
        IServerStreamWriter<SubscribeBlueForceEventsResponse> responseStream,
        ServerCallContext context)
    {
        for (var i = 0; i < 2; i++)
        {
            var frame = new SubscribeBlueForceEventsResponse
            {
                Header = new ResponseHeader { Success = true },
            };
            frame.UpdatedBlueForces.Add(new BlueForce
            {
                Identity = new Identity { StringIdentity = $"bf-frame-{i}" },
            });
            await responseStream.WriteAsync(frame, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
