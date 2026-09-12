# Kuestenlogik.Bowire.Protocol.TacticalApi.Sample

The canonical TacticalAPI demo — **thirteen MIL-2525C tracks in five
groups**, **four blue forces**, and **this host's own pose**, across the
western Baltic and the Schleswig-Holstein coast, broadcast every two
seconds — combined so it demonstrates **both** ways Bowire meets a
TacticalAPI server, from one project:

- **Embedded** — the RadarSweep gRPC server runs in-process, and the
  workbench is mounted at `/bowire` with the server already in the
  Sources rail (via `tacticalapi-catalogue.json`). The plugin discovers
  all three services from its bundled schema — no reflection needed.
- **Separate** — it is a real TacticalAPI gRPC server, so point an
  external workbench or the CLI at it.

The server reuses the plugin's own public `Rheinmetall.TacticalApi.V0`
bindings (`GrpcServices="Both"`), so the sample needs no separate upstream
`.proto` fetch.

All three upstream services are served:

| Service | What it does here |
|---|---|
| `Situation` | Read-only. The thirteen tracks, as one snapshot per frame. |
| `OwnPose` | Read **and write**. Where this host is, and a way to tell it otherwise. |
| `BlueForceTracking` | Read **and write**. Friendly participants that report themselves, with keep-alive expiry. |

## The scenario

| Group | Tracks | Motion |
|---|---|---|
| **RadarSweep** | 3 (friendly / hostile / neutral) | 120° apart on one 6.6 km circle at 54°N 11.5°E, a rotation a minute |
| **Convoy Alpha** | 3 friendly | Nose-to-tail east along a road at 11 m/s, 60 m apart |
| **Convoy Bravo** | 2 friendly | South at 16 m/s, 80 m apart |
| **UAV Kite** | 1 friendly | Orbiting the Bay of Lübeck, 2.5 km radius, a rotation every 90 s |
| **Engagement** | 2 friendly + 2 hostile | Two pairs closing head-on, so the trajectories cross |
| **Blue forces** | 4 friendly, on `BlueForceTracking` | A static command post, this host's own vehicle, a UAV mounted on it, and a dismounted section at walking pace |

The groups are deliberately unlike each other — different places, speeds,
shapes and affinities. Three contacts on one circle show that a map is
updating; they show nothing about whether the workbench keeps entities
apart. A bug that merges two tracks, colours by message type instead of
by entity, or drops the tail of a multi-entity frame is visible here and
invisible on a single circle.

**Every `Situation` track arrives in the same snapshot message**, as one
entry in `situationObjects`. That is the shape to test against: N
entities in ONE frame. In the map widget's **Tracks** panel, set *Group
by* to `uuid` (or `symbol.name.content`) to separate them — the path is
resolved per array element, not once per frame.

The DIS sample in `Bowire.Protocol.Dis` is deliberately the mirror image:
one entity per PDU, grouped across frames.

## Blue forces are not situation objects with another name

A situation object is something a system reports *about*. A blue force
reports *itself* — it keeps itself alive, and it is deleted when it stops.
The sample implements that rule rather than describing it, and the four
seeded forces are picked to make the difference visible:

| Callsign | Type | Where it gets its position |
|---|---|---|
| `Nordstern` | leader | Static. A command post that drifts is a bug. |
| `Gecko 21` | vehicle, `own_blue_force` | This host's own pose — the same value `OwnPose` reports. |
| `Kiebitz 1` | unmanned, `mount_host = Gecko 21` | None of its own: mounted means *exactly where the host is*. |
| `Möwe 3` | leader | A leg at 1.4 m/s — the slowest thing on the map. |

Two behaviours are worth driving from the workbench:

- **One write moves three things.** Call `OwnPose` → `UpdatePosition`
  with a coordinate &mdash; the envelope the contract expects, not just the
  numbers:

  ```json
  {
    "position": {
      "sourceIdentifier": "Api",
      "pointLocation": {
        "locationTime": "2026-09-12T10:15:00Z",
        "geoPoint": {
          "latitudeCoordinate": 54.52,
          "longitudeCoordinate": 9.91,
          "measurementCode": "ESTIMATE"
        }
      }
    }
  }
  ```

  `sourceIdentifier` names the system reporting the fix, `locationTime` is when
  the fix was taken (not when it was reported), and `measurementCode` says how
  &mdash; `GPS` for a tracked position, `ESTIMATE` for a derived one. Leave
  `sourceIdentifier` out and this server refuses the write the way a real one
  does: gRPC answers `OK`, the response header carries `success = false`, and
  Bowire reports `tacticalapi:refused`. That is the path worth seeing once.

  The own pose reports it, `Gecko 21` reports it, and
  `Kiebitz 1` — bolted to Gecko 21 — reports it too. A client that draws
  `mount_host` as a relationship and one that draws two unrelated dots
  both look right until this call; then only one of them stays right. A
  fix that is never refreshed keeps being used but comes back flagged
  `is_invalid_or_expired` after 30 s, which is what the schema says
  should happen to a stale GNSS position.
- **A blue force you add expires.** Call `BlueForceTracking` →
  `AddOrUpdateBlueForces` with a new identity &mdash; `string_identity` is the
  right form here, and `lastContactTime` is what the keep-alive contract reads:

  ```json
  {
    "blueForcesToUpdates": [
      {
        "identity": { "stringIdentity": "sample-uav-9" },
        "callsign": "Kiebitz 9",
        "lastContactTime": "2026-09-12T10:15:00Z",
        "blueForceType": { "isUnmanned": true },
        "symbol": { "symbolCatalog": "MIL_2525C", "stringIdentifier": "SFAPMH----****" },
        "pointLocation": {
          "locationTime": "2026-09-12T10:15:00Z",
          "geoPoint": {
            "latitudeCoordinate": 54.33,
            "longitudeCoordinate": 10.14,
            "measurementCode": "GPS"
          }
        }
      }
    ]
  }
  ```

  It appears in the open
  subscription at once. Leave it alone for 30 s and it comes back one
  last time with `is_deleted` set, then disappears from `GetBlueForces` —
  the upstream keep-alive contract. The seeded four are re-stamped every
  tick, so they stay. A client that ignores `is_deleted` keeps drawing a
  friendly unit that is not there.

## Two ports, and why

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample
```

- **<http://localhost:5191/bowire>** — the embedded workbench, HTTP/1.1.
- **`http://localhost:5192`** — the three gRPC services, cleartext HTTP/2
  (h2c). Already in the Sources rail of the embedded workbench; from
  outside:

  ```pwsh
  bowire --url tacticalapi@http://localhost:5192
  ```

One socket cannot carry both. Kestrel chooses between HTTP/1.1 and
HTTP/2 by ALPN, and ALPN is part of the TLS handshake — so a *cleartext*
endpoint left on the `Http1AndHttp2` default serves HTTP/1.1 and answers
every gRPC call with HTTP/2 error `HTTP_1_1_REQUIRED`. Splitting the
ports is also what a real TacticalAPI deployment does: a native gRPC
port, and a web port beside it.
