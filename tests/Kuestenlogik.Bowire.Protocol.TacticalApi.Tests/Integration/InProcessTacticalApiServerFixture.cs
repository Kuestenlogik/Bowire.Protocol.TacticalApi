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

    /// <summary>
    /// The HTTP/1.1 listener. It carries gRPC-Web and nothing else — a
    /// native gRPC call against it fails — so a test that succeeds here
    /// has proven the plugin switched transports (#67).
    /// </summary>
    public string GrpcWebServerUrl { get; private set; } = string.Empty;

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
            // The second socket is what TacNet's :4268 is: HTTP/1.1 only,
            // so the only gRPC that can reach the services on it is
            // gRPC-Web. Two sockets because a cleartext endpoint cannot
            // negotiate between HTTP/1.1 and h2c — ALPN lives in TLS.
            o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
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
        // Kestrel reports the listeners in the order they were declared:
        // the h2c socket first, the HTTP/1.1 one second.
        var bound = addresses!.Addresses
            .Select(a => a.Replace("[::]", "localhost", StringComparison.Ordinal)
                .Replace("0.0.0.0", "localhost", StringComparison.Ordinal))
            .ToList();
        ServerUrl = bound[0];
        GrpcWebServerUrl = bound[1];

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
    /// <summary>The wording of the envelope refusal, asserted by the tests.</summary>
    internal const string MissingReporterMessage = "reporter is required";

    private readonly object _gate = new();
    private readonly List<SituationObject> _objects =
    [
        new SituationObject
        {
            Symbol = new Symbol
            {
                Identity = new Identity { UuidIdentity = "test-uuid-1" },
            },
        },
    ];

    public override Task<GetSituationObjectsResponse> GetSituationObjects(
        GetSituationObjectsRequest request, ServerCallContext context)
    {
        var resp = new GetSituationObjectsResponse
        {
            Header = new ResponseHeader { Success = true },
        };
        lock (_gate)
        {
            resp.SituationObjects.Add(_objects.Select(o => o.Clone()));
        }
        return Task.FromResult(resp);
    }

    public override Task<AddOrUpdateSituationObjectsResponse> AddOrUpdateSituationObjects(
        AddOrUpdateSituationObjectsRequest request, ServerCallContext context)
    {
        // The refusal the contract's own comments call for: every write
        // message marks `reporter` as Required, and a real server answers
        // its absence with success = false, not with a gRPC error (#66).
        // Mirrors the sample's Situation service.
        foreach (var update in request.SituationObjects)
        {
            var reporter = update.Symbol?.Reporter;
            if (reporter is null || reporter.TypeCase == Identity.TypeOneofCase.None)
            {
                return Task.FromResult(new AddOrUpdateSituationObjectsResponse
                {
                    Header = new ResponseHeader { Success = false, ErrorMessage = MissingReporterMessage },
                });
            }
        }

        lock (_gate)
        {
            foreach (var update in request.SituationObjects)
            {
                var symbol = update.Symbol!;
                _objects.Add(new SituationObject
                {
                    Symbol = new Symbol
                    {
                        Identity = symbol.Identity,
                        CreationMetaData = new CreationMetaData
                        {
                            CreationTime = symbol.ReportingTime,
                            CreatorIdentity = symbol.Reporter,
                        },
                        Name = symbol.Name is null
                            ? null
                            : new DataPropertyString { Content = symbol.Name.Content },
                    },
                });
            }
        }
        return Task.FromResult(new AddOrUpdateSituationObjectsResponse
        {
            Header = new ResponseHeader { Success = true },
        });
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

    /// <summary>
    /// Request header that makes the subscribe pump answer with a refusing
    /// frame instead of data, so the stream-side header check is testable.
    /// </summary>
    internal const string RefuseHeader = "x-fixture-refuse";

    /// <summary>The wording both refusal paths use, asserted by the tests.</summary>
    internal const string RefusalMessage = "source_identifier is required";

    public override Task<UpdatePositionResponse> UpdatePosition(
        UpdatePositionRequest request, ServerCallContext context)
    {
        // TacticalAPI reports a rejected operation in the response header, not
        // as a gRPC error — an ordinary OK carrying success = false
        // (service_types.proto). Mirrors the sample server, and exists so the
        // plugin's refusal path has something real to read (#66).
        if (string.IsNullOrWhiteSpace(request?.Position?.SourceIdentifier))
        {
            return Task.FromResult(new UpdatePositionResponse
            {
                Header = new ResponseHeader { Success = false, ErrorMessage = RefusalMessage },
            });
        }

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
        // A refusing frame on request. Upstream's reference client checks the
        // header of every frame and throws on it, so the plugin has to treat
        // one as an error rather than as data (#66).
        if (context.RequestHeaders.Get(RefuseHeader) is not null)
        {
            await responseStream.WriteAsync(
                new SubscribePositionEventsResponse
                {
                    Header = new ResponseHeader { Success = false, ErrorMessage = RefusalMessage },
                }).ConfigureAwait(false);
            return;
        }

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
