---
title: TacticalAPI
summary: 'TacticalAPI is a Bowire sibling plugin that wraps Rheinmetall''s situational-awareness gRPC interface with a bundled schema so the service tree renders even without Server Reflection. Discovery and typed invoke for Situation, OwnPose and BlueForceTracking, a server-streaming pump per service, mTLS via the shared __bowireMtls__ marker, IBowireMockEmitter for recording replay.'
---

<!--
  This file is the source of truth for this plugin's page on https://bowire.io.

  Bowire's docs build fetches it into its own docs/protocols/tacticalapi.md and
  links it from the protocols navigation. Bowire keeps the fetched result
  committed so its site still builds when this repository is unreachable —
  that copy is overwritten on every build, so edit this file, never that one.

  The page lives here because the behaviour it describes lives here. While it
  sat in the Bowire repository, a claim and the code making it true could only
  be fixed in two separate commits, and for at least one setting they drifted
  for weeks.
-->

# TacticalAPI

The TacticalAPI plugin connects Bowire to Rheinmetall's situational-awareness gRPC interface: bundled-schema discovery (no Server Reflection on the target needed), typed unary invoke across `Situation`, `OwnPose` and `BlueForceTracking`, a server-streaming pump per service, URL-scheme normalisation (`tacticalapi@host:port`, `grpc(s)://`, bare `host:port`), and mTLS via the shared `__bowireMtls__` marker alongside the legacy `_bowire:client-cert-pfx` keys.

