# Kuestenlogik.Bowire.Protocol.TacticalApi

[![CI](https://img.shields.io/github/actions/workflow/status/Kuestenlogik/Bowire.Protocol.TacticalApi/ci.yml?branch=main&label=CI)](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Kuestenlogik.Bowire.Protocol.TacticalApi)](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.TacticalApi)
[![License](https://img.shields.io/github/license/Kuestenlogik/Bowire.Protocol.TacticalApi)](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/blob/main/LICENSE)

Bowire protocol plugin for Rheinmetall's **[TacticalAPI](https://github.com/Rheinmetall/tacticalapi)** — a gRPC interface for situational-awareness systems. The plugin bundles the upstream service schema so users get a typed discovery sidebar and invoke pane against any TacticalAPI server, **even when the server does not expose gRPC Server Reflection**.

## What it does

- **Bundled schema** — at build time the plugin downloads the upstream `.proto` files, compiles them with `Grpc.Tools`, and ships only the generated C# bindings. The Bowire sidebar can render the `Situation` service tree without uploading or hand-editing `.proto` files.
- **Drop-in protocol tab** — once installed, Bowire shows a `TacticalAPI` tab next to gRPC / REST / SignalR. Connect via `bowire --url tacticalapi@<host:port>`.
- **Server-streaming aware** — TacticalAPI's `SubscribeSituationObjectEvents` is server-streaming; the plugin surfaces it as a streaming method, not a unary call.

## Licensing — please read

This repository (the plugin source, generated bindings package, README, samples) is **Apache-2.0**.

The TacticalAPI `.proto` files at <https://github.com/Rheinmetall/tacticalapi> are licensed under **EPL-2.0 OR BSD-3-Clause** (Rheinmetall Electronics GmbH). Vendoring those files into this Apache-2.0 repository would constitute redistribution under a different license, which the EPL does not permit. To keep both licenses honoured:

- The `.proto` files are **downloaded at build time** from the upstream repository into `obj/tacticalapi-protos/` (gitignored, never committed).
- `Grpc.Tools` then compiles them into the assembly. **Only the generated C# bindings** ship in our NuGet package.
- The `.proto` source itself never enters our source tree or our package.

If you need air-gapped builds, pre-populate `<repo-root>/artifacts/obj/Kuestenlogik.Bowire.Protocol.TacticalApi/<Configuration>/tacticalapi-protos/rheinmetall/tactical_api/v0/` with the six `.proto` files yourself (same names as upstream) and the build target will short-circuit because `DownloadFile` skips unchanged files.

The upstream pinned commit is [`e68546809d981cd649325dba4a9702c1a77a1a0b`](https://github.com/Rheinmetall/tacticalapi/tree/e68546809d981cd649325dba4a9702c1a77a1a0b/rheinmetall/tactical_api/v0). The pin will move to a released tag once Rheinmetall cuts one.

## Install

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.TacticalApi
```

Bowire discovers the plugin automatically via assembly scanning — no extra registration code needed.

## Use

```bash
bowire --url tacticalapi@my-situation-server:50051
```

Open the Bowire workbench, pick the **TacticalAPI** tab, and the four methods of the `Situation` service (`SubscribeSituationObjectEvents`, `GetSituationObjects`, `AddOrUpdateSituationObjects`, `DeleteSituationObjects`) appear in the sidebar.

## Build

Building this repo **requires outbound internet access** so the proto-fetch target can reach `raw.githubusercontent.com`. GitHub Actions runners have it by default — fine for CI. For offline builds see the air-gapped instructions above.

```bash
dotnet restore
dotnet build -c Release
dotnet test  -c Release
```

## Roadmap

- **0.1.0 (this release)** — bundled-schema discovery, plugin registration, identity API, generated client stubs available to consumers.
- **0.2.0** — typed unary invoke (`GetSituationObjects` / `AddOrUpdateSituationObjects` / `DeleteSituationObjects`) and server-streaming pump for `SubscribeSituationObjectEvents` via the generated client, JSON request/response envelopes matching the Bowire schema.
- **0.3.0** — sample server + walkthrough, position-extractor adapter for the upcoming Bowire map widget so `SituationObjectLocation` updates land on the map automatically, authentication helpers (TLS + bearer-token metadata).

## Acknowledgements

The TacticalAPI specification, including all `.proto` files this plugin compiles against, is the work of **Rheinmetall Electronics GmbH**. Used here in accordance with the upstream EPL-2.0 / BSD-3-Clause licensing. See <https://github.com/Rheinmetall/tacticalapi> for the upstream project.

## License

[Apache-2.0](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/blob/main/LICENSE)
