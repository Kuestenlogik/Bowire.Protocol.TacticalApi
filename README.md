# Kuestenlogik.Bowire.Protocol.TacticalApi

[![CI](https://img.shields.io/github/actions/workflow/status/Kuestenlogik/Bowire.Protocol.TacticalApi/ci.yml?branch=main&label=CI)](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Kuestenlogik.Bowire.Protocol.TacticalApi)](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.TacticalApi)
[![License](https://img.shields.io/github/license/Kuestenlogik/Bowire.Protocol.TacticalApi)](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/blob/main/LICENSE)
[![Bowire](https://img.shields.io/badge/Bowire-%E2%89%A5%201.5.0%2C%20%3C%202.0-006B9F)](https://github.com/Kuestenlogik/Bowire/blob/main/docs/architecture/compatibility.md)

Bowire protocol plugin for Rheinmetall's **[TacticalAPI](https://github.com/Rheinmetall/tacticalapi)** — a gRPC interface for situational-awareness systems. The plugin bundles the upstream service schema so users get a typed discovery sidebar and invoke pane against any TacticalAPI server, **even when the server does not expose gRPC Server Reflection**.

## What it does

- **Bundled schema** — at build time the plugin downloads the upstream `.proto` files, compiles them with `Grpc.Tools`, and ships only the generated C# bindings. The Bowire sidebar can render the `Situation` service tree without uploading or hand-editing `.proto` files.
- **Drop-in protocol tab** — once installed, Bowire shows a `TacticalAPI` tab next to gRPC / REST / SignalR. Connect via `bowire --url tacticalapi@<host:port>`.
- **Server-streaming aware** — TacticalAPI's `SubscribeSituationObjectEvents` is server-streaming; the plugin surfaces it as a streaming method, not a unary call. Client-streaming and duplex aren't part of the TacticalAPI surface (no upstream RPC defines them) and the plugin rejects callers that try.
- **mTLS via the shared `__bowireMtls__` marker** — same auth profile that REST / gRPC / Kafka / AMQP read; PEM cert + key + optional CA + allow-self-signed. The legacy `_bowire:client-cert-pfx` / `_bowire:client-cert-password` / `_bowire:tls-skip-validation` keys stay supported for callers that pinned against the pre-1.0 vocabulary.

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

## Sample

A runnable sample lives in the central
[`Bowire.Samples`](https://github.com/Kuestenlogik/Bowire.Samples) repo
under
[`src/Kuestenlogik.Bowire.Samples.TacticalApi`](https://github.com/Kuestenlogik/Bowire.Samples/tree/main/src/Kuestenlogik.Bowire.Samples.TacticalApi)
— a self-contained mini gRPC server seeded with three MIL-2525C
symbols (friendly / hostile / neutral surface contacts) so Bowire's
TacticalAPI tab has live data to discover, invoke against, and
subscribe to without a real Rheinmetall server in the lab.

## Tests

Unit tests run on every CI build (descriptor walk, JSON parser
edge cases, mTLS-marker handling, URL normalisation). The
integration suite under `tests/.../Integration` carries
`[Trait("Category", "Docker")]` and spins up the
Bowire.Samples.TacticalApi server in a container for end-to-end
discovery + invoke + stream round-trips. CI runs both passes;
local `dotnet test --filter "Category!=Docker"` skips the
container side.

## What's in 1.0

- Bundled-schema discovery (no Server Reflection required on the target).
- Unary invoke + server-streaming subscribe for the whole upstream `Situation` service.
- Shared `__bowireMtls__` marker integration alongside the legacy `_bowire:` keys.
- Plugin-tunable knobs: `invocationDeadlineSeconds`, `streamIdleSeconds`, `allowSelfSignedCerts`.
- IBowireMockEmitter so recordings tagged `protocol: "tacticalapi"` replay through `bowire mock`.

## Upstream proto pinning

The bundled schema is fetched from a specific commit on
[`Rheinmetall/tacticalapi`](https://github.com/Rheinmetall/tacticalapi)
(see the `<TacticalApiCommit>` property in the csproj). Rheinmetall
publishes commits but no release tags as of 2026-05; pinning to a
commit-SHA is the only reproducible option. The weekly
[`check-upstream-protos.yml`](.github/workflows/check-upstream-protos.yml)
workflow watches the upstream `HEAD` and opens an issue when a new
commit lands so the pin gets bumped deliberately rather than drifting.

## Acknowledgements

The TacticalAPI specification, including all `.proto` files this plugin compiles against, is the work of **Rheinmetall Electronics GmbH**. Used here in accordance with the upstream EPL-2.0 / BSD-3-Clause licensing. See <https://github.com/Rheinmetall/tacticalapi> for the upstream project.

## License

[Apache-2.0](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/blob/main/LICENSE)
