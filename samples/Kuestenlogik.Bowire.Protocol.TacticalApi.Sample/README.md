# Kuestenlogik.Bowire.Protocol.TacticalApi.Sample

The canonical TacticalAPI demo — **thirteen MIL-2525C tracks in five
groups** under **seven 2525D control measures**, **four blue forces**, and
**this host's own pose**, across the western Baltic and the
Schleswig-Holstein coast, broadcast every two seconds — combined so it demonstrates **both** ways Bowire meets a
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
| `Situation` | Read **and write**. The thirteen tracks and the seven control measures, as one snapshot per frame — and symbols the operator adds, changes and deletes beside them. |
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
| **Overlay** | 7 control measures (6 friendly + 1 hostile) | Static. The lines, areas, arrow, corridor, sector and ellipse the tracks operate under — see below |
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

## The overlay is geometry, not points

A track is a point, and a point is the one shape a symbol renderer draws
from the code alone. The rest of what MIL-STD-2525 calls a tactical
graphic is drawn from its geometry, and the TacticalAPI `SymbolLocation`
has a case for each kind. The overlay uses one graphic per case the
standard has a symbol for, so a consumer that handles the cases one at a
time can see which one it has not reached yet:

| Graphic | 2525D code | `SymbolLocation` case | Where |
|---|---|---|---|
| Boundary, battalion | `10032500161101000000` | `line`, 4 points | North–south between the convoys' ground and the engagement |
| Phase Line *PL HANSE* | `10032500001403000000` | `line`, 4 points | East–west across the engagement's line of advance |
| Assembly Area *AA BUCHE* | `10032500001502000000` | `polygon`, 5 points | Around Convoy Alpha's origin |
| Axis of Advance, main attack *AXIS BLAU* | `10032500001514030000` | `multipoint`, 3 path points + 1 width point | Blau's attack, south-east onto Rot |
| Air Corridor *AC KITE* | `10032500001701000000` | `corridor`, 3 points, 2 000 m wide | From the coast out to the UAV's orbit |
| Sensor Range Fan *Radar Wismar* | `10032500002422000000` | `fan`, 1–12 km, 300°–030° | The sector the sweep-centre radar watches, over the water |
| Defended Area, hostile | `10062500002002010000` | `ellipse`, centre + one point per axis | Offshore to the north-east — the overlay's one red graphic |

Two things are deliberate. The codes are **2525D in the twenty-digit
numeric form** — `symbolIdentifier.content.numericIdentifier` with
`firstTenDigits` / `secondTenDigits`, `symbolCatalog` =
`SYMBOL_CATALOG_MIL2525_D` — where the tracks use fifteen-letter 2525C
strings, so the numeric path a real producer sends is exercised too; a
consumer reassembles the code by formatting each half with ten digits.
And **nothing in the overlay moves**: control measures are planned, not
observed, and they carry no motion for the tick to apply.

`routeLocation` and `sketchLocation` are left out on purpose — a route is
its own situation-object type upstream, and a sketch carries its own
colour and line style instead of a symbol code.

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

## Writing to it

