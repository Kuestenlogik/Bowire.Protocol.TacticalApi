# Kuestenlogik.Bowire.Protocol.TacticalApi

[![CI](https://img.shields.io/github/actions/workflow/status/Kuestenlogik/Bowire.Protocol.TacticalApi/ci.yml?branch=main&label=CI)](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Kuestenlogik.Bowire.Protocol.TacticalApi)](https://www.nuget.org/packages/Kuestenlogik.Bowire.Protocol.TacticalApi)
[![License](https://img.shields.io/github/license/Kuestenlogik/Bowire.Protocol.TacticalApi)](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/blob/main/LICENSE)
[![Bowire](https://img.shields.io/badge/Bowire-%E2%89%A5%202.2.1%2C%20%3C%203.0-006B9F)](https://github.com/Kuestenlogik/Bowire/blob/main/docs/architecture/compatibility.md)

Bowire protocol plugin for Rheinmetall's **[TacticalAPI](https://github.com/Rheinmetall/tacticalapi)** — a gRPC interface for situational-awareness systems. The plugin bundles the upstream service schema so users get a typed discovery sidebar and invoke pane against any TacticalAPI server, **even when the server does not expose gRPC Server Reflection**.

## What it does

- **Bundled schema** — at build time the plugin downloads the upstream `.proto` files, compiles them with `Grpc.Tools`, and ships only the generated C# bindings. The Bowire sidebar can render the service tree without uploading or hand-editing `.proto` files.
- **Every upstream service** — `Situation` (situational-awareness objects), `OwnPose` (the reporting platform's own position) and `BlueForceTracking` (friendly participants reporting themselves). Which services exist is a one-line list in the plugin, so an upstream addition lands in discovery, invoke, streaming and mock replay at once rather than service by service.
- **Drop-in protocol tab** — once installed, Bowire shows a `TacticalAPI` tab next to gRPC / REST / SignalR. Connect via `bowire --url tacticalapi@<host:port>`.
- **Server-streaming aware** — each service has a `Subscribe…` pump (`SubscribeSituationObjectEvents`, `SubscribePositionChangedEvents`, `SubscribeBlueForceEvents`); the plugin surfaces them as streaming methods, not unary calls. Client-streaming and duplex aren't part of the TacticalAPI surface (no upstream RPC defines them) and the plugin rejects callers that try.
- **mTLS via the shared `__bowireMtls__` marker** — same auth profile that REST / gRPC / Kafka / AMQP read; PEM cert + key + optional CA + allow-self-signed. The legacy `_bowire:client-cert-pfx` / `_bowire:client-cert-password` / `_bowire:tls-skip-validation` keys stay supported for callers that pinned against the pre-1.0 vocabulary.

## Licensing — please read

This repository (the plugin source, generated bindings package, README, samples) is **Apache-2.0**.

The TacticalAPI `.proto` files at <https://github.com/Rheinmetall/tacticalapi> are licensed under **EPL-2.0 OR BSD-3-Clause** (Rheinmetall Electronics GmbH). Vendoring those files into this Apache-2.0 repository would constitute redistribution under a different license, which the EPL does not permit. To keep both licenses honoured:

- The `.proto` files are **downloaded at build time** from the upstream repository into `obj/tacticalapi-protos/` (gitignored, never committed).
- `Grpc.Tools` then compiles them into the assembly. **Only the generated C# bindings** ship in our NuGet package.
- The `.proto` source itself never enters our source tree or our package.

If you need air-gapped builds, pre-populate `<repo-root>/artifacts/obj/Kuestenlogik.Bowire.Protocol.TacticalApi/<Configuration>/tacticalapi-protos/rheinmetall/tactical_api/v0/` with the ten `.proto` files yourself (same names as upstream) and the build target will short-circuit because `DownloadFile` skips unchanged files.

The upstream pinned commit is [`58661c9c5de7db1b944a37f6ed05fe16c603cd0e`](https://github.com/Rheinmetall/tacticalapi/tree/58661c9c5de7db1b944a37f6ed05fe16c603cd0e/rheinmetall/tactical_api/v0). The pin will move to a released tag once Rheinmetall cuts one.

## Install

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.TacticalApi
```

Bowire discovers the plugin automatically via assembly scanning — no extra registration code needed.

## Use

```bash
bowire --url tacticalapi@my-situation-server:50051
```

Open the Bowire workbench, pick the **TacticalAPI** tab, and all three services appear in the sidebar:

| Service | Methods |
|---|---|
| `Situation` | `SubscribeSituationObjectEvents`, `GetSituationObjects`, `AddOrUpdateSituationObjects`, `DeleteSituationObjects` |
| `OwnPose` | `SubscribePositionChangedEvents`, `GetPosition`, `UpdatePosition` |
| `BlueForceTracking` | `SubscribeBlueForceEvents`, `GetBlueForces`, `AddOrUpdateBlueForces` |

## Build

Building this repo **requires outbound internet access** so the proto-fetch target can reach `raw.githubusercontent.com`. GitHub Actions runners have it by default — fine for CI. For offline builds see the air-gapped instructions above.

```bash
dotnet restore
dotnet build -c Release
dotnet test  -c Release
```

## Sample

A runnable sample lives in this repo under
[`samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample`](samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample)
— a self-contained mini gRPC server serving all three services (thirteen
MIL-2525C tracks, four blue forces, and its own pose), with the Bowire
workbench embedded alongside it. `dotnet run`, open
<http://localhost:5191/bowire>, and the TacticalAPI tab has live data to
discover, invoke against, subscribe to and — for `OwnPose` and
`BlueForceTracking` — write to, without a real Rheinmetall server in the
lab. See its [README](samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample/README.md)
for the scenario and the two writes worth driving by hand.

## Tests

Unit tests run on every CI build (descriptor walk, JSON parser
edge cases, mTLS-marker handling, URL normalisation). The
integration suite under `tests/.../Integration` carries
`[Trait("Category", "Integration")]` and hosts an in-process
Kestrel + gRPC stub of all three services for end-to-end discovery
+ invoke + stream + write round-trips — no Docker daemon needed, so
CI and a laptop run exactly the same pass.

## What's in 1.0

- Bundled-schema discovery (no Server Reflection required on the target).
- Unary invoke + server-streaming subscribe for every upstream service — `Situation`, `OwnPose` and `BlueForceTracking`.
- Shared `__bowireMtls__` marker integration alongside the legacy `_bowire:` keys.
- Plugin-tunable knobs: `invocationDeadlineSeconds`, `streamIdleSeconds`, `allowSelfSignedCerts`.
- IBowireMockEmitter so recordings tagged `protocol: "tacticalapi"` replay through `bowire mock`.

## Upstream proto pinning

The bundled schema is fetched from a pinned git ref on
[`Rheinmetall/tacticalapi`](https://github.com/Rheinmetall/tacticalapi)
(the `<TacticalApiProtoRef>` property in the csproj). Rheinmetall
publishes commits but no release tags as of 2026-09, so the ref is a
commit SHA — the only reproducible option until a tag exists; when one
does, the same property takes it. The weekly
[`check-upstream-protos.yml`](.github/workflows/check-upstream-protos.yml)
workflow watches the upstream `HEAD` and opens an issue when a new
commit lands, and says so when a tag has appeared, so the pin gets
bumped deliberately rather than drifting.

## Acknowledgements

The TacticalAPI specification, including all `.proto` files this plugin compiles against, is the work of **Rheinmetall Electronics GmbH**. Used here in accordance with the upstream EPL-2.0 / BSD-3-Clause licensing. See <https://github.com/Rheinmetall/tacticalapi> for the upstream project.

## License

[Apache-2.0](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi/blob/main/LICENSE)
