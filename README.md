# KSP Flight Planner

Windows desktop tool (WPF, .NET 10) for Kerbal Space Program mission planning.
Current feature: resonant orbit calculator for deploying evenly spaced satellite constellations.

## Layout

| Project | Purpose |
|---|---|
| `src/KspFlightPlanner.Core` | Orbital math and models (no UI / DB dependencies) |
| `src/KspFlightPlanner.Data` | SQLite schema, migrations, seed data, repositories |
| `src/KspFlightPlanner.App` | WPF UI, builds `KspFlightPlanner.exe` |
| `tests/KspFlightPlanner.Tests` | xUnit tests (math checked against the reference calculator) |

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
  (`is_builtin = 1`; user-added bodies can use `0`). `mu` is stored as mass × G.
- `saved_constellations` — user plans.
- Schema version is tracked with `PRAGMA user_version`. To change the schema, append a new script to
  `Migrations.Scripts`; never edit one that has shipped.

## Credits

Body data and the resonant-orbit approach come from Eric Meyer's
[Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) (MIT License).