The TacticalAPI plugin connects Bowire to [Rheinmetall's **TacticalAPI**](https://github.com/Rheinmetall/tacticalapi) &mdash; a gRPC interface for situational-awareness systems. The plugin ships the upstream service schema bundled with the package, so Bowire can render the service tree against any TacticalAPI server **even when the server does not expose gRPC Server Reflection**.

**Package:** `Kuestenlogik.Bowire.Protocol.TacticalApi` (sibling repo, not bundled with the CLI)

## Install

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.TacticalApi
```

Bowire discovers the plugin automatically via assembly scanning &mdash; no extra registration code.

## Use

```bash
bowire --url tacticalapi@my-situation-server:50051
```

Open the workbench, pick the **TacticalAPI** tab, and all three upstream services appear in the sidebar:

| Service | Methods | What it carries |
|---|---|---|
| `Situation` | `SubscribeSituationObjectEvents`, `GetSituationObjects`, `AddOrUpdateSituationObjects`, `DeleteSituationObjects` | Situational-awareness objects a system reports *about* &mdash; symbols, action tasks, organisation units. |
| `OwnPose` | `SubscribePositionChangedEvents`, `GetPosition`, `UpdatePosition` | The reporting platform's *own* position, and a way to set it. |
| `BlueForceTracking` | `SubscribeBlueForceEvents`, `GetBlueForces`, `AddOrUpdateBlueForces` | Friendly participants reporting *themselves*, on a 30 s keep-alive: a blue force that stops being re-sent is deleted implicitly. |

Click **Execute** on any unary method and the call goes out to the server; pick a streaming method and the workbench's Wireshark-style frame pane starts filling with events as they're emitted.

### URL forms

The plugin normalises the URL Bowire passes through:

| You type | What the plugin connects to |
|---|---|
| `tacticalapi@host:4267` | `https://host:4267` (default — TacticalAPI in the field is mTLS) |
| `grpc://host:50051` | `http://host:50051` (plaintext gRPC) |
| `grpcs://host:50051` | `https://host:50051` |
| `host:4267` | `https://host:4267` |

### Settings

The plugin declares four settings (Settings → TacticalAPI). The workbench delivers them in the metadata bag, and the plugin strips them before the gRPC `Metadata` is built, so none of them reaches the server:

| Setting | Default | What it does |
|---|---|---|
| `invocationDeadlineSeconds` | `0` (off) | gRPC deadline for **unary** calls. Bounds a cold connect against a server that hangs. Not applied to subscriptions &mdash; a deadline on a stream would end every subscription on the clock. |
| `streamIdleSeconds` | `0` (off) | Ends a subscription after this many seconds **without a frame**. The pump closes with one last frame carrying `"status": "tacticalapi:stream-idle"` and the reason, so the frame pane shows why it stopped rather than looking like the server hung up. |
| `allowSelfSignedCerts` | `false` | Skip server-certificate validation. Staging and self-signed only. |
| `useGrpcWeb` | `false` | Speak gRPC-Web over HTTP/1.1 instead of native gRPC over HTTP/2 &mdash; see [Two ports](#two-ports-native-grpc-and-grpc-web). |

Anything else in the metadata bag travels as a gRPC request header, except the transport keys below.

### mTLS / TLS

TacticalAPI servers in production almost always run behind mTLS. The plugin takes its TLS configuration from the metadata bag and never forwards any of it &mdash; the keys are filtered out before the gRPC `Metadata` is built. Three paths:

| Path | Purpose |
|---|---|
| **`__bowireMtls__`** (shared marker) | The preferred path, and the one the workbench's mTLS auth profile writes: client certificate and private key as PEM, plus `allowSelfSigned`. It is the same marker the core REST / gRPC / Kafka / AMQP plugins read (`Kuestenlogik.Bowire.Auth.MtlsConfig`), so an mTLS profile set up once in the workbench works on TacticalAPI the same way it works everywhere else. When it is present, the legacy PFX keys below are ignored so two client certificates never load at once. |
| `allowSelfSignedCerts=true` (setting) | Accept any server certificate. Same opt-in semantics as the core gRPC plugin's `--allow-self-signed-certs`; the marker's own `allowSelfSigned` covers the same need. |
| `_bowire:tls-skip-validation=true`, `_bowire:client-cert-pfx=<path>`, `_bowire:client-cert-password=<pw>` (legacy) | The keys this plugin first shipped with: accept any server certificate; a PFX with client certificate and private key; its password (omit for an unprotected PFX). Still honoured so recorded sessions and saved profiles from before the shared marker keep working. From the CLI: `--metadata _bowire:client-cert-pfx=...`. |

### Writing to TacticalAPI

Every write carries the same envelope, and the contract marks all three fields
`Required:` on every update and delete message &mdash; `UpdateSymbol`,
`UpdateActionTask`, `DeleteSituationObject` and the rest. Bowire surfaces them
as required in the invoke form (since #68), but the conventions behind them are
not guessable:

| Field | What to send |
| --- | --- |
| `identity` | The object's own id. A fresh UUID when creating a symbol; the existing one when updating it. `string_identity` is the shorter form for low-bandwidth links; `int32_identity` / `int64_identity` are reserved for internal use and must not be used to create objects. |
| `reporter` | Who generated the change. Use the value Rheinmetall gave you &mdash; **`TacticalAPI`** if you have none. |
| `reporting_time` | UTC time of the change. Only the most up-to-date information is considered, so use the current time for a real change &mdash; but **keep the previous timestamp when nothing changed**, or you will re-date an unchanged object. |

Two more rules that cost data rather than time when missed:

- **Updates are sparse.** An `Update…` message changes only the properties you
  send. Omit a property entirely to leave it untouched; send the property with
  no `content` to clear the value. Sending a complete object is a legitimate
  request that overwrites everything the operator never meant to touch.
- **A position needs its own time and quality.** `Point.location_time` is the
  time of the fix, not of the report, and `GeoPoint.measurement_code` says how
  it was obtained &mdash; `MEASUREMENT_CODE_GPS` for a tracked position,
  `MEASUREMENT_CODE_ESTIMATE` for a derived one (enums go under their full
  proto names in the JSON the invoke pane sends). Upstream's reference client
  sets both on every position it sends, and `OwnPose.UpdatePosition`
  additionally names its `source_identifier` (the system reporting the fix).

**A refused write is not a transport error.** TacticalAPI answers a rejected
operation with an ordinary gRPC `OK` whose `ResponseHeader` carries
`success = false` and an `error_message`. Bowire reads that header and reports
the call as `tacticalapi:refused` with the server's wording in the response
metadata (#66) &mdash; for unary calls and for every streamed frame, which is
what upstream's own client does. A server that fills no header at all is
treated as success, so older or partial implementations keep working.

### Two ports: native gRPC and gRPC-Web

TacticalAPI servers commonly expose **two ports**: a native HTTP/2 gRPC endpoint (typically `:4267`) and a gRPC-Web endpoint over HTTP/1.1 (typically `:4268`). Rheinmetall's own reference client defaults to the gRPC-Web one, because it is what survives a proxy or load balancer that will not carry h2c.

This plugin speaks both (#67). Native gRPC is the default; switch the wire with either of:

- the plugin setting **`useGrpcWeb`** (Settings → TacticalAPI), or
- the shared `__bowireGrpcTransport=web` marker in the call metadata — the same marker the core appends for the core gRPC plugin's `grpcweb@` hint, so a caller that already knows that vocabulary needs no second one.

Discovery works over either transport regardless, because the proto schema is bundled rather than fetched at connect time. Server-streaming works over gRPC-Web too; what gRPC-Web cannot carry is client-streaming and duplex, and the TacticalAPI surface has neither.

## Licensing &mdash; please read

The plugin code, generated bindings package, and documentation are **Apache-2.0**. The upstream TacticalAPI `.proto` files at <https://github.com/Rheinmetall/tacticalapi> are **EPL-2.0 OR BSD-3-Clause** (Rheinmetall Electronics GmbH). Vendoring those files into an Apache-2.0 repository would be redistribution under a different license, which EPL-2.0 does not permit. To honour both:

- The `.proto` files are **downloaded at build time** from the upstream repository (pinned commit `58661c9c5de7db1b944a37f6ed05fe16c603cd0e`) into `obj/tacticalapi-protos/` &mdash; gitignored, never committed.
- `Grpc.Tools` compiles them into the assembly; **only the generated C# bindings** ship in the NuGet package.
- The `.proto` source itself never enters the plugin's source tree or its published package.

The pin will move to a released tag once Rheinmetall cuts one. A scheduled GitHub Action in the sibling repo (`.github/workflows/check-upstream-protos.yml`) runs weekly, compares the pinned commit against `Rheinmetall/tacticalapi@main`, and opens a tracking issue when upstream drifts &mdash; so the bump cadence stays visible without manual polling.

## Build requirements

Building the plugin **requires outbound internet access** to `raw.githubusercontent.com` so the proto-fetch target can reach the upstream repo. GitHub Actions runners have this by default; air-gapped CI does not.

### Air-gapped builds

Pre-populate the proto cache before invoking `dotnet build`:

```text
<repo-root>/artifacts/obj/Kuestenlogik.Bowire.Protocol.TacticalApi/<Configuration>/tacticalapi-protos/rheinmetall/tactical_api/v0/
```

Drop the ten upstream `.proto` files (same filenames as on `Rheinmetall/tacticalapi`) into that directory and the build's `DownloadFile` target short-circuits because the files already exist. Consumers of the **published NuGet package** don't need network access &mdash; only contributors and CI building from source do.

## Try it — upstream test client as data populator

Rheinmetall ships an [official C# test client](https://github.com/Rheinmetall/tacticalapi/tree/main/testclient/csharp) alongside the proto set. It's a small CLI that exercises every operation on all three services &mdash; `--observesituation` (server-streaming), `--printsituation` (unary polling), `--sendsymbol` (create), `--changesymbolname` (update), `--deletesymbol` (delete), plus `--setownposition` / `--printownposition` / `--observeownposition` on `OwnPose` and `--updateblueforce` / `--printblueforces` / `--observeblueforces` on `BlueForceTracking`. For Bowire users the test client is the fastest way to **populate a server with realistic data** so the workbench has something interesting to render.

End-to-end demo, against either a live TacticalAPI server or the upstream `TacNet` instance:

```bash
# 1. Build the upstream test client (one-time)
git clone https://github.com/Rheinmetall/tacticalapi
cd tacticalapi/testclient/csharp
dotnet build TacticalApi.TestClient.csproj

# 2. Place a handful of symbols at WGS84 coordinates near Hamburg
#    (the test client takes lat / lon as positional args)
dotnet TacticalApi.TestClient.dll --sendsymbol 53.5 9.9
dotnet TacticalApi.TestClient.dll --sendsymbol 53.55 10.0
dotnet TacticalApi.TestClient.dll --sendsymbol 53.6 10.05

# 3. Add the optional map-widget extension so SituationObjectLocation
#    pins land on a MapLibre canvas next to the streaming-frames pane.
#    (Skip this step if you only want the raw JSON view — Bowire still
#    auto-detects the lat/lon fields, it just falls back to a
#    "Install Kuestenlogik.Bowire.Map" placeholder card instead of a map.)
dotnet add package Kuestenlogik.Bowire.Map

# 4. Point Bowire at the same server — native HTTP/2 transport
bowire --url grpc@https://localhost:4267
#    …or gRPC-Web over HTTP/1.1 if the server exposes :4268 too
bowire --url grpcweb@https://localhost:4268

# 5. In the workbench, pick Situation → SubscribeSituationObjectEvents.
#    Click Execute. With the Frame-Semantics Framework live in
#    Bowire 1.3.0+, the workbench auto-detects the lat/lon fields on
#    every SituationObjectLocation and mounts a Map tab next to the
#    streaming-frames pane — every symbol the test client created
#    appears as a pin, every new --sendsymbol from a parallel
#    terminal lights up live.
```

Why this demo carries weight: **no Bowire-side configuration was involved**. No `bowire.schema-hints.json`, no `IBowireSchemaHints` implementation, no manual right-click on a field. The TacticalAPI plugin ships transport-only; the framework recognises `coordinate.latitude` / `coordinate.longitude` from the field names + WGS84 ranges and routes the data into the map widget on its own. The pgAdmin pattern: shape-of-data drives viewer choice, not protocol-author opt-in. The map widget itself rides its own NuGet package (`Kuestenlogik.Bowire.Map`) so Bowire core stays ~870 KB lighter for users who never need geographic rendering &mdash; same plugin model as the protocol packages.

A companion walkthrough in **[the mock-server docs](../features/mock-server.md#external-client-validation)** uses the same test client to validate `bowire mock` &mdash; a useful inverse comparison if you ever want to verify Bowire reproduces a real server's behaviour faithfully.

## Roadmap

- **v1.0.0 (shipped 2026-05-26)** &mdash; bundled-schema discovery, typed unary CRUD (`GetSituationObjects` / `AddOrUpdateSituationObjects` / `DeleteSituationObjects`), the server-streaming pump for `SubscribeSituationObjectEvents`, URL-scheme normalisation (`tacticalapi@`, `grpc(s)://`, bare `host:port`), mTLS via the shared `__bowireMtls__` marker (legacy `_bowire:client-cert-pfx` keys still honoured for back-compat), `IBowireMockEmitter` for recording replay, in-process Kestrel-hosted integration suite. The current stable line.
- **post-1.0 (shipped)** &mdash; the upstream pin moved to `58661c9` ("Add position and blue force tracking"), which brought the `OwnPose` and `BlueForceTracking` services. Both are discovered, invoked and replayed on the path `Situation` already used; the plugin now iterates a list of service-bearing `.proto` files instead of naming one, so the next upstream service is a one-line change. Nothing in the existing surface broke &mdash; the only edit upstream made to a file the plugin already compiled was two blank lines removed from `situation_object_updates.proto`.
- **post-1.0** &mdash; MIL-STD-2525 / APP-6 symbol renderer (the schema's `SymbolIdentifier` field is already wired through; the map widget side needs a [milsymbol.js](https://github.com/spatialillusions/milsymbol)-style renderer to turn the SIDC into the correct tactical-affiliation glyph). Service-Bus / Artemis-flavoured AMQP 1.0 discovery (parallel item on the AMQP plugin's side) could similarly land as a vendor-specific follow-on.

## Sample

A canonical mini-server lives at [`Bowire.Protocol.TacticalApi/samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample`](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/tree/main/samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample) &mdash; RadarSweep grown into a *combined* sample that serves all three services and mounts the embedded workbench beside them. `dotnet run`, then open <http://localhost:5191/bowire> for the workbench (HTTP/1.1); the gRPC services listen on `http://localhost:5192` (cleartext HTTP/2) and are already seeded into the Sources rail, so an external Bowire connects with `bowire --url tacticalapi@http://localhost:5192`. The workbench port also serves the same three services as gRPC-Web, so the plugin's `useGrpcWeb` setting has something to dial (`tacticalapi@http://localhost:5191`). Thirteen `Situation` tracks in five groups exercise the read side, and three writes are worth driving by hand, each with a request body in the sample's README that carries the envelope upstream's reference client sets: `Situation.AddOrUpdateSituationObjects` places a symbol, changes one property of it sparsely, and &mdash; with `reporter` left out &mdash; is refused the way a real server refuses; `OwnPose.UpdatePosition` moves the own pose, the blue force flagged `own_blue_force` and the UAV mounted on it in one call; a blue force added via `AddOrUpdateBlueForces` and then left alone comes back once with `is_deleted` after 30 s. The ports are split because ALPN lives in the TLS handshake, so one *cleartext* socket cannot serve both HTTP/1.1 and h2c. For a full Harbor Control Center scene, see the harbor-demo sibling [`Bowire.Samples/harbor-demo/src/Kuestenlogik.Bowire.Samples.TacticalApi`](https://github.com/Kuestenlogik/Bowire.Samples/tree/main/harbor-demo/src/Kuestenlogik.Bowire.Samples.TacticalApi).

## Links

- Sibling repository: <https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi> &mdash; full README with the licensing rationale, the proto-fetch target, and the air-gapped instructions in source form.
- Upstream specification: <https://github.com/Rheinmetall/tacticalapi> &mdash; the canonical TacticalAPI proto set, by Rheinmetall Electronics GmbH.
- Related plugin docs: [gRPC](grpc.md) (parent protocol, including the gRPC-Web transport TacticalAPI's `:4268` port uses), [DIS](dis.md) (sibling simulation-environment plugin).

## Acknowledgements

The TacticalAPI specification, including every `.proto` file this plugin compiles against, is the work of **Rheinmetall Electronics GmbH**. Used in accordance with the upstream EPL-2.0 / BSD-3-Clause licensing.
