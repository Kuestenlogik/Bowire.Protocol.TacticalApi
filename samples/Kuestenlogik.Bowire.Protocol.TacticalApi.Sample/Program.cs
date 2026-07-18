// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

// Combined TacticalAPI sample for Bowire. One project, both stories:
//
//   * Embedded — the RadarSweep demo server (three MIL-2525C tracks
//     circling 54°N 11.5°E) runs in-process on :5191, and the workbench
//     is mounted at /bowire with the server already seeded into the
//     Sources rail via tacticalapi-catalogue.json.
//   * Separate — it is a real TacticalAPI gRPC server, so point an
//     external workbench or `bowire --url tacticalapi@http://localhost:5191`
//     at it.
//
// A single cleartext Kestrel port carries both: the default Http1AndHttp2
// negotiates HTTP/2 (gRPC) and HTTP/1.1 (the UI) on the same socket.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample
//   → open http://localhost:5191/bowire

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;
using Kuestenlogik.Bowire.Sources;

// Force the TacticalAPI plugin assembly to load before AddBowire's
// reflection scan runs — the Kuestenlogik.Bowire 2.2.x contract scans
// loaded assemblies, so without an explicit type reference the plugin DLL
// wouldn't be loaded in time for discovery.
_ = typeof(global::Kuestenlogik.Bowire.Protocol.TacticalApi.BowireTacticalApiProtocol);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5191");

// The folded RadarSweep gRPC server. The Situation service base comes
// from the plugin's public Rheinmetall.TacticalApi.V0 bindings.
builder.Services.AddGrpc();
builder.Services.AddSingleton<SituationServiceImpl>();

// Embedded Bowire + catalogue-driven discovery pointed at this host.
builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

app.MapGrpcService<SituationServiceImpl>();

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));

await app.RunAsync();
