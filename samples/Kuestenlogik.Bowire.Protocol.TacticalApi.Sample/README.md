# Kuestenlogik.Bowire.Protocol.TacticalApi.Sample

The canonical TacticalAPI demo — **thirteen MIL-2525C tracks in five
groups** across the western Baltic and the Schleswig-Holstein coast,
broadcast every two seconds — combined so it demonstrates **both** ways
Bowire meets a TacticalAPI server, from one project:

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

## The scenario

| Group | Tracks | Motion |
|---|---|---|
| **RadarSweep** | 3 (friendly / hostile / neutral) | 120° apart on one 6.6 km circle at 54°N 11.5°E, a rotation a minute |
| **Convoy Alpha** | 3 friendly | Nose-to-tail east along a road at 11 m/s, 60 m apart |
| **Convoy Bravo** | 2 friendly | South at 16 m/s, 80 m apart |
| **UAV Kite** | 1 friendly | Orbiting the Bay of Lübeck, 2.5 km radius, a rotation every 90 s |
| **Engagement** | 2 friendly + 2 hostile | Two pairs closing head-on, so the trajectories cross |

The groups are deliberately unlike each other — different places, speeds,
shapes and affinities. Three contacts on one circle show that a map is
updating; they show nothing about whether the workbench keeps entities
apart. A bug that merges two tracks, colours by message type instead of
by entity, or drops the tail of a multi-entity frame is visible here and
invisible on a single circle.

**Every track arrives in the same snapshot message**, as one entry in
`situationObjects`. That is the shape to test against: N entities in ONE
frame. In the map widget's **Tracks** panel, set *Group by* to `uuid` (or
`symbol.name.content`) to separate them — the path is resolved per array
element, not once per frame.

The DIS sample in `Bowire.Protocol.Dis` is deliberately the mirror image:
one entity per PDU, grouped across frames.

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