Three writes are worth driving from the workbench. Each request body below
is the shape [Rheinmetall's reference client](https://github.com/Rheinmetall/tacticalapi/tree/main/testclient/csharp)
sends, so copying it produces a request a real server accepts &mdash; and
each one has a field you can leave out to see the server refuse the way a
real one does: gRPC answers `OK`, the response header carries
`success = false` and a reason, and Bowire reports `tacticalapi:refused`
with that reason in the response metadata. The conventions behind the
fields are in [the protocol page](../../docs/protocol.md#writing-to-tacticalapi).

- **A symbol you place stays where you put it.** Call `Situation` →
  `AddOrUpdateSituationObjects` with the envelope every write carries:

  ```json
  {
    "situationObjects": [
      {
        "symbol": {
          "identity": { "uuidIdentity": "6f1c2d3e-4a5b-4c6d-8e9f-0a1b2c3d4e5f" },
          "reporter": { "stringIdentity": "TacticalAPI" },
          "reportingTime": "2026-09-13T10:15:00Z",
          "name": { "content": "Fähre Holnis" },
          "symbolIdentifier": {
            "content": { "symbolCatalog": "SYMBOL_CATALOG_MIL2525_C", "stringIdentifier": "SNSP------*****" }
          },
          "location": {
            "content": {
              "point": {
                "locationTime": "2026-09-13T10:15:00Z",
                "geoPoint": { "latitudeCoordinate": 54.86, "longitudeCoordinate": 9.58 }
              }
            }
          }
        }
      }
    ]
  }
  ```

  `identity` is the symbol's own id &mdash; a fresh UUID to create one, the
  existing one to change it. `reporter` is who made the change; use the value
  Rheinmetall gave you, or `TacticalAPI` if you have none. `reportingTime` is
  the UTC time of the change. All three are marked *Required* in the
  contract, and this server refuses a write missing any of them &mdash; leave
  `reporter` out and the response says so.

  Send the same `identity` again with **only** `"name": { "content": "…" }`
  and only the name changes: updates are sparse, and a property you do not
  send is a property you did not touch. Send a seeded track's `identity`
  (they are in `GetSituationObjects`) with a `location` and it stops
  following its leg &mdash; the operator's fix takes over, the way an own-pose
  fix does below. Then `DeleteSituationObjects` with the same envelope
  (`identity`, `reporter`, `reportingTime`) retires it: it comes through the
  open subscription once more with `isDeleted` set, and is gone from the next
  `GetSituationObjects`. Delete it twice and the second call is refused
  &mdash; "no situation object with identity …" &mdash; which is the refusal
  most worth having seen once. A symbol sent with an `expiryTime` retires
  itself: the contract says expired symbols are marked deleted
  automatically, and the next tick after the time passes does exactly what
  a delete does, with `Sample.Expiry` as the reporter.

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
          "measurementCode": "MEASUREMENT_CODE_ESTIMATE"
        }
      }
    }
  }
  ```

  `sourceIdentifier` names the system reporting the fix, `locationTime` is when
  the fix was taken (not when it was reported), and `measurementCode` says how
  &mdash; `MEASUREMENT_CODE_GPS` for a tracked position,
  `MEASUREMENT_CODE_ESTIMATE` for a derived one. Enums travel under their
  full proto names in JSON, as here; the short `GPS` the C# bindings use is
  not accepted on the wire. Leave `sourceIdentifier` out and this server
  refuses the write.

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
        "symbol": { "symbolCatalog": "SYMBOL_CATALOG_MIL2525_C", "stringIdentifier": "SFAPMH----****" },
        "pointLocation": {
          "locationTime": "2026-09-12T10:15:00Z",
          "geoPoint": {
            "latitudeCoordinate": 54.33,
            "longitudeCoordinate": 10.14,
            "measurementCode": "MEASUREMENT_CODE_GPS"
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
  friendly unit that is not there. Leave `identity` or `lastContactTime`
  out and the write is refused &mdash; the whole request, not just that
  entry, because the contract has one header per call and no
  partial-success shape.

## Two ports, and why

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample
```

- **<http://localhost:5191/bowire>** — the embedded workbench, HTTP/1.1.
  The same socket serves the three services as **gRPC-Web**, which is
  what Rheinmetall's TacNet does on its `:4268` and what their reference
  client dials by default. To reach it from outside, turn on the plugin's
  `useGrpcWeb` setting (Settings → TacticalAPI) and point it here:

  ```pwsh
  bowire --url tacticalapi@http://localhost:5191
  ```

- **`http://localhost:5192`** — the three gRPC services, cleartext HTTP/2
  (h2c) — TacNet's `:4267`. Already in the Sources rail of the embedded
  workbench; from outside:

  ```pwsh
  bowire --url tacticalapi@http://localhost:5192
  ```

One socket cannot carry both. Kestrel chooses between HTTP/1.1 and
HTTP/2 by ALPN, and ALPN is part of the TLS handshake — so a *cleartext*
endpoint left on the `Http1AndHttp2` default serves HTTP/1.1 and answers
every gRPC call with HTTP/2 error `HTTP_1_1_REQUIRED`. Splitting the
ports is also what a real TacticalAPI deployment does: a native gRPC
port, and a web port beside it. Point the plugin at the wrong one and the
error is a transport error, not a refusal: `useGrpcWeb` against `:5192`
or native gRPC against `:5191` both fail before any service is reached.
