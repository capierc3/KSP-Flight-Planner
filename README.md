# KSP Flight Planner

Windows desktop tool (WPF, .NET 10) for Kerbal Space Program mission planning.

- **Window** — laid out like an IDE: tool panes at the sides (Mission, Calculators, Saved missions, Locations, Vessels, Rocket, Flights), documents in
  the middle (Legs & phases, Δv map, Transfer Planner, Rendezvous, …). Drag any pane to another side, split a side top
  and bottom, tab panes together, pin them to the edge or float them; *View* reopens anything closed. Two modes, switched
  top right or with Ctrl+1 / Ctrl+2: **Plan** and **Fly**, each with its own arrangement, saved when the app closes
  (*View › Reset Window Layout* puts the current mode back to the default). Docking by
  [AvalonDock](https://github.com/Dirkster99/AvalonDock).

- **Saved missions** — the mission is the document the app works on: name it at the top of the Mission pane and
  *File › Save Mission* (Ctrl+S) stores its stops, phases, per-leg choices, options, the design checked against it and
  its notes (Notes pane). The Saved missions pane lists them, most recent first: double-click to open, or New /
  Delete. *Save Mission As* saves a copy and carries on with the copy. The title bar shows the open mission, with `*`
  for unsaved changes; you're asked before they'd be lost, and the last mission reopens at the next start.
- **Mission builder** — plan a route as an ordered list of stops (add, reorder, remove, or click them on the
  interactive Δv map), split the legs into rocket phases (Launch / Transfer / Lander …) with per-phase margins,
  toggle aerobraking and plane changes per leg, edit any map value (e.g. Mun landing 580 → 700; stored as a
  user override), add custom locations such as a space station at any altitude (Hohmann transfer from the
  body's low orbit).
- **Leg tools** — a leg between planets (Kerbin → Duna) can be flown in a chosen launch window: *Plan a transfer
  window for this leg* under its stop opens the Transfer Planner locked to the leg. The window picked (best, or
  clicked on the porkchop plot) is saved with the mission, and its ejection and capture burns replace the Δv map's
  values in Legs & phases and the Rocket totals. *＋ Rendezvous* in the Mission pane plans meeting crafts from the
  save (relays, stations, debris, without importing them) from where the mission is (its last stop; stops are added at the end, in order): from a
  KSC launch, from a craft it's at, or from the low orbit the Δv map's steps reach (e.g. home from the Mun). *Add to
  mission* adds a stop per craft; the crafts are kept with the mission, not on the Locations tab. *＋ Transfer* opens
  the Transfer Planner from the planet the mission is at (a moon's planet, via the map), and adds the destination's
  orbit (or a flyby) flown in the window picked; *＋ Deploy* opens Resonant Orbits around the body the mission is at,
  and adds a stop in that orbit with the constellation. Orbits not on the Δv map are kept with the mission too. Any leg to a placed
  craft can *Plan the rendezvous*: the Rendezvous tab opens locked to the leg, and the plan's burns replace the map's
  orbital values (the plan is kept as made, since craft move). A new tool on the next leg starts when the previous leg's tool arrives. A stop in a circular orbit can
  *Deploy satellites here*: Resonant Orbits opens locked to the stop's orbit, and the carrier's burns are added to a
  phase you pick (each satellite's own circularize burn is shown, not added).
- **Locations** — every body with all known data (physical, orbit, atmosphere, low orbit, Δv-map stops,
  derived values) read-only; custom locations editable: circular or elliptical orbit, inclination, description
  and images. Only independent values are stored; everything derived is calculated live, so nothing conflicts.
- **Math guide** — every formula the app uses with sources and worked examples, in [docs/MATH.md](docs/MATH.md)
  and on the in-app Guide tab (same file, embedded at build time).
- **Transfer planner** — best launch window between two bodies (porkchop plot via Lambert solver),
  ejection/capture Δv, ejection burn angle, ideal Hohmann reference, optional return trip, and a mission Δv budget.
  Its inputs (bodies, orbits, earliest departure…) sit in the Calculators pane, which shows the inputs of whichever
  calculator (Transfer Planner, Rendezvous, Resonant Orbits) you looked at last, so the documents are all results.
  Opened from a mission leg it chooses that leg's window; on its own it's a free calculator.
- **Rendezvous** — meet one or more craft in orbit (relays to repair, debris to deorbit), starting from a KSC launch or
  a craft already in orbit. Targets come straight from the linked save (no import needed) or from placed custom
  locations. Plans when to lift off and which heading flies straight into the target's plane, then per target:
  match planes, wait or fly a phasing orbit ("go lower to catch up"), a Lambert intercept with a clickable porkchop
  plot, and the match-velocity burn (each leg also drawn from above: orbits, transfer arc and numbered burns); optionally a deorbit at the end. Every burn is shown by feel (which way, what to
  look for) or as exact maneuver-node values (UT, prograde / normal / radial), with a Δv budget checked against a saved
  design or the chosen vessel's remaining Δv. Opened from a mission leg it plans that leg (one target, start and target
  from the leg).
- **Resonant orbits** — carrier orbit for deploying evenly spaced satellite constellations. Opened from a mission stop it plans
  that stop's deployment.
- **Vessels** — every vessel in the linked save, grouped by body, with its remaining Δv, thrust, burn time,
  fuel left per resource, engines, crew and parts. Part data (masses, engines, antennas, resource densities) is read
  from the KSP folder — ModuleManager's cache when present, so mod patches count — and stored in the database.
  Saved designs (VAB/SPH .craft files, including the one open in the editor) get a stage-by-stage Δv, TWR and burn
  time, simulated like KSP's own stage readout (crossfeed, fuel lines, boosters dropped when spent).
  In the Mission Builder's Rocket pane, pick a design to check it against the mission: its stages fill the phases
  in order, showing which stages cover each phase, the spare or missing Δv, and the launch TWR. Margins count as
  wiggle room: a design that flies the route but not every margin is "enough, inside the margin" (amber), not "not enough".
  It also checks the TWR at every landing and lift-off (Kerbin launch, Mun landing and ascent, …) with the stage
  firing then, the mass left at that point, the body's surface gravity and, for atmospheres, the thrust lost to pressure.
- **KSP save link** — set your KSP folder and save in *Tools › Settings*, then *File › Import Vessels from Save* (Ctrl+I) turns vessels in orbit
  into placed custom locations linked to the save. While linked, their orbits follow the game: the save file is
  watched and re-read whenever KSP writes it (scene changes, launches, autosave). The planner only reads KSP's files.
- **Live connection (kRPC)** — *Tools › Connect to KSP* talks to the running game through the
  [kRPC](https://krpc.github.io/krpc/) mod (install it with CKAN and click *Start server* in its in-game window). The
  status bar shows the link, the game scene and the game date. Address and ports default to kRPC's stock server and can
  be changed in *Tools › Settings › Live connection*. While connected:
  - the game's live time and (in flight) every vessel's live orbit are laid over the save, so the Rendezvous tab, Vessels
    tab and linked custom locations follow the game between saves; vessels launched since the last save show up too
  - the Rendezvous tab can start from **the craft you're flying** and add **the craft you've targeted**
  - the Transfer Planner's **Today** button sets the earliest departure to the game's date
- **Fly mode** — the flying side. *Fly this plan* on the Rendezvous tab (or *Fly this deployment* on Resonant
  Orbits) switches to Fly and opens the plan in the Flight document: its burns with live countdowns, whether each is on the craft yet, **Send to KSP** (one
  burn, the next, or all), and how far the craft has drifted from the plan, with **Re-plan from here** to fly the
  rest from its real orbit. Resonant deployments time the carrier's burn and each satellite's circularize burn from
  the carrier's real orbit. Flights are kept until you delete them.

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
- `custom_locations`, `custom_location_images` — user-defined orbits (periapsis, apoapsis, inclination,
  description) and their images (stored as BLOBs).
- `custom_locations.ksp_save`, `ksp_vessel_id` — set when a location was imported from a save and follows its vessel.
- `ksp_parts`, `ksp_part_resources`, `ksp_part_engines`, `ksp_engine_propellants`, `ksp_part_antennas`, `ksp_resources` —
  the part catalog read from the KSP install (replaced as a whole when GameData changes).
- `app_settings` — preferences as key/value text (KSP folder, chosen save, watch on/off, kRPC address and ports); see `SettingsRepository`.
- `saved_constellations`, `saved_missions`, `flights` — user data (a flight is stored as JSON: burns, start orbit and the plan's inputs).
- **Pre-release:** the schema is a single v1 script that is still edited in place. When the schema or seed
  data changes, the database is rebuilt automatically and user tables are copied across
  (`PlannerDatabase.UserTables`, with mappings for renamed columns). Before release, switch to appended
  migrations in `Migrations.Scripts`.

## Data sources

- All systems: Eric Meyer's [Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) (MIT License).
- Stock orbits, atmosphere and gravity: the [KSP wiki](https://wiki.kerbalspaceprogram.com) `<Body>/Param` pages.
- Surface-to-low-orbit Δv and the mission builder's route values: the community
  [KSP Δv map 1.0.4](https://www.reddit.com/r/KerbalSpaceProgram/) by /u/swashlebucky, based on Kowgan's map
  (CC BY-NC-SA 4.0).
