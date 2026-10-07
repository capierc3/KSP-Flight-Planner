# KSP Flight Planner

Windows desktop tool (WPF, .NET 10) for Kerbal Space Program mission planning.

- **Transfer planner** — best launch window between two bodies (porkchop plot via Lambert solver),
  ejection/capture Δv, ejection burn angle, ideal Hohmann reference, and a one-way mission Δv budget.
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
- `saved_constellations` — user plans.
- **Pre-release:** the schema is a single v1 script that is still edited in place. After changing it,
  delete the database file and it is rebuilt on next launch. Once user data must be preserved, freeze
  shipped scripts and append new ones to `Migrations.Scripts` instead.

## Data sources

- All systems: Eric Meyer's [Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) (MIT License).
- Stock orbits, atmosphere and gravity: the [KSP wiki](https://wiki.kerbalspaceprogram.com) `<Body>/Param` pages.
- Surface-to-low-orbit Δv: community KSP 1.x Δv map (approximate).
