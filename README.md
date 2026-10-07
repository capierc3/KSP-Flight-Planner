# KSP Flight Planner

Windows desktop tool (WPF, .NET 10) for Kerbal Space Program mission planning.

- **Mission builder** — plan a route as an ordered list of stops (add, reorder, remove, or click them on the
  interactive Δv map), split the legs into rocket phases (Launch / Transfer / Lander …) with per-phase margins,
  toggle aerobraking and plane changes per leg, edit any map value (e.g. Mun landing 580 → 700; stored as a
  user override), add custom locations such as a space station at any altitude (Hohmann transfer from the
  body's low orbit), and save missions.
- **Math guide** — every formula the app uses with sources and worked examples, in [docs/MATH.md](docs/MATH.md)
  and on the in-app Guide tab (same file, embedded at build time).
- **Transfer planner** — best launch window between two bodies (porkchop plot via Lambert solver),
  ejection/capture Δv, ejection burn angle, ideal Hohmann reference, optional return trip, and a mission Δv budget.
- **Resonant orbits** — carrier orbit for deploying evenly spaced satellite constellations.

## Layout

| Project | Purpose |
|---|---|
| `src/KspFlightPlanner.Core` | Orbital math and models (no UI / DB dependencies) |
| `src/KspFlightPlanner.Data` | SQLite schema, migrations, seed data, repositories |
| `src/KspFlightPlanner.App` | WPF UI, builds `KspFlightPlanner.exe` |
| `tests/KspFlightPlanner.Tests` | xUnit tests (math checked against reference values) |

## Build, test, run

```
dotnet test
dotnet run --project src/KspFlightPlanner.App
```

## Publish a standalone exe

```
dotnet publish src/KspFlightPlanner.App -c Release -o publish
```

Produces `publish/KspFlightPlanner.exe`, a self-contained single file (no .NET install required).

## Database

SQLite file at `%LOCALAPPDATA%\BrickRoadSoftware\KspFlightPlanner\kspflightplanner.db`, created on first run.

- `star_systems`, `celestial_bodies` — built-in constants seeded from `src/KspFlightPlanner.Data/Seed/bodies.json`
  (`is_builtin = 1`). Stock bodies include parent, orbital elements, atmosphere and surface-to-orbit Δv;
  modded systems currently have physical data only (no orbits, so no transfer planning).
- `dv_map_nodes` — the Δv map as a tree rooted at Kerbin's surface (seeded from `Seed/dvmap.json`).
- `dv_map_overrides` — user edits to map values, keyed by node key.
- `custom_locations` — user-defined circular orbits (attached to the map under the body's low orbit).
- `saved_constellations`, `saved_missions` — user data.
- **Pre-release:** the schema is a single v1 script that is still edited in place. After changing it,
  delete the database file and it is rebuilt on next launch. Once user data must be preserved, freeze
  shipped scripts and append new ones to `Migrations.Scripts` instead.

## Data sources

- All systems: Eric Meyer's [Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) (MIT License).
- Stock orbits, atmosphere and gravity: the [KSP wiki](https://wiki.kerbalspaceprogram.com) `<Body>/Param` pages.
- Surface-to-low-orbit Δv and the mission builder's route values: the community
  [KSP Δv map 1.0.4](https://www.reddit.com/r/KerbalSpaceProgram/) by /u/swashlebucky, based on Kowgan's map
  (CC BY-NC-SA 4.0).
