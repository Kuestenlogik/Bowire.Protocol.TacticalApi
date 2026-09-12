// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

// Combined TacticalAPI sample for Bowire. One project, both stories:
//
//   * Embedded — the RadarSweep demo server (thirteen MIL-2525C tracks,
//     four blue forces and this host's own pose around the western
//     Baltic) runs in-process, and the workbench is mounted at /bowire
//     with the server already seeded into the Sources rail via
//     tacticalapi-catalogue.json.
//   * Separate — it is a real TacticalAPI gRPC server, so point an
//     external workbench or `bowire --url tacticalapi@http://localhost:5192`
//     at it.
//
// All three upstream services are served: Situation (read-only),
// OwnPose and BlueForceTracking (read and write).
//
// Two cleartext ports, because one cannot do it. Kestrel can only pick
// between HTTP/1.1 and HTTP/2 on a shared socket by ALPN, and ALPN is
// part of the TLS handshake — so a cleartext endpoint left on the
// Http1AndHttp2 default answers every gRPC call with HTTP/2 error
// HTTP_1_1_REQUIRED. The sample used to claim one port carried both; it
// did not, and no call in it had ever reached a service. Splitting the
// ports is what a real TacticalAPI deployment does too: a native gRPC
// port, and a web port beside it.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample
//   → open http://localhost:5191/bowire (workbench, HTTP/1.1)
//   → gRPC on http://localhost:5192 (h2c)
//   → the same three services as gRPC-Web on http://localhost:5191
//     (HTTP/1.1) — turn on the plugin's `useGrpcWeb` setting to dial it

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Force the TacticalAPI plugin assembly to load before AddBowire's
// reflection scan runs — the Kuestenlogik.Bowire 2.2.x contract scans
// loaded assemblies, so without an explicit type reference the plugin DLL
// wouldn't be loaded in time for discovery.
_ = typeof(global::Kuestenlogik.Bowire.Protocol.TacticalApi.BowireTacticalApiProtocol);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // HTTP/1.1 for the workbench: browsers do not speak cleartext HTTP/2.
    kestrel.ListenLocalhost(5191, listen => listen.Protocols = HttpProtocols.Http1);
    // h2c for gRPC: prior-knowledge HTTP/2, no negotiation, no TLS.
    kestrel.ListenLocalhost(5192, listen => listen.Protocols = HttpProtocols.Http2);
});

// The folded RadarSweep gRPC server. The service bases come from the
// plugin's public Rheinmetall.TacticalApi.V0 bindings.
builder.Services.AddGrpc();
builder.Services.AddSingleton<ExerciseClock>();
builder.Services.AddSingleton<OwnPlatform>();
builder.Services.AddSingleton<SituationServiceImpl>();
builder.Services.AddSingleton<OwnPoseServiceImpl>();
builder.Services.AddSingleton<BlueForceTrackingServiceImpl>();

// One heartbeat for the whole scenario. Each service is registered a
// second time under IScenarioTick so the ticker can find it without
// naming the three types itself — the same singleton instance gRPC
// serves from, not a copy, or the workbench would watch one scenario
// while another one moved.
builder.Services.AddSingleton<IScenarioTick>(sp => sp.GetRequiredService<SituationServiceImpl>());
builder.Services.AddSingleton<IScenarioTick>(sp => sp.GetRequiredService<OwnPoseServiceImpl>());
builder.Services.AddSingleton<IScenarioTick>(sp => sp.GetRequiredService<BlueForceTrackingServiceImpl>());
builder.Services.AddHostedService<ScenarioTicker>();

// Embedded Bowire + catalogue-driven discovery pointed at this host.
builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

// gRPC-Web on the HTTP/1.1 port, next to the workbench. Rheinmetall's TacNet
// splits the same way — native gRPC on one port, gRPC-Web on another — because
// gRPC-Web is what survives a proxy that will not carry h2c, and browsers
// cannot speak cleartext HTTP/2 at all. With this the sample exercises both
// sides of the plugin's `useGrpcWeb` setting (#67) rather than only the h2c one.
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

app.MapGrpcService<SituationServiceImpl>();
app.MapGrpcService<OwnPoseServiceImpl>();
app.MapGrpcService<BlueForceTrackingServiceImpl>();

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));

await app.RunAsync();
