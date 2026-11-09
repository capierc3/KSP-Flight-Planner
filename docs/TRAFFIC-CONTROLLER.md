# KSP Traffic Controller: where it stands

KSP Traffic Controller flies planned trips on their own and keeps track of them: the planner plans a trip, the game add-on flies it, burns and docking included, whether or not the player is watching. It also runs station traffic (lanes, port booking, safety stops). Any craft flying a route this way is a "route craft": a cargo shuttle, a crew ferry, a relay keeping its orbit.

The planner's in-game add-ons are built as one DLL from `mod/KspFlightPlanner.AddOns`, with a folder each:

| Add-on | Folder | What it does |
|---|---|---|
| **Surveys** | `Surveys/` | Stations map the ore of the body they orbit ([SURVEY.md](SURVEY.md)) |
| **Traffic Controller** | `Traffic/` | Flies routes and directs station traffic (this page) |
| **Refueler** | `Refueler/` | Moves fuel between a docked craft and the station's tanks ([MATH.md §11.9](MATH.md#11-background-trips)) |
| **Kerbin Resources** | `KerbinResources/` | Crew management: the flight log, personnel files and the crew window ([KERBIN-RESOURCES.md](KERBIN-RESOURCES.md)) |
| **Dispatch** (idea) | — | Will run routes by itself: repeating loops, triggers such as a station running low on fuel, crew rotation. It hands each trip to Traffic Controller to fly. Automated loops below are its first job. |

**Status (2026-11-08):** the core flying works. Both flyers, on rails and in physics, have been tested with three shuttles at once between Kerb Station, Mun Station and Min Station. What's left is mostly gameplay on top: automated loops, crew rules and cargo.

