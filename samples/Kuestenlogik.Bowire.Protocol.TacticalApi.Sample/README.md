# Kuestenlogik.Bowire.Protocol.TacticalApi.Sample

The canonical **RadarSweep** TacticalAPI demo — three MIL-2525C tracks
circling a radar centre at 54°N 11.5°E, one rotation a minute, broadcast
every two seconds — combined so it demonstrates **both** ways Bowire
meets a TacticalAPI server, from one project:

- **Embedded** — the RadarSweep gRPC server runs in-process on `:5191`,
  and the workbench is mounted at `/bowire` with the server already in the
  Sources rail (via `tacticalapi-catalogue.json`). The plugin discovers
  the `Situation` service from its bundled schema — no reflection needed.
- **Separate** — it is a real TacticalAPI gRPC server, so point an
  external workbench or the CLI at it.

The server reuses the plugin's own public `Rheinmetall.TacticalApi.V0`
bindings (`GrpcServices="Both"`), so the sample needs no separate upstream
`.proto` fetch. A single cleartext Kestrel port serves both gRPC (HTTP/2)
and the UI (HTTP/1.1).

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample
```

- Embedded workbench: <http://localhost:5191/bowire> — pick the
  `Situation` service and run `GetSituationObjects` (unary) or
  `SubscribeSituationObjectEvents` (server streaming) to watch the tracks
  sweep.
- As a separate target:

  ```pwsh
  bowire --url tacticalapi@http://localhost:5191
  ```
