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
| **Station traffic** | Stations with a Traffic Controller on duty direct traffic: departure clearance, one ship at a time inside 200 m, arrivals below and departures above. Stations without one are uncontrolled airspace, so ships rely on their own safety stop. |
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
- [x] Traffic control needs a pilot aboard the station (since stage A: a Traffic Controller on duty in the station's own parts)
- [x] Kerbin Resources flight log: every crewed flight with who was aboard, hand-flown ones too (built 2026-11-08, not tried in game; replaces the pilots' log), with the Kerbin Resources window to view it
- [x] Route qualification: a crewed route craft needs a pilot who has flown that route, in that direction (built 2026-11-09, not tried in game)
- [x] Training: one ride-along with a pilot who has 5+ flights on the route qualifies the trainee (built 2026-11-09, not tried in game)
- [x] Controllers, stage A (built 2026-11-09; tried in game 2026-11-09: departure clearance, the no-controller warning with warp stopped before a Mun burn, a controller going on duty and the burn uploaded and made on time, a probe holding at 200 m for docking clearance with no controller on duty): a probe's burns are uploaded 10 min ahead by a Traffic Controller on duty in that SOI (a station's, or KSC's for Kerbin). Without one the burn is missed and the trip planned again, held until someone's on duty there. The controller checks the course after each burn, and burns go on their log with kind and priority (critical = 1). Departure clearance comes from a station's on-duty controller, and a station with none is uncontrolled airspace. Docking clearance at the 200 m hold, too: a probe needs a station controller to fly it in, and a crewed craft at a station with none docks on its own. Lanes and traffic control now need an on-duty Traffic Controller, not just a pilot aboard. Job titles name the post ("Mun Station Traffic Controller", "KSC Traffic Controller"). Two settings: "Controllers direct traffic" and "Upload burns (minutes before)".
- [x] Stage C, capacity (built 2026-11-09, not tried in game): 2, 4 or 8 slots by burns, hand-offs and clearances handled. A burn holds a slot from half its length before its time to half after, plus a minute. A craft in the zone holds one too, and each controller handles one craft in the zone at a time. With every slot taken, a more urgent burn takes the slot of a less urgent one, which is then uploaded again if a slot frees before it, or missed. The Traffic Controller window lists each controller's slots now and the busiest over the next 6 hours.
- [x] Stage D, departure approval (built 2026-11-09, not tried in game): before a probe leaves, each burn's SOI is predicted along the planned path. A burn with no controller on duty there, or with that SOI's controllers fully booked then, goes on a list, and the game asks: fly it uncontrolled, or stay docked (or don't start, if it isn't docked). Not asked again for a trip planned again on the way.
- [x] Uncontrolled burn: warp stops, warning shown, the probe coasts past the burn (stage A)
- [x] Stage B, CommNet (built 2026-11-09, not tried in game): a controller needs a link to the probe to upload a burn and to check it after (KSC: the probe's link home; a station: a path from the station). The check waits until someone has a link. Coming into a new SOI, the flight is handed over from the controller who had it to one there, over a link between the two, and both get it on their log. With nobody left in the old SOI, one in the new SOI picks it up. Off with CommNet off, or with the "Controllers need a CommNet link" setting off.
- [x] Stage E, the Station window (built 2026-11-10, not tried in game): in flight, a toolbar button shows the craft you're flying; at the space centre, clicking the Administration building opens it with every station listed. See [Station pane](#station-pane).
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

Agreed 2026-11-09: the probe core does the flying. The controller's job is to make sure the burn is uploaded and, once the probe has made it, to check that it's still on track.

- **Every burn has two controller moments:**
  - **Upload**, 10 minutes before the burn: a controller covering the SOI the burn happens in sends it up. With nobody to upload it, the burn is **missed**.
  - **After-burn check**, once the burn is done: the controller checks the course. If something went wrong (a short burn, a bad pointing), the trip is planned again from where the probe is.
- **A missed burn is never made late.** The probe coasts on, time warp stops, a warning is shown, and the trip is planned again. If there's still no controller for that SOI, the new plan waits until someone goes on duty there; it isn't planned into another missed burn.
- **Burns that would lose the ship come first.** A burn is critical when skipping it would leave the probe escaping the body it should stay round (a capture), or with its periapsis in the air or the ground. Critical burns always get priority 1.
- **Experience only adds capacity** (for now). A green controller is as accurate as a veteran; they just can't handle as many flights at once.

**Crewed route craft** fly their own burns: a cleared pilot aboard needs no controller in flight. But leaving a station that has a Traffic Controller on duty needs **departure clearance** from a free controller there. With none free, the craft waits docked. If the station has no Traffic Controller on duty, it's **uncontrolled airspace**: the craft leaves without waiting, and nobody is called in. Examples: the only pilot aboard is off duty, or the only pilot is the one flying the shuttle. Probes get departure clearance the same way.

**One craft at a time per controller**, agreed 2026-11-09: a Traffic Controller handles one craft inside their station's keep-out zone at a time. That covers a craft leaving, from its clearance until it's out past the zone's edge, and a craft docking, from its clearance until it has docked. Two controllers on duty can handle two craft at once. A departure sent while every controller is busy stays docked and **holds** ("docked at Kerb Station, holding for clearance to leave"), then undocks by itself as soon as one is free. If it held past its first burn, it leaves anyway and is planned again once clear of the station. An arrival waits at the zone's edge (or 3 km behind on rails) the same way. This is the first piece of the capacity rules: stage C adds more craft per controller with experience.

**Arrival (docking) clearance**, agreed 2026-11-09: at a station with a Traffic Controller on duty, every arriving craft holds at the keep-out zone's edge (200 m) and calls the station. It comes in only once the controller gives the all clear. A crewed craft's pilot then flies it in. A probe is **flown in by the controller** from the hold to the port, so it can't dock at a station with no Traffic Controller on duty: it waits at the zone's edge (or 3 km behind the station on rails) until someone goes on duty there. A crewed craft at such a station is in uncontrolled airspace and docks on its own, as with departures.

- A probe needs a controller only for its **burns**. Coasting through an SOI needs nobody.
- Who can be a controller:
  - A pilot **on duty** aboard the **station itself**, not in a ship docked to it. This gives a reason to build crew space into stations.
  - In Kerbin's SOI, a pilot **on duty at KSC** (standing in for the tracking station). The player picks who's on duty in Kerbin Resources' duty roster (built 2026-11-09, [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md#duty-roster)). A pilot put on a ship comes off duty automatically. Only pilots on duty earn controller experience, not every pilot in the Astronaut Complex.
  - Experience lives in Kerbin Resources' controller log (built 2026-11-09, [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md#controller-log)): Traffic writes a burn or hand-off entry when it happens, and counts the log into capacity with these rules.
  - Job titles (built 2026-11-09, `CrewRules.JobOf`), for pilots on duty, shown by Kerbin Resources:
    - **Traffic Controller**, named after the post ("KSC Traffic Controller", "Mun Station Traffic Controller"): on duty at KSC, or on a station (KSP's vessel type Station, or a craft a route flies to) sitting in a command part (cupola, pod, cockpit) of the station's own parts.
    - **Shuttle pilot:** in the command part of a ship docked to the station, a craft flying a route, or a ship of their own.
    - **Awaiting orders** (no title): anywhere else, such as a hitchhiker can or a habitat module. A docked-on module with no command part never counts, so it needs no setting.
    - The controller rules will take controllers only from pilots titled Traffic Controller.
  - Station pilots go on duty the same way (agreed 2026-11-09): a pilot aboard a station controls only while on duty there. Every controller, at KSC or on a station, is on the duty roster, ready for shifts and fatigue.
  - Later (Kerbin Resources): shifts and fatigue. A controller can't work around the clock; an overworked one handles less and is slower. Not part of the first build.
- So probe trips can run in Kerbin's SOI from KSC alone, but docking at a station still needs a pilot on that station.
- Capacity grows with **burns and arrivals/departures handled**, counted per controller. The route doesn't matter. Starting tiers: under 10 → 2, 10–19 → 4, 20+ → 8. Several controllers on one station add their capacities together.
- A burn takes up one controller slot for **its burn time + 1 minute** (lining up beforehand, then checking the result and planning the next burn or handing the flight off). The physics flyer sees the real burn time. The rails flyer makes the burn instantly but still uses the time the burn would take at full thrust.
- The load is tracked ahead of time from the planned burns, so the station shows a warning as it gets close to its limit.
- When full, burns go by priority, **1 (most urgent) to 10 (can wait hours)**. Starting values: critical (would lose the ship) 1, course correction 3, phasing 5, departure 10, docking approach 10. A higher-priority burn takes the slot. A burn that can't get a slot is missed, and the trip is planned again. A held departure waits at its port, and a held docking waits outside the station.
- Departure approval: checks that every future burn has a controller. If one won't, the player is asked at departure whether to fly it uncontrolled.
- An uncontrolled burn: time warp stops, a warning is shown, and the probe makes no burn. It coasts right past.
- Links: the controller needs a CommNet link to the probe **at upload and for the after-burn check**, not during the burn itself (the probe carries the stored burn). So a capture behind the Mun still happens. Controllers need a link to each other to hand a flight over (a Mun Station controller only knows what's coming because Kerbin's controller handed it off).

### Build stages

1. **Stage A: burns need a controller.** Built. Burn kinds and priorities, critical burns, upload at T−10 min, missed burns and the held replan, the after-burn check, departure and docking clearance, traffic control only from an on-duty Traffic Controller, controller log entries.
2. **Stage B: CommNet.** Built. A link at upload and at the check; hand-offs between SOIs.
3. **Stage C: capacity and priority.** Built. Slots, tiers from the controller log, the load forecast, bumping lower priorities.
4. **Stage D: departure approval.** Built. Every future burn checked before leaving; the "fly uncontrolled?" question.
5. **Stage E: Station pane.** Built.

Settings (Difficulty Settings → KSP Flight Planner → Traffic Controller): the rules on/off, the upload lead, the CommNet link rule, departure approval on/off, slots for new, experienced and veteran controllers and when each starts, the minute a burn holds a slot after it, and the priorities of corrections, phasing, transfers and departures.

### Station pane

Built 2026-11-10, not tried in game. The **Station** window manages one station: what's docked, what's coming and going, who's aboard, and the controllers' load against their capacity.

- **Where:** in flight (and the map), its own toolbar button shows the craft you're flying, station or not. At the space centre, clicking the **Administration building** opens it. With no strategies to manage (science mode, sandbox), KSP only says the building is closed, so that box is closed and the window opens instead, with every station listed down the left to pick from. A station is a craft of KSP's type Station, one routes fly to, or one someone's on duty aboard. A copy in the planner comes later.
- **Header:** name, body and orbit. **Controlled** (each Traffic Controller on duty in the station's own parts, with slots in use now, the busiest over the next 6 hours, any craft they're handling in the zone, and how much they've handled) or **uncontrolled airspace**.
- **Ports:** each of the station's own ports, numbered: free, booked by a route craft (with when it arrives), a craft coming in, one backing off, or occupied by a docked craft. A button beside each changes which route craft may use it (the port's right-click Traffic setting).
- **Docked craft:** each with its crew, whether it's quiet (engines, RCS and wheels off), and its route's state. A held departure can be cancelled; the craft stays docked.
- **Traffic, next 24 hours:** craft arriving (and their port), waiting 3 km behind for a port, or coming in now; departures held here (for clearance or your approval); and burns in this SOI, which its controllers upload. A burn of a craft only passing through is marked so. A burn gets a warning when no controller covers the SOI, or when more burns as urgent or more overlap its slot than the SOI's controllers have slots.
- **Crew:** everyone aboard, part by part (a docked ship's crew marked so), with trait and duty. Buttons put each on or off duty, and **Move…** lists the parts with a free seat to move them to (see [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md#moving-crew)). A pilot in the station's own parts without controls (a hitchhiker can) gets a hint to move to a cupola or pod, and the section says when no pilot in a part with controls could be the station's controller.
- **Power** and **Heat:** whether the whole stack's batteries last the night where it is, the Sun now (next sunset or sunrise), the charge flow measured from the batteries, and whether its radiators keep up with its drills and ISRU. See [POWER-AND-HEAT.md](POWER-AND-HEAT.md).
- **Supplies:** the station's own tanks, each resource with how full it is.

The window reads the station once a second, loaded or not. Changes made at the space centre (a crew move, a port setting) are saved to the save file straight away.

## Docked craft

Built 2026-11-09, not tried in game. A docked craft is made **quiet** so nothing on it fights the station: its engines are shut down, and its RCS blocks and reaction wheels switched off. Reaction wheels on docked ships pulling against the station's SAS are a known way to shake a big stack apart.

- It happens on every docking: the autopilot's, the player's, and docking on rails while you're away. The lighter of the two craft is taken to be the one docking, and only its own parts are touched.
- What was switched off is kept in the save (`QUIET` nodes in the Traffic scenario), part by part.
- On every undocking, the autopilot's or the player's, a quiet craft whose port is free again gets exactly those back: engines lit at zero throttle, RCS blocks and reaction wheels on. A message says what was switched on.
- Route craft that docked before this was built have no record. When one is undocked by hand, it's readied to fly whole, as the autopilot does when it undocks one.
- The Refueler switches the station's SAS off while it pumps, and back on when pumping ends however it ends: done, stopped, or the craft gone.

## Code map

| Where | What |
|---|---|
| `mod/KspFlightPlanner.AddOns/Traffic/TrafficService.cs` (paths below in `Traffic/` unless shown) | kRPC calls from the planner: start a trip, list routes, re-plan requests |
| `TrafficScenario.cs`, `Route.cs` | Saved routes and the per-frame loop that drives them |
| `RailsFlyer.cs`, `RailsDocking.cs`, `CourseCheck.cs` | The rails flyer, docking and undocking on records, course checks |
| `PhysicsFlyer.cs` | The physics flyer: attitude, burns, coming in, docking, leaving, traffic, safety stop |
| `PortBooking.cs`, `TrafficPortModules.cs`, `TrafficPorts.cfg` | Port booking and waiting, port settings and roles (ModuleManager patch) |
| `CrewRules.cs`, `Controllers.cs`, `TrafficSettings.cs` | Crew-rule gates: route qualification and training (`CanFly`, `Standings`), traffic control, controllers (`Upload`, `Checked`, `HoldsReplan`, `ClearDeparture`, `IsCritical`); per-save settings |
| `../KerbinResources/` | Kerbin Resources: flight log, personnel, crew window (code map in [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md)) |
| `DockedQuiet.cs` | Docked craft made quiet, and woken on undocking |
| `StationView.cs`, `StationWindow.cs` | The Station window: reading a station loaded or not, the window in flight, and taking over the Administration building |
| `../PowerHeat/` | Power and heat: reading the parts (`PowerReader`), day and night (`Sunlight`), the sums (`PowerBudget`), splitting an editor station into modules (`ModuleSplit`), the shared drawing (`PowerView`), and the editor's Power & Heat window ([POWER-AND-HEAT.md](POWER-AND-HEAT.md)) |
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