- Branch: `haulers`, last commit `39f5d6d`, pushed. Not yet merged into `develop`.
- Needs: [kRPC](https://github.com/krpc/krpc) and ModuleManager (both on CKAN). See the README.
- The maths and the reasons behind each rule: [MATH.md §11](MATH.md#11-background-trips) (11.1–11.16).
- Test checklists: [TEST-ROUTES.md](TEST-ROUTES.md), [TEST-STATION-TRIPS.md](TEST-STATION-TRIPS.md), [TEST-TRAFFIC.md](TEST-TRAFFIC.md).

---

## How it works

| Piece | What it does |
|---|---|
| **Planner (app)** | Plans the trip (From here, Go to), sends it to the game, and plans it again when the game says a route craft has gone off course. |
| **Rails flyer** | Away from the player: each burn is made instantly at its time, using the fuel it would use. The course is checked, and the trip is planned again if it drifts. Docking and undocking happen on the saved records. |
| **Physics flyer** | Near the player: it flies like a pilot. It turns, burns, comes in, docks and leaves on its engines and RCS. |
| **Station traffic** | Stations with a pilot aboard direct traffic: one ship at a time inside 200 m, arrivals below and departures above. Stations without a pilot have none, so ships rely on their own safety stop. |
| **Ports** | Ports are booked about an hour before arriving. With none free, a ship waits 3 km behind the station. Each port has a "Route craft" setting and each ship a "Traffic role". |

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
- [x] Port settings (Any / Crew & supplies only / Tankers only / No route craft) and route craft roles (Crew & supplies / Tanker)

**Recovery**
- [x] A burn cut short when physics turns off (leaving for the tracking station mid-burn) is finished on rails
- [x] Re-planning errors are shown in the game instead of being swallowed
- [x] Quickloading keeps the route craft's trips
- [x] The planner follows a ship that comes back under a new id after docking or undocking

## Built but not tried in game yet

- [ ] A parked or half-docked route craft docks on rails once you're away from both it and the station (`RailsFlyer.DockWhileAway`, commit `39f5d6d`)
- [ ] Go to orbit for a route craft: a trip that ends in an orbit, not at a station (MATH §11.16)
- [ ] The safety stop with a kerbal on EVA
- [ ] Two route craft meeting at Kerb Station (uncontrolled), one coming in while the other leaves, with the sideways step-aside
- [ ] Refueler window: its own toolbar button, and fuel moves only to and from the vessels ticked as station tanks (other docked craft are left alone)

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

**Crew rules** (the hooks exist: `CrewRules.CanFly` and `HasTrafficControl`, plus the pilots' log). Agreed 2026-11-08; see [Crew rules design](#crew-rules-design) below.
- [x] Traffic control needs a pilot aboard the station
- [x] Kerbin Resources flight log: every crewed flight with who was aboard, hand-flown ones too (built 2026-11-08, not tried in game; replaces the pilots' log), with the Kerbin Resources window to view it
- [x] Route qualification: a crewed route craft needs a pilot who has flown that route, in that direction (built 2026-11-09, not tried in game)
- [x] Training: one ride-along with a pilot who has 5+ flights on the route qualifies the trainee (built 2026-11-09, not tried in game)
- [ ] Controllers: a probe's burns need a free controller in that SOI (a station pilot, or a pilot on duty at KSC)
- [ ] Controller capacity grows with burns and arrivals/departures handled; burn priority when it's full
- [ ] Departure approval checks every future burn has a controller, or the player confirms flying uncontrolled
- [ ] Uncontrolled burn: warp stops, warning shown, the probe coasts past the burn
- [ ] CommNet link needed: controller ↔ probe, and controller ↔ controller for hand-offs
- [ ] Station pane: what's docked, planned arrivals and departures, crew aboard, controller load
- [ ] Every number in the rules is a setting

**Cargo and roles**
- [ ] Tankers that fill a station's tanks automatically
- [ ] Tugs (moving tanks or modules)
- [ ] More roles than Crew & supplies and Tanker, if needed

**Before merging into develop**
- [ ] Try the items under "Built but not tried in game yet"
- [ ] Open a PR from `haulers` into `develop` (the Test check must pass)

## Crew rules design

Agreed 2026-11-08. Not built yet. Numbers shown are starting values; every one is a setting (no hard-coded values).

### Settings

All rule numbers live in KSP's per-save settings screen (Difficulty Settings → custom tabs, `GameParameters.CustomParameterNode`), the same place other mods put theirs.

### Kerbin Resources

The flight log the rules read is Kerbin Resources' ([KERBIN-RESOURCES.md](KERBIN-RESOURCES.md)): every crewed flight with who was aboard, hand-flown ones too.

### Route qualification

- A crewed route craft may fly a route on its own only if a pilot aboard has flown it before, **in that direction**. Kerbin → Minmus and Minmus → Kerbin are separate routes.
- First flight: the player flies it by hand with a pilot aboard, and the flight log credits the pilot.
- Training: a pilot riding along on a route craft flown by a pilot with **5+ flights** on that route is qualified after **1** ride.
- No losing qualification over time.
- Built 2026-11-09. Checked once, when a route starts (before undocking), with the craft's own crew, not its station's. A route planned again on the way is the same trip, cleared when it started. A crewed craft with no pilot aboard is refused. Probe craft aren't held by this.
- How a pilot's flights on a trip are counted, from the flight log in the order flights arrived:
  - **Flown by hand:** every pilot aboard gets a flight.
  - **Autopilot with a cleared pilot aboard:** each cleared pilot gets a flight. A pilot not yet cleared gets a training ride if a pilot aboard already had 5+ flights, and is cleared after enough rides.
  - **Autopilot with no cleared pilot aboard:** this could only have happened before the rule existed, or with it off, so it counts for every pilot aboard as if flown by hand.
- The refusal says who's aboard and what to do, for example "no pilot aboard (Jebediah Rockmore) has flown Mun Station → Kerb Station. Fly it once by hand with a pilot aboard, or send a pilot with 5+ flights on it along to train one".
- The Kerbin Resources data card shows each pilot's routes, for example "Cleared for: Mun Station → Kerb Station 6× (trains others)" and "Training: Kerb Station → Min Station (0/1 rides)".
- Settings (difficulty settings, "Traffic Controller"): the rule on/off, flights to train others (5), training rides to be cleared (1).

### Controllers (probe route craft)

- A probe needs a controller only for its **burns**. Coasting through an SOI needs nobody.
- Who can be a controller:
  - A pilot **on duty** aboard the **station itself**, not in a ship docked to it. This gives a reason to build crew space into stations.
  - In Kerbin's SOI, a pilot **on duty at KSC** (standing in for the tracking station). The player picks who's on duty in Kerbin Resources' duty roster (built 2026-11-09, [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md#duty-roster)). A pilot put on a ship comes off duty automatically. Only pilots on duty earn controller experience, not every pilot in the Astronaut Complex.
  - Experience lives in Kerbin Resources' controller log (built 2026-11-09, [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md#controller-log)): Traffic writes a burn or hand-off entry when it happens, and counts the log into capacity with these rules.
  - Job titles (built 2026-11-09, `CrewRules.JobOf`), for pilots on duty, shown by Kerbin Resources:
    - **Traffic Controller:** on duty at KSC, or on a station (KSP's vessel type Station, or a craft a route flies to) sitting in a command part (cupola, pod, cockpit) of the station's own parts.
    - **Shuttle pilot:** in the command part of a ship docked to the station, a craft flying a route, or a ship of their own.
    - **Awaiting orders** (no title): anywhere else, such as a hitchhiker can or a habitat module. A docked-on module with no command part never counts, so it needs no setting.
    - The controller rules will take controllers only from pilots titled Traffic Controller.
  - Station pilots go on duty the same way (agreed 2026-11-09): a pilot aboard a station controls only while on duty there. Every controller, at KSC or on a station, is on the duty roster, ready for shifts and fatigue.
  - Later (Kerbin Resources): shifts and fatigue. A controller can't work around the clock; an overworked one handles less and is slower. Not part of the first build.
- So probe trips can run in Kerbin's SOI from KSC alone, but docking at a station still needs a pilot on that station.
- Capacity grows with **burns and arrivals/departures handled**, counted per controller. The route doesn't matter. Starting tiers: under 10 → 2, 10–19 → 4, 20+ → 8. Several controllers on one station add their capacities together.
- A burn takes up one controller slot for **its burn time + 1 minute** (lining up beforehand, then checking the result and planning the next burn or handing the flight off). The physics flyer sees the real burn time. The rails flyer makes the burn instantly but still uses the time the burn would take at full thrust.
- The load is tracked ahead of time from the planned burns, so the station shows a warning as it gets close to its limit.
- When full, burns go by priority, **1 (most urgent) to 10 (can wait hours)**. Starting values: capture 1, course correction 3, phasing 5, departure 10, docking approach 10. A higher-priority burn takes the slot; a held departure waits at its port and a held docking waits outside the station.
- Departure approval: checks that every future burn has a controller. If one won't, the player is asked at departure whether to fly it uncontrolled.
- An uncontrolled burn: time warp stops, a warning is shown, and the probe makes no burn. It coasts right past.
- Links: the controller needs a CommNet link to the probe, and controllers need a link to each other to hand a flight over (a Mun Station controller only knows what's coming because Kerbin's controller handed it off).

### Station pane

Manages a station: what's docked, planned arrivals and departures, who's aboard, and the controllers' load against their capacity.

## Code map

| Where | What |
|---|---|
| `mod/KspFlightPlanner.AddOns/Traffic/TrafficService.cs` (paths below in `Traffic/` unless shown) | kRPC calls from the planner: start a trip, list routes, re-plan requests |
| `TrafficScenario.cs`, `Route.cs` | Saved routes and the per-frame loop that drives them |
| `RailsFlyer.cs`, `RailsDocking.cs`, `CourseCheck.cs` | The rails flyer, docking and undocking on records, course checks |
| `PhysicsFlyer.cs` | The physics flyer: attitude, burns, coming in, docking, leaving, traffic, safety stop |
| `PortBooking.cs`, `TrafficPortModules.cs`, `TrafficPorts.cfg` | Port booking and waiting, port settings and roles (ModuleManager patch) |
| `CrewRules.cs`, `TrafficSettings.cs` | Crew-rule gates: route qualification and training (`CanFly`, `Standings`), traffic control; per-save settings |
| `../KerbinResources/` | Kerbin Resources: flight log, personnel, crew window (code map in [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md)) |
| `Ships.cs`, `RcsBalance.cs`, `TrafficPanel.cs`, `../PanelWindow.cs` | Ship helpers, RCS balance, the in-game Traffic Controller panel, shared window resizing |
| `Refueler/FuelTransfer.cs`, `RefuelerPanel.cs`, `RefuelerScenario.cs` | Splitting a stack into station tanks and craft, moving fuel, the in-game Refueler window |
| `src/KspFlightPlanner.App/Services/RouteWatch.cs` | The planner answering re-plan requests |
| `src/KspFlightPlanner.Core/Ksp/VesselRelink.cs` | Following a vessel across docking and undocking |

## History

| Commit | What landed |
|---|---|
| `369bae5` | Background trips: route craft fly planned routes on their own |
| `2e1129a` | Rails flyer finished: course checks and re-planning |
| `2698d35` | Physics flyer: real burns, coming in, docking, leaving |
| `93df0f6` | Smoother trips, refuelling, RCS balance, pilot-style intercept |
| `9eb7ca0` | Dock and undock on rails; plan from the tracking station |
| `283f8ca` | Mun prediction, docking roll and burn time fixes; undock prep, warp holds, station traffic |
| `5f3eb04` | Crew rules gate, pilots' log, safety stop |
| `0110843` | Station traffic control, port booking, port settings, test fixes |
| `39f5d6d` | Traffic retest fixes, relink by name, orbit trips, cut-burn hand-back, docking on rails while away |
