# Haulers: where the feature stands

Haulers are ships that fly planned trips on their own: the planner plans a trip, the game add-on flies it, burns and docking included, whether or not the player is watching. The end goal is an idle or management mode: haulers run routes between stations by themselves.

**Status (2026-11-08):** the core flying works. Both flyers, on rails and in physics, have been tested with three shuttles at once between Kerb Station, Mun Station and Min Station. What's left is mostly gameplay on top: automated loops, crew rules and cargo.

- Branch: `haulers`, last commit `39f5d6d`, pushed. Not yet merged into `develop`.
- Needs: [kRPC](https://github.com/krpc/krpc) and ModuleManager (both on CKAN). See the README.
- The maths and the reasons behind each rule: [MATH.md §11](MATH.md#11-background-trips) (11.1–11.16).
- Test checklists: [TEST-HAULERS.md](TEST-HAULERS.md), [TEST-STATION-TRIPS.md](TEST-STATION-TRIPS.md), [TEST-TRAFFIC.md](TEST-TRAFFIC.md).

---

## How it works

| Piece | What it does |
|---|---|
| **Planner (app)** | Plans the trip (From here, Go to), sends it to the game, and plans it again when the game says a hauler has gone off course. |
| **Rails flyer** | Away from the player: each burn is made instantly at its time, using the fuel it would use. The course is checked, and the trip is planned again if it drifts. Docking and undocking happen on the saved records. |
| **Physics flyer** | Near the player: it flies like a pilot. It turns, burns, comes in, docks and leaves on its engines and RCS. |
| **Station traffic** | Stations with a pilot aboard direct traffic: one ship at a time inside 200 m, arrivals below and departures above. Stations without a pilot have none, so ships rely on their own safety stop. |
| **Ports** | Ports are booked about an hour before arriving. With none free, a ship waits 3 km behind the station. Each port has a "Haulers" setting and each ship a "Hauler role". |

## Done and tested in game

**Trips**
- [x] Planned trips flown on rails: burns, fuel use, course checks and re-planning when off course
- [x] Trips between moons (Mun ↔ Kerbin ↔ Minmus), including ones that need a capture or phasing orbit
- [x] Planning from the tracking station, and for any ship while flying another one
- [x] Docked craft undock on rails, and dock on rails at arrival when nobody is watching
- [x] Refuelling from the station, and balancing a ship's RCS

**Physics flying**
- [x] Turning and real burns, with time warp held for burns, backing off and docking
- [x] Coming in to the station (pilot-style intercept, speeding up when it's crawling), docking, and the magnets catching
- [x] Leaving: backing out, then the departure lane (controlled stations) or pushing clear (uncontrolled ones)
- [x] The autopilot stays on when you switch ships; only the panel switch turns it off

**Traffic and ports**
- [x] Arrival lane below and departure lane above; arrivals hold for a ship leaving, then dock
- [x] Safety stop: three ships leaving at once give way in one fixed order (leaving, then soonest burn, then ship id), with no deadlock; a stopped ship holds against the station and steps sideways out of the other's path
- [x] Port booking; waiting 3 km behind a full station, then coming in once a port frees up
- [x] Port settings (Any / Crew & supplies only / Tankers only / No haulers) and hauler roles (Crew & supplies / Tanker)

**Recovery**
- [x] A burn cut short when physics turns off (leaving for the tracking station mid-burn) is finished on rails
- [x] Re-planning errors are shown in the game instead of being swallowed
- [x] Quickloading keeps the haulers' trips
- [x] The planner follows a ship that comes back under a new id after docking or undocking

## Built but not tried in game yet

- [ ] A parked or half-docked hauler docks on rails once you're away from both it and the station (`RailsFlyer.DockWhileAway`, commit `39f5d6d`)
- [ ] Go to orbit for a hauler: a trip that ends in an orbit, not at a station (MATH §11.16)
- [ ] The safety stop with a kerbal on EVA
- [ ] Two haulers meeting at Kerb Station (uncontrolled), one coming in while the other leaves, with the sideways step-aside
- [ ] Station fuel window: its own toolbar button, and fuel moves only to and from the vessels ticked as station tanks (other docked craft are left alone)

## Known rough edges

- When a port frees up, a ship waiting 3 km out jumps to the line-up point 2.4 km out (on rails, so it's only visible on the map).
- A badly broken trip can still produce an expensive re-plan. For example, a half-escaped ship got a 1,280 m/s three-burn trip in the 2026-11-08 test (the cut-burn fix should stop this happening).

## Next: gameplay

These sit on top of working flight. Nothing below is started unless it's ticked.

**Automated loops** (the main goal)
- [ ] A route that repeats: fuel trigger, haul, dock, unload, refuel, return
- [ ] What starts a trip: a timer, the station's fuel or supply level, or a manual send
- [ ] Unloading and loading cargo (fuel first, then supplies)
- [ ] A view of each route's state and history in the planner

**Crew rules** (the hooks exist: `HaulerRules.CanFly` and `HasTrafficControl`, plus the pilots' log)
- [x] Traffic control needs a pilot aboard the station
- [x] Pilots' log: each pilot's flights between stations, with times flown
- [ ] A crewed hauler needs a pilot who has flown that route before
- [ ] A probe-core hauler needs a pilot on a station in the same sphere of influence to fly it
- [ ] Training: a pilot who has flown a route enough times can train a new one

**Cargo and roles**
- [ ] Tankers that fill a station's tanks automatically
- [ ] Tugs (moving tanks or modules)
- [ ] More roles than Crew & supplies and Tanker, if needed

**Before merging into develop**
- [ ] Try the items under "Built but not tried in game yet"
- [ ] Open a PR from `haulers` into `develop` (the Test check must pass)

## Code map

| Where | What |
|---|---|
| `mod/KspFlightPlanner.Survey/HaulerService.cs` | kRPC calls from the planner: start a trip, list routes, re-plan requests |
| `HaulerScenario.cs`, `HaulerRoute.cs` | Saved routes and the per-frame loop that drives them |
| `RailsFlyer.cs`, `RailsDocking.cs`, `CourseCheck.cs` | The rails flyer, docking and undocking on records, course checks |
| `PhysicsFlyer.cs` | The physics flyer: attitude, burns, coming in, docking, leaving, traffic, safety stop |
| `PortBooking.cs`, `HaulerPortModules.cs`, `HaulerPorts.cfg` | Port booking and waiting, port settings and roles (ModuleManager patch) |
| `HaulerRules.cs`, `PilotLog.cs` | Crew-rule gates and the pilots' log |
| `HaulerShip.cs`, `RcsBalance.cs`, `HaulerPanel.cs` | Ship helpers, RCS balance, the in-game Haulers panel |
| `FuelTransfer.cs`, `StationFuelPanel.cs` | Splitting a stack into station tanks and craft, moving fuel, the in-game Station fuel window |
| `src/KspFlightPlanner.App/Services/HaulerWatch.cs` | The planner answering re-plan requests |
| `src/KspFlightPlanner.Core/Ksp/VesselRelink.cs` | Following a vessel across docking and undocking |

## History

| Commit | What landed |
|---|---|
| `369bae5` | Background trips: haulers fly planned routes on their own |
| `2e1129a` | Rails flyer finished: course checks and re-planning |
| `2698d35` | Physics flyer: real burns, coming in, docking, leaving |
| `93df0f6` | Smoother trips, refuelling, RCS balance, pilot-style intercept |
| `9eb7ca0` | Dock and undock on rails; plan from the tracking station |
| `283f8ca` | Mun prediction, docking roll and burn time fixes; undock prep, warp holds, station traffic |
| `5f3eb04` | Crew rules gate, pilots' log, safety stop |
| `0110843` | Station traffic control, port booking, port settings, test fixes |
| `39f5d6d` | Traffic retest fixes, relink by name, orbit trips, cut-burn hand-back, docking on rails while away |
