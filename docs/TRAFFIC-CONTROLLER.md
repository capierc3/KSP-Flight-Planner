# KSP Traffic Controller: where it stands

KSP Traffic Controller flies planned trips on their own and keeps track of them: the planner plans a trip, the game add-on flies it, burns and docking included, whether or not the player is watching. It also runs station traffic (lanes, port booking, safety stops). Any craft flying a route this way is a "route craft": a cargo shuttle, a crew ferry, a relay keeping its orbit.

The planner's in-game add-ons are built as one DLL from `mod/KspFlightPlanner.AddOns`, with a folder each:

| Add-on | Folder | What it does |
|---|---|---|
| **Surveys** | `Surveys/` | Stations map the ore of the body they orbit ([SURVEY.md](SURVEY.md)) |
| **Traffic Controller** | `Traffic/` | Flies routes and directs station traffic (this page) |
| **Refueler** | `Refueler/` | Moves fuel between a docked craft and the station's tanks ([Fuel transfer and RCS balance](#fuel-transfer-and-rcs-balance)) |
| **Kerbin Resources** | `KerbinResources/` | Crew management: the flight log, personnel files and the crew window ([KERBIN-RESOURCES.md](KERBIN-RESOURCES.md)) |
| **Dispatch** (idea) | — | Will run routes by itself: repeating loops, triggers such as a station running low on fuel, crew rotation. It hands each trip to Traffic Controller to fly. Automated loops below are its first job. |

**Status (2026-11-08):** the core flying works. Both flyers, on rails and in physics, have been tested with three shuttles at once between Kerb Station, Mun Station and Min Station. What's left is mostly gameplay on top: automated loops, crew rules and cargo.

- Branch: `haulers`, last commit `39f5d6d`, pushed. Not yet merged into `develop`.
- Needs: [kRPC](https://github.com/krpc/krpc) and ModuleManager (both on CKAN). See the README.
- The maths and the reasons behind each rule: [How the numbers are worked out](#how-the-numbers-are-worked-out); the trip planning itself is [MATH.md §10](MATH.md#10-background-trips).
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
- [ ] Go to orbit for a route craft: a trip that ends in an orbit, not at a station ([MATH.md §10.4](MATH.md#104-trips-that-end-in-an-orbit))
- [ ] The safety stop with a kerbal on EVA
- [ ] Two route craft meeting at Kerb Station (uncontrolled), one coming in while the other leaves, with the sideways step-aside
- [ ] Refueler window: its own toolbar button, and fuel moves only to and from the vessels ticked as station tanks (other docked craft are left alone)
- [ ] The Refueler keeps pumping on rails (built 2026-11-18): after you switch vessels, go to the tracking station or the space centre, and through time warp there; started and stopped from the Station window too
- [ ] The Station window in the tracking station (built 2026-11-18): its toolbar button, every station listed, the craft you select picked

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

- **Where:** in flight (and the map), its own toolbar button shows the craft you're flying, station or not. In the **tracking station** the same button opens it with every station listed down the left, and selecting a craft in the tracking station's list or map picks it there (station or not); clicks on the window don't reach the map behind it. At the space centre, clicking the **Administration building** opens it. With no strategies to manage (science mode, sandbox), KSP only says the building is closed, so that box is closed and the window opens instead, with every station listed down the left to pick from. A station is a craft of KSP's type Station, one routes fly to, or one someone's on duty aboard. A copy in the planner comes later.
- **Header:** name, body and orbit. **Controlled** (each Traffic Controller on duty in the station's own parts, with slots in use now, the busiest over the next 6 hours, any craft they're handling in the zone, and how much they've handled) or **uncontrolled airspace**.
- **Ports:** each of the station's own ports, numbered: free, booked by a route craft (with when it arrives), a craft coming in, one backing off, or occupied by a docked craft. A button beside each changes which route craft may use it (the port's right-click Traffic setting).
- **Docked craft:** each with its crew, whether it's quiet (engines, RCS and wheels off), and its route's state. A held departure can be cancelled; the craft stays docked.
- **Traffic, next 24 hours:** craft arriving (and their port), waiting 3 km behind for a port, or coming in now; departures held here (for clearance or your approval); and burns in this SOI, which its controllers upload. A burn of a craft only passing through is marked so. A burn gets a warning when no controller covers the SOI, or when more burns as urgent or more overlap its slot than the SOI's controllers have slots.
- **Crew:** everyone aboard, part by part (a docked ship's crew marked so), with trait and duty. Buttons put each on or off duty, and **Move…** lists the parts with a free seat to move them to (see [KERBIN-RESOURCES.md](KERBIN-RESOURCES.md#moving-crew)). A pilot in the station's own parts without controls (a hitchhiker can) gets a hint to move to a cupola or pod, and the section says when no pilot in a part with controls could be the station's controller.
- **Power** and **Heat:** whether the whole stack's batteries last the night where it is, the Sun now (next sunset or sunrise), the charge flow measured from the batteries, and whether its radiators keep up with its drills and ISRU. See [POWER-AND-HEAT.md](POWER-AND-HEAT.md).
- **Supplies:** the station's own tanks, each resource with how full it is.
- **Fuel (Refueler):** when anything is docked, the Refueler's controls for the stack, loaded or not: the station-tank ticks, a craft's tanks against the station's, and Start/Stop to fill or unload it. See [Fuel transfer and RCS balance](#fuel-transfer-and-rcs-balance).

The window reads the station once a second, loaded or not. Changes made at the space centre (a crew move, a port setting) are saved to the save file straight away.

## Docked craft

Built 2026-11-09, not tried in game. A docked craft is made **quiet** so nothing on it fights the station: its engines are shut down, and its RCS blocks and reaction wheels switched off. Reaction wheels on docked ships pulling against the station's SAS are a known way to shake a big stack apart.

- It happens on every docking: the autopilot's, the player's, and docking on rails while you're away. The lighter of the two craft is taken to be the one docking, and only its own parts are touched.
- What was switched off is kept in the save (`QUIET` nodes in the Traffic scenario), part by part.
- On every undocking, the autopilot's or the player's, a quiet craft whose port is free again gets exactly those back: engines lit at zero throttle, RCS blocks and reaction wheels on. A message says what was switched on.
- Route craft that docked before this was built have no record. When one is undocked by hand, it's readied to fly whole, as the autopilot does when it undocks one.
- The Refueler switches the station's SAS off while it pumps, and back on when pumping ends however it ends: done, stopped, or the craft gone.

## How the numbers are worked out

The sums behind how the add-on flies route craft in physics, docks them, moves fuel, and orders station traffic, ports and crews. The trip planning itself (burns on rails, fuel, staying on course, trips that end in an orbit) is flight-planning maths and stays in [MATH.md §10](MATH.md#10-background-trips).

### The physics flyer: turning the ship


When a route craft is loaded with physics running near the player, the add-on flies it by writing its controls every physics tick, like an autopilot (only the toolbar's Traffic Controller panel switches it off: touching the controls doesn't, since what the last ship left on them read as the player's hands and turned it off on switching to it). It holds the ship still (damping any turn) until the next burn is 10 minutes away, then turns it so its engines face the burn, along the node axes of [MATH.md §1.8](MATH.md#18-maneuver-node-axes) *at the burn's time*: a direction fixed in space, so the ship turns once and holds (facing the axes as they are now made it follow them round the orbit). Then it flies the burn ([The physics flyer: flying a burn](#the-physics-flyer-flying-a-burn)). RCS is on only while the autopilot is moving the ship sideways: a ship's RCS rarely pushes through its centre of mass, so holding a heading on RCS nudges it off course over a long wait. When the autopilot lets go of a ship, it puts the player's RCS switch back and turns SAS on, so the ship holds still instead of tumbling.

The turn is worked out on each of the ship's three control axes (pitch, roll, yaw) separately. The error is the rotation from where the ship points to where it should, as an angle e on each axis (rad). The ship can change its turn rate by at most α = τ / I on that axis, from the torque τ its reaction wheels, RCS and gimbals can make (the smaller of the two directions) and its moment of inertia I. It wants the turn rate that still lets it stop in time, with a margin, but no faster than 0.5 rad/s, and gently at the end:

```
ω_want = sign(e) · min( √(2 · 0.6·α · |e|),  |e| / 1.5 s,  0.5 rad/s )
u      = clamp( (ω_want − ω) / (0.25 s · α),  −1, 1 )         the control input on that axis
```

ω is the ship's turn rate on that axis now. KSP turns the ship against its input (torque = −u · τ on each axis), so the control written is −u. In case that convention is ever the other way, each axis checks itself: when a strong input keeps turning the ship the opposite way (15 ticks more often than not), its sign is flipped and logged. Once an axis has agreed 15 ticks more often than not, it's settled for good, so pushes the controls didn't make (docking magnets, a bump) can't flip it.

Example: a 13 t shuttle with I = 20 t·m² on pitch and 5 kN·m of reaction wheel torque, so α = 0.25 rad/s², turning 90° (1.571 rad). It speeds up to the 0.5 rad/s cap in 2 s (0.5 rad), and braking at 0.6α = 0.15 rad/s² takes 0.5² / (2 × 0.15) = 0.833 rad, so it coasts the 0.238 rad between for 0.48 s and brakes for 3.33 s: about **5.8 s**, then a short settle. 10° out it wants min(√(2 × 0.15 × 0.1745), 0.1745 / 1.5) = **0.116 rad/s**.

- Code: `PhysicsFlyer.Attitude.Drive`, `.Learn`, `.AvailableTorque`, `PhysicsFlyer.Pilot` (mod/KspFlightPlanner.AddOns); `TrafficPanel`
- Source: [MATH.md §1.8](MATH.md#18-maneuver-node-axes); a rate profile that can always stop in time (bang-bang with a margin), per axis, as kRPC's and MechJeb's autopilots do; KSP's `ITorqueProvider`

### The physics flyer: flying a burn


A real burn takes time, so it's centred on the planned time t_b: half its Δv before, half after. The ship gets lighter as it burns, so the first half of the Δv takes a little longer than the second, and it starts that much earlier than half its length. A burn under 1 m/s is made on RCS instead (bigger ones on the main engine: RCS's Isp is low and its tanks small; a 4.2 m/s correction on RCS took about 12 of the shuttle's 150 units in the test of 2026-11-06), when the monopropellant can give at least four times it (rocket equation with the thrusters' vacuum Isp, about 240 s): no turning, pushed straight along the Δv still to go, with RCS's finer control. The autopilot sets the engines' thrust limiters itself for each burn, and leaves them where it last set them (the player's setting from before the trip is rarely the one wanted after it): 100%, so a limiter left low for docking doesn't stretch a burn out, or lower for a burn so small it would be over in under 2 s. The length at full throttle comes from the rocket equation ([MATH.md §7.1](MATH.md#71-a-vessels-remaining-δv-rocket-equation)) with the lit engines' full thrust F (limiters at 100%) and their thrust-weighted vacuum Isp, or the main engine kind's if none is lit yet:

```
vₑ = Isp · g₀
t₁₀₀  = m₀ · vₑ / F · (1 − e^(−Δv / vₑ))         at 100%
t     = max(t₁₀₀, 2 s)                           limiter = 100% · min(1, t₁₀₀ / 2 s), at least 1%
t_½   = t · (1 − e^(−Δv / 2vₑ)) / (1 − e^(−Δv / vₑ))     the time for the first half of the Δv
start at t_b − t_½
```

At the start the add-on works out the orbit the burn should leave the ship on, exactly as the rails flyer would make it ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft): the ship's position and velocity on its orbit at t_b, plus the planned Δv along the node axes), lights the main engines if none is lit, and from then on steers on the Δv still to go, as KSP's own maneuver node does:

```
Δv_left = v_goal(t_b) − v_ship(t_b)              both orbits' velocities at t_b (the ship's orbit as it is now)
throttle = 0                                      while the engines point more than 5° off Δv_left
throttle = clamp(|Δv_left| / a_max, 0.02, 1)      otherwise: a_max = F / m now, so it eases off over the last second's worth
done when |Δv_left| < 0.05 m/s, or it starts growing again once under 1 m/s (the last bit overshot)
```

Because it aims at the goal orbit, not at a fixed direction, the turning of the Δv over a long burn is followed as it goes. It stops early, and says why, when the engines have had no thrust for 3 s (out of fuel), or the ship leaves the sphere of influence mid-burn. Whatever's left is the course check's: before each real burn when the player has stayed near the ship (it never went back on rails), or once it's back on rails ([MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again)). Time warp is stopped 30 s before the burn starts (plus a frame's worth at the current rate) when the craft is loaded near the player, so it unpacks and the autopilot can turn it first; physics warp is stopped during the burn.

Example: the round 1 shuttle (18.17 t) with a Skipper (650 kN, Isp 320 s in vacuum), 252 m/s: vₑ = 320 × 9.80665 = 3,138.1 m/s, t = 18.17 × 3,138.1 / 650 × (1 − e^(−252 / 3,138.1)) = 87.72 × 0.07716 = **6.8 s**, so it lights 3.4 s before the planned time, with the limiter at 100% even if it was left at 2.5% (which would have made it a 270 s burn). A 5 m/s correction would take 0.14 s at 100%, so the limiter goes to 100 × 0.14 / 2 = **7%** and it takes 2 s. At about 38 m/s² (650 kN on 17 t), the throttle starts easing off with 38 m/s left; with 1 m/s left it's at 2.6%.

Example (the capture of 2026-11-06): 840.5 m/s on 28.32 t with a Skipper: t = 28.32 × 3,138.1 / 650 × (1 − e^(−0.2678)) = **32.1 s**; half its length is 16.1 s, but the first half of its Δv takes 32.1 × (1 − e^(−0.1339)) / (1 − e^(−0.2678)) = **17.1 s**, so it lights a second earlier.

**Cut short.** If physics goes off mid-burn (the player leaves for the tracking station, or switches far away), what's left, `Δv_left`, becomes the burn again, split into prograde, normal and radial on the orbit the ship has now at t_b, and the rails flyer makes it straight away, as it makes any burn ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)). There's no course check first: it would find the half-made burn off course. Dropping the rest instead left a shuttle 8 s into a 252 m/s burn leaving the Mun on a path into it, paused off course, and it crashed (the test of 2026-11-08). Example: 252.2 m/s to go at the start, cut at 8 s with 23 m/s left (say) along the prograde: the burn becomes 23 m/s prograde at t_b, made on rails, using that burn's fuel from the tanks.

**Time warp.** A route craft near the player stops any time warp 30 s before its burn starts (plus a frame's worth at the rate: rate × 0.1 s), and keeps it off until the burn is done: warp the player started to reach another ship hours away too, KSP's *Warp to* included (it's cancelled, or it raises the rate again). Warp would pack the ship and its burn would be made in one jump on rails ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)). The same goes for a route craft docking near the player ([The physics flyer: docking](#the-physics-flyer-docking)) and, as before, one backing off ([The physics flyer: leaving a station](#the-physics-flyer-leaving-a-station)).

- Code: `PhysicsFlyer.BurnSeconds`, `.StartLead`, `.RcsDeltaV`, `.StopWarpForBurn`, `PhysicsFlyer.Pilot.StartBurn`, `.FollowBurn`, `.HandBackBurn`, `.ThrustAxis`, `CourseCheck.CheckIfResting` (mod/KspFlightPlanner.AddOns)
- Source: [MATH.md §1.8](MATH.md#18-maneuver-node-axes), [MATH.md §7.1](MATH.md#71-a-vessels-remaining-δv-rocket-equation), [MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft); steering on the remaining Δv to a goal orbit, as KSP's maneuver node (and MechJeb's node executor) do

### The physics flyer: coming in to the station


When the player is at the station (it's loaded) with the autopilot on, the craft isn't parked at the meeting ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)). It's lined up as soon as all that's left is the match at the meeting and it's within 10 minutes of the meeting or 30 km of the station, while it's still far out of sight; waiting for the meeting time could let it come into KSP's range on its intercept with no room to stop. The match's fuel is paid as the rails flyer would. The add-on picks a free port on the station that one of the craft's ports fits (the one facing most towards the craft), and puts the craft on that port's axis, out of sight:

```
spot   = port position + axis · 150 m                      where it will stop
start  = port position + axis · (150 m + 2,400 m)          just outside the 2,250 m at which KSP draws ships
r      = r_station(now) + (start − station position)        only the 2.4 km offset goes through the world/orbit swap
v      = v_station(now) − axis · 20 m/s                     coming straight in
```

Then time warp stops (on rails it would sail through the station), and the craft's physics range is raised: KSP turns physics on for a ship near the one being flown only within 200 m in orbit, so the craft's own pack/unpack distances are set to 2,350 m / 2,200 m, inside its load distances. It comes into view at 2.25 km, physics comes on at 2.2 km, and the autopilot brings it in. The speed it wants falls off towards the spot so it can always stop there:

```
d      = |r_ship − spot|,   v = v_ship − v_station
b      = min(½ · F/m, 2.5 m/s²)                           the braking it plans on
v_want = −(r_ship − spot)/d · min(20 m/s, √(2·b·(d − 2 m)), d / 10 s)
Δv     = v_want − v                                        fire along Δv when it's over 0.6 m/s (until under 0.2),
                                                           pointed within 8°, throttle = clamp(|Δv| / (F/m), 0.02, 1)
```

While it's only coasting it faces back along its drift, ready to brake, so the flip happens before the braking burn. Within 20 m of the spot and under 0.3 m/s it has arrived, and docking takes over (for now, the placement of [MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)). If the player leaves before then, it never reaches physics range within 5 minutes, or the autopilot can't fly it, it's parked as in [MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft) instead.

**Braking in from an intercept.** The match at the meeting is never flown as a burn by the physics flyer: centred on the meeting time, half of it would come after the craft reached the station's position, at full speed. (That is how the test of 2026-11-05 ended: an 845 m/s match flown for real, then cut off mid-burn, put the shuttle through the station.) Instead, with the craft loaded (the player riding along), it switches to coming in when

```
t ≥ t_meet − |v_rel| / (½ · F/m) − 60 s,   or it's within 20 km
```

and brakes along the same profile, now allowed to be fast far out: v_want = min(√(2·b·(d − 2 m)), max(20 m/s, d / 30 s), d / 10 s), with b = ½·F/m while it's faster than 20 m/s (2.5 m/s² as before once it's slow).

**Flown as a pilot flies an intercept.** It comes in to a *hold point* near the station on the side it arrives from (max(50 m, both ships' sizes + 15 m) out), and stops there; docking ([The physics flyer: docking](#the-physics-flyer-docking)) then takes it round to the port on RCS.

*Far out*, it watches the closest approach to the hold point the way KSP's map marks it: from both orbits as they are, the distance between the ship and the hold point sampled 240 times over the next hour or so, then a golden-section search round the nearest, twice a second. Two ships on nearby orbits don't close in straight lines (their relative path curves over minutes), so anything worked out from the straight drift goes wrong far out: in the test of 2026-11-06 the transfer burn left it on course for 67 m, and straight-line steering turned that into a 3.9 km pass. At checkpoints by the time to closest approach it corrects once if the miss is over the allowance, then coasts:

```
time to closest approach:  over 10 min   10 min   5 min   2 min
allowed miss:               800 m        800 m    200 m   100 m      (and a check as soon as coming in starts)
correction: Δv solved so the ship is at the hold point at the closest-approach time (at least 90 s away):
            g(Δv) = r_ship(t_ca; v + Δv) − (r_station(t_ca) + offset),   Newton on the three components, slopes by 0.05 m/s nudges
```

A correction is flown as a burn: towards the orbit it should leave the ship on, a fixed Δv in space, the same Δv still to go as [The physics flyer: flying a burn](#the-physics-flyer-flying-a-burn). (Flown as a velocity relative to the station instead, it kept RCS on: the relative velocity of two nearby orbits changes by itself as they curve, so it chased it, and pushed the closest approach back out, 680 m corrected to 340 m two minutes later, 2026-11-06.) Under 1 m/s it's made on RCS, with no turning; anything bigger on the main engine. Between checkpoints time warp is allowed; it stops 20 s before the next one, and the Traffic Controller panel has **Warp to next check**.

*Close in* (under a minute to the closest approach, within 1 km, or when it's time to brake: within 1.5 × its stopping distance at half thrust, plus 200 m), straight-line steering is accurate, and it flies the last of it as a pilot does: along the line it wants to close at about d / 30 s (the half-minute time to go), never faster than it can stop (√(2·b·d)) and never faster than it already is, braking only when it's closing more than a fifth (or 1 m/s) too fast. Far too slow, under half of `cruise = min(want, max(1, d / 60 s))`, it speeds up to `cruise` (it used to wait to be nearly stopped, under 0.2 m/s, before pushing, so a ship at 0.25 m/s crawled: 728 m would take 49 minutes, and over that long the orbit bends the straight line, by about 3n²·d ≈ 0.02 m/s² sideways at 728 m round Kerbin, so each correction left it further off, the test of 2026-11-08; at 728 m, cruise = min(24, 12.1) = 12.1 m/s, about a minute plus braking). Sideways it keeps the pass within a tenth of the distance (5 m at least). No time warp close in.

The profile is a limit to slow down to, never a speed to reach: v_want is also capped at max(v_closing, 20 m/s), the speed it's already closing at. (In the test of 2026-11-06 it began braking in at 48 m/s from 15.7 km, sped up to the d / 30 s limit, over 375 m/s, and then braked it all off again.) Under 60 m/s the thrust limiters go down so full throttle is a gentle 3 m/s², finer than a few percent of a big engine: on the shuttle's 21 t with a Skipper, 100 × 3 / (650 / 21) = **9.7%**. The last of it, under 2 m/s and within 300 m, is on RCS. Until the station is loaded (riding in, it loads at 2.25 km), the spot is 300 m short of the station on the craft's side; then the spot off a free port facing it.

**The collision guard.** Coming in, the closest pass to the station's centre on the present drift is worked out every frame:

```
r = r_ship − r_station,  v = v_ship − v_station
t_ca = −(r · v) / |v|²,   pass = |r + v · t_ca|
guard  when  t_ca > 0,  pass < size_station + size_ship + 20 m,  and  t_ca < |v| / (0.8 · F/m) + 5 s
```

Then, whatever else it was doing, it burns flat out back along its drift and away from the station's centre (aim = −v̂ + pasŝ), and says so on screen.

Example: the crash's 845 m/s with a Skipper on 16 t (F/m ≈ 40 m/s²): braking starts 845 / 20 + 60 = **102 s** before the meeting, about 86 km out; the speed it wants is d / 30 s, 667 m/s at 20 km, 200 m/s at 6 km, 20 m/s from 600 m, then d / 10 s. Following d / 30 s needs v / 30 s of braking, 22 m/s² at its fastest: about half the Skipper's push.

Example: the shuttle with a Skipper (about 38 m/s² at full throttle, so b = 2.5 m/s²). It coasts in at 20 m/s until d / 10 s drops below 20 m/s, at d = 200 m, which is 350 m from the port: there it starts braking at 20 / 10 = 2 m/s², easing off as it slows (the speed it wants halves every 10 × ln 2 = 6.9 s), and it covers the 200 m down to 20 m in 10 × ln(200 / 20) = **23 s**. At 38 m/s², 2 m/s² is a throttle of about 5%.

- Code: `RailsFlyer.LineUp`, `.WatchArrival`, `.WatchApproach`, `.Park`, `Ships.PickStationPort`, `.RaisePhysicsRange`, `PhysicsFlyer.Pilot.Approach`, `.TimeToBrake`, `.StartApproach`, `.AvoidStation` (mod/KspFlightPlanner.AddOns); `RouteReplan.Stretched` (Core)
- Source: [MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft), [The physics flyer: flying a burn](#the-physics-flyer-flying-a-burn); KSP's vessel ranges (`VesselRanges`: load 2,250 m, unpack 200 m in orbit by default); a speed profile that can always stop in time, as MechJeb's docking autopilot uses

### The physics flyer: docking


Once the craft has stopped off the port ([The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station)), or is parked beside the station ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)) and the player comes near, a route craft with RCS docks itself; one without is placed at the port ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)). The ship's port faces the station's port throughout (the shortest turn, roll left as it is), and RCS moves it, as MechJeb's docking autopilot does: round the station to a point straight out from the port, then in along the port's axis.

```
a      = the station port's axis (out of the port),  p = its position,  q = the ship's port's position
along  = (q − p) · a              lateral = (q − p) − a · along
clear  = size_station + size_ship + 10 m          (size = furthest part from the ship's origin)
entry  = p + a · max(clear, 30 m)
```

- **Final approach**, when it's in front (along > 0), within max(0.5 m, along/10) of the axis and facing the port within 3°: v_want = −a · min(0.8, max(0.15, along / 20 s)) − lateral / 5 s, slowing to 0.15 m/s for the magnets.
- **Otherwise to the entry point**, straight if that line keeps at least `clear` from the station's centre, or else 30° at a time round a sphere of radius `clear` about it: v_want towards the goal at min(2 m/s, √(2·½·a_RCS·d), d / 10 s).

Facing the port leaves the ship free to roll about the port's axis, so it's rolled to put the sideways error along its X axis: one pair of thrusters fixes it, not two at an angle (the nearer way round). Under half a metre of error the roll is held: kept as a direction in space for the ship's X axis, not as an angle from its roll now. An angle kept that way stays ahead of the ship however far it rolls: in the test of 2026-11-06 the craft rolled slowly, 8° off, 35 m out, and never started in, since the final approach waits for the ports to face each other within 3°. RCS makes up the difference, relative to the station, in the ship's own axes (KSP's RCS reads X right, Z along the nose, Y the third, each the other way to the push: the tests flipped all three), aiming to close it in half a second and doing nothing under 2 cm/s, to save monopropellant; a_RCS is the thrusters' power over the mass, shared over six directions. Each axis checks its sign against how the ship really moves, as the turning does ([The physics flyer: turning the ship](#the-physics-flyer-turning-the-ship)). It's docked when its port is (it may become part of the station); after 10 minutes it gives up and is placed at the port instead. Docked, the craft's own engines are shut down and its RCS blocks switched off, so nothing on it can fire by accident at the station.

Example: from the entry point 30 m out, it closes at 0.8 m/s until along / 20 s drops below that, at 16 m (17.5 s); then the speed falls with along, 16 m to 3 m in 20 × ln(16 / 3) = 33.5 s; then 0.15 m/s for the last 3 m to the magnets' reach (about 0.25 m), 18 s: about **69 s** from the entry point to the magnets.

- Code: `PhysicsFlyer.Pilot.Dock`, `.DockingAttitude`, `.Translate`, `.SegmentClear`, `.AroundStep`, `PhysicsFlyer.SignCheck`, `Ships.PortFor` (mod/KspFlightPlanner.AddOns)
- Source: MechJeb's docking autopilot (the route round the target, the corridor, speed caps), read for its ideas; [The physics flyer: turning the ship](#the-physics-flyer-turning-the-ship)

### The physics flyer: leaving a station


A trip can start with the craft docked (From here › Fly as the docked craft, then Fly it in the background). The add-on finds it by its own name, as KSP keeps it on its docking port, checks the plan ([MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again)) on the docked stack's orbit, which is the craft's until it moves off, and only then undocks it. Then, with physics on, it backs straight out along the station port's axis on RCS, holding still:

```
clear  = max(30 m, size_ship + 20 m)
v_want = axis · min(1 m/s, max(0.2 m/s, (clear − distance) / 5 s))        relative to the station, as in [The physics flyer: docking](#the-physics-flyer-docking)
```

until it's `clear` from the port. It doesn't stop there (stopping dead under 0.03 m/s on RCS hung on for minutes in the test of 2026-11-08): it goes straight into the push below, changing its backing-off velocity for the push to within 0.1 m/s. Left 30 m away and nearly still, its path on rails would swing round the station's every orbit and can pass back through it (a 30 m offset outward drifts about 6π × 30 = 565 m along the orbit each orbit). So it pushes backwards along the orbit, 0.5 m/s at a time: a slightly lower, faster orbit that drifts steadily ahead and away (by Clohessy–Wiltshire, about 3 · 0.5 · t m along the orbit: **4.2 km** by a burn 47 minutes later). Then it checks its path, left alone until the first burn, against the station's, 400 points, counting only the part after it has first got 200 m away (the part where it could swing back; counted from the start, a ship 30–70 m away always looked too close, and it pushed four times, 2 m/s, in the test of 2026-11-06): if they come within 100 m, it pushes again (four pushes at most). The pushes are on RCS while the monopropellant covers four times the push (0.5 m/s on the 30 t shuttle is 30,000 × (1 − e^(−0.5 / 2,354)) ≈ 6 kg, about **1.6 units**), so there's no turning near the station; otherwise on the main engine, at 0.5 m/s², once it's 100 m out. Time warp is held while it backs off near the player. Only then does it turn for its first burn. Without RCS it drifts clear on the undocking push. A burn that's due (within 30 s of its start) ends the backing off once it's 15 m out, and 5 minutes is the most it waits. Going on rails ends it too (nothing to bump into there).

The plan was made from the station's orbit, and backing off moves the craft off it, so the plan is checked ([MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again)) the moment backing off ends. Even 30 m and a few cm/s can grow past the 1 km line by a burn hours away (a 30 m offset outward from the planet drifts about 6π × 30 = 565 m along the orbit every orbit, by the Clohessy–Wiltshire equations of [MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again)). When it does, the planner plans the trip again from where the craft really is, minutes before the first burn rather than at it, and the game says so as routine ("… is clear: the planner plans its trip again from here"), not as off course.

Example: the shuttle, about 10 m long, backs off to max(30, 10 + 20) = **30 m**: 1 m/s until 25 m (5 s × 1 m/s from the end), then slowing (the gap left halves every 3.5 s) to the 0.2 m/s floor at 29 m, and 0.2 m/s for the last metre: 25 + 5 × ln 5 + 5 = **38 s** in all.

Undocked, on rails or loaded, the craft is made ready to fly: every engine lit at zero throttle, its RCS thrusters and RCS switched on (docking shut them down, [The physics flyer: docking](#the-physics-flyer-docking)). The thrust limiters are left as they are. A player who switches to it mid-trip finds it ready to take over.

- Code: `TrafficService.StartRoute`, `Ships.FindDockedCraftPort`, `.VesselOfPart`, `.ReadyToFly`, `RailsDocking.ReadyToFly`, `PhysicsFlyer.Pilot.BackOff` (mod/KspFlightPlanner.AddOns); `FromHereViewModel.FlyInBackgroundAsync` (App)
- Source: [MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again), [The physics flyer: docking](#the-physics-flyer-docking)

### Fuel transfer and RCS balance


**Fuel.** The Refueler moves liquid fuel, oxidizer and monopropellant between one craft docked in a stack and the station's tanks, either way, started and stopped by hand. Its controls are in its own window in flight (its own toolbar button, apart from the Traffic Controller window, for the stack being flown) and in the Station window's **Fuel** section (in flight, the tracking station and the space centre, for any station). The stack is first split back into the vessels that docked to make it, as the planner does ([SAVE-AND-LIVE-GAME.md “Importing vessels from a KSP save”](SAVE-AND-LIVE-GAME.md#importing-vessels-from-a-ksp-save), `DockedCraft.Pieces`): the part tree is cut at every docked pair of ports, and each piece takes its name and type from its port's record of the vessel it came with (the root's piece takes the stack's name). Each piece is a **station part** or a **craft**: a craft by default when it's a ship, lander, rover or plane with a command part and an engine (the planner's rule, so both show the same split), otherwise a station part. The player can tick or untick any piece; the mark is kept in the save by vessel name. The station's tanks are the parts of every station part. Other craft docked there are never touched, so filling one shuttle doesn't drain the lander or the other shuttles beside it.

**On rails.** Start makes a pump job, kept in the save (`PUMP` nodes in the Refueler scenario: stack, craft, fill or unload, the Δv to keep, the game time it last pumped to), one per stack. The job runs in the Refueler scenario, not in a window, four times a real second, in flight, the tracking station and the space centre, so it carries on when you close the window, fly something else, or leave the flight scene. When the stack is loaded it moves fuel between its parts; when it isn't, between its saved parts' tanks, the stack split the same way from the saved records (a part joins its parent by docking when its port is docked and either port's record names the other part, `dockUId`; the child side's `DOCKEDVESSEL` names the vessel it came with). It ends by itself when the craft is full (or empty), when unloading reaches the Δv to keep, or when the craft has undocked, with a message on screen. Nothing runs in the editor or main menu, where game time doesn't pass either. Quickloading takes the jobs back to the save's.

Fuel is pumped, taken from every unlocked tank in proportion to what each holds and put into every unlocked tank in proportion to its free room. Nothing is made or lost. The flow is a pump's, set by the docking port the craft is joined by, and it runs by game time, so it carries on through time warp. In zero g the propellant doesn't settle, so real spacecraft move it slowly through bladders or pressurant gas; a station pump through a standard port is taken as 5 kg/s. For comparison, an aerial-refuelling boom moves about 45 kg/s, and ISS refuelling from Progress about 0.05–0.1 kg/s.

| Port (node type) | Flow *q* (setting) |
|---|---|
| Junior (size0) | 1 kg/s |
| Standard (size1, and any port of unknown size) | 5 kg/s |
| Senior (size2 and bigger) | 20 kg/s |

Each step, with Δt the game time since the last step. For each fuel *i*, *M_i* is what can still move: the smaller of what one side holds and the other has room for. *ρ_i* is its density (t/unit).

```
M      = Σ M_i·ρ_i                    t still to move
budget = q · Δt / 1000                t this step
move_i = M_i · min(1, budget / M)     units: shared by what's left of each fuel, so they all finish together
time   = M · 1000 / q                 s, shown in the window as the time left
```

Example: filling an empty Kerb Shuttle mk2 (1,634 LF, 1,998 Ox, 150 Mono; ρ = 0.005, 0.005, 0.004 t/unit) from a station with plenty. M = 8.17 + 9.99 + 0.60 = 18.76 t. Through a standard port that's 18,760 / 5 = 3,752 s, about 1 hour 3 minutes. Through a Junior port it's 5 h 13 min, and through a Senior port 15 min 38 s. At 100× time warp the standard-port hour passes in under 40 real seconds. Every step moves the same share of each fuel (LF 8.17 / 18.76 = 43.6 % of the mass), so all three run out together.

On 2026-11-09 Kerb Station (406 parts, no rigid joints) broke apart during a fill at the old rate, a craft's tanks in 30 s. Stock KSP's own part-menu transfer is faster still: each receiving tank gains 1/20 of its capacity a second (`UIPartActionResourceTransfer.UpdateItem`), so the rate alone probably wasn't the cause. The pump is slower for realism, not as the fix.

The station's SAS is switched off while fuel is pumped and the stack is loaded, and back on after (docs/TRAFFIC-CONTROLLER.md "Docked craft"); a stack that was unloaded when its pumping ended gets SAS back the next time it loads. Unloading stops by itself when the craft's Δv ([MATH.md §10.2](MATH.md#102-the-fuel-a-background-burn-uses), from its own saved parts when it isn't loaded) is down to the figure the player keeps (300 m/s to start with, enough to deorbit from a low orbit). Unloading pumps a minute of game time at a time, the Δv checked before each minute, at most 50 minutes a step, so it can't run far past that figure; at high time warp it falls behind game time and catches up over the next steps.

Example: Kerb Station (station) with Kerbin Station - Fuel Tank (probe), Kerb Station - Electrical (probe), Homeward Bound 4 and three Kerb Shuttle mk2s (ships with engines). The station's tanks are the station core, the fuel tank and the electrical module; filling Kerb Shuttle mk2 C takes fuel from those three only, in proportion to what each holds.

**RCS balance.** One thrust limiter s_m ∈ [0.05, 1] per RCS block, chosen so a push in each of the six directions turns the ship as little as it can. For each block m and push d, F_md and τ_md are the force and torque (about the centre of mass) of its thrusters that fire for that push (a thruster fires when its thrust is within about 73° of the push; each push and its opposite are both counted, so which way a thruster's axis points doesn't matter):

```
minimise   Σ_d | Σ_m s_m · τ_md |²   (+ a small pull of each s_m towards 1, to keep thrust)
by projected gradient descent (2,000 steps), then scaled so the largest s_m is 1
imbalance  = max_d |Σ_m s_m τ_md| / (|Σ_m s_m F_md| · size)        0 is perfect
```

The limiters are only changed when that makes it better. The autopilot balances a route craft's RCS the first time it needs RCS on a trip, and the panel has a button for the ship being flown. A block has one limiter for all six directions, so it's a best fit, and it shifts as fuel drains (the centre of mass moves).

- Code: `FuelTransfer.Pieces` (loaded and saved), `.StationTanks`, `.Move`, `StackPiece.IsStation`, `.Tanks`, `.DeltaV`, `FuelTransfer.PumpKgS`, `.Movable`, `.Pump`, `RefuelerScenario.Step`, `RefuelerView`, `Ships.DeltaVLeft` (saved parts), `RefuelerSettings`, `RcsBalance.Balance`, `TrafficPanel.DrawRcs` (mod/KspFlightPlanner.AddOns)
- Source: [MATH.md §10.2](MATH.md#102-the-fuel-a-background-burn-uses); pump rates: KC-135 boom transfer rate (USAF fact sheet), ISS Progress refuelling (NASA); stock rate read from KSP 1.12 Assembly-CSharp; RCS balancing as RCS Build Aid shows the imbalance and MechJeb's RCS balancer corrects it, read for the idea

### Docking and undocking on rails


With nobody near (the tracking station, or flying something else), a route craft docks and undocks on the saved records, as KSP would leave them, so the station shows it docked whenever you visit. The parts are written in tree order (each after its parent), as KSP loads them. Every link a part makes by part number is renumbered with it: parent, attach nodes (`attN`, `srfN`) and symmetry partners (`sym`). Before the game's vessels are replaced, each new record is checked (`RailsDocking.Check`): parents first, links in range, symmetry partners of the same part, docked ports' partners aboard; a record that fails is never swapped in, and the craft stays docked (or parks). A backup save ("persistent-before-rails-dock") before each one can be switched on for debugging (`RailsDocking.KeepBackups`); it is off, as each is a full save.

Each part's record holds its parent part and its position p and rotation q relative to the ship's root part. Two docked ships are one record: the docking port of the one that docked hangs off the other's port as its parent ("Docked (docker)" and "Docked (dockee)", each naming the other port's part), the rest of it hangs below its port, and each port remembers its own ship's name, type and root part.

**Undocking** cuts the craft's branch (its port and everything below it) into a record of its own, turned so its own root part r is the root again (the parent links on the way up from r reversed), with every part re-expressed from r, and both ports set free:

```
p_i' = q_r⁻¹ · (p_i − p_r),    q_i' = q_r⁻¹ · q_i
```

It's put on the station's orbit 60 m ahead (the mean anomaly moved on by n · 60 m / v), checked at once ([MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again)), and flies its trip from there.

The plan was made from the station's position, and on the same orbit 60 m ahead is simply where the station will be Δt later. So every burn is brought forward by that much, and the craft flies the planned path itself:

```
Δt = 60 m / v,    v = √(μ / a)          (the mean anomaly moved on, n · 60 / v, over n)
burn UTs  →  UT − Δt
```

Flown at the planned times instead, each burn came Δt further round the body than planned. Inside the body's pull that turns the whole escape path by θ ≈ 60 m / r, so it leaves with its velocity off by v∞ · θ, a fraction of a m/s, and days of coasting stretch that to kilometres. The check right after undocking saw it and called the ship off course, and the planner planned it again from where it was (and came out with nearly the same burns: 1,200 → 1,199 m/s). It was not a settling problem: KSP gives a loaded record its whole orbit at once (`ProtoVessel.Load` → `OrbitSnapshot.Load` → `Orbit.Init`), so checking a frame later would have said the same. With the burns brought forward, the path is the plan's, Δt early. What's left is the meeting's own Δt (the craft is about V · Δt along its path at the meeting time, a few hundred m in low orbit), well inside the 1 km tolerance, and braking in takes it up.

Example (Protogen's Kerb Shuttle mk2 C leaving Mun Station, 2026-11-07): low Mun orbit at v ≈ 550 m/s gives Δt ≈ 0.11 s and θ ≈ 60 / 250,000 ≈ 2.4 × 10⁻⁴ rad. Leaving the Mun at a few hundred m/s, that's about 0.1 m/s off, which after the coast to Kerbin met Kerb Station 6,483 m off at 23.4 m/s. Leaving Minmus (v ≈ 170 m/s, Δt ≈ 0.35 s, θ ≈ 8 × 10⁻⁴) for Mun Station, a longer trip through two spheres of influence, it was 86.8 km off.

**Docking** picks a free port on the station and a free one on the ship that fits it (same size, from the part designs), and moves every part of the ship into the station's frame so the ports are face to face. From each port's part design comes where its face is on the part (l, λ); in each ship's frame the face is at P = p + q·l facing R = q·λ. The ship's port is turned to face the station's (forward against forward, up along up) and moved onto it:

```
R_h' = LookRotation(−R_s·forward, R_s·up)
turn = R_h' · R_h⁻¹,     shift = P_s − turn · P_h
p_i' = shift + turn · p_i,    q_i' = turn · q_i            for every part of the ship
```

The ship's tree is re-rooted at its port, which then hangs off the station's port; both ports are marked docked to each other, and the ship's engines and RCS blocks are switched off. Then the two old vessels are taken out of the game (not destroyed the usual way, which would count their crew as lost) and the new ones loaded as KSP loads a save's vessels.

Example (Protogen's Kerb Shuttle mk2 at Mun Station): the shuttle's port is at (−1.503, −9.097, 0.000) and the station's at (−0.938, −9.097, 0.000) in the station's frame, 0.566 m apart along x, the two halves of a Clamp-O-Tron pair: the shuttle's port hangs off the station's, and undocking puts the shuttle's command pod back at the root of its own record.

**Docking once you've left.** A route craft parked beside its station ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)), or part way in with the physics flyer ([The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station), [The physics flyer: docking](#the-physics-flyer-docking)) when the player leaves, isn't left waiting for the player to come back and watch: once neither it nor the station is loaded (the tracking station, or flying somewhere else), it's docked on rails as above, trying every 30 s if it can't (no free port it fits, say). Coming in when the player leaves, it docks on rails straight away rather than parking. Left 30 m from its port for the tracking station, a shuttle used to sit parked till the player came back (the test of 2026-11-08). Loaded, the physics flyer (or the stand-in, [The physics flyer: docking](#the-physics-flyer-docking)) docks it as before.

- Code: `RailsDocking.Undock`, `.UndockLeadS`, `.Dock`, `.FindDockedIn`, `.Reroot`, `.Build`, `.PortPose`, `.Replace` (mod/KspFlightPlanner.AddOns); `TrafficService.StartRoute`, `RailsFlyer.Arrive`, `.DockWhileAway`, `.WatchApproach`; From here's ship picker outside flight (App)
- Source: KSP's save format (VESSEL › PART: parent, position, rotation, attN; ModuleDockingNode: state, dockUId, DOCKEDVESSEL)

### Station traffic


Several route craft can use one station. Ports are claimed everywhere. In physics, a station with traffic control also gives clearance into its keep-out zone and has lanes; one without leaves its route craft to their pilots.

**Ports are claimed.** A station port is claimed by the craft coming in to it or docking at it (from when it's picked to when it's docked), and by the craft that has just left it while it backs off. Every pick of a port, on rails or in physics, leaves out ports other route craft have claimed, and picks from the rest as before ([The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station), [The physics flyer: docking](#the-physics-flyer-docking), [Docking and undocking on rails](#docking-and-undocking-on-rails)). A route craft parked to dock later keeps its claim. This holds at every station, with or without traffic control.

**Traffic control.** A station directs its traffic when a pilot is aboard (any kerbal with the Pilot trait, including one in a ship docked to it, [Crew rules and the flight log](#crew-rules-and-the-flight-log)).

**The keep-out zone.** Round a controlled station:

```
zone = max(200 m, size_station + size_ship + 50 m)
```

Only one route craft at a time moves inside it. A route craft needs clearance to come in, and gives way to:

1. one leaving the station (backing out of a port, then climbing out the departure lane): it has a burn to make, so it always goes first;
2. one docking at the station;
3. one that started coming in to the station before it did (first come, first served; the ship's id breaks a tie).

**The lanes.** Up is away from the body the station orbits:

```
up = (station − body centre) / |station − body centre|
```

- **Arrivals wait below the station.** Not cleared, a route craft waits in the arrival lane at the zone's edge, `−up × zone` from the station. Each one queued behind it waits a further 60 m down:
  ```
  wait = −up × (zone + 60 m × place)          place = route craft coming in that started before it
  ```
  Cleared, its hold point moves in along the same line to `−up × max(50 m, size_station + size_ship + 15 m)`, the hold point of [The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station), and docking takes it round to its port ([The physics flyer: docking](#the-physics-flyer-docking)). The lane follows the station round its orbit. If the straight way to its waiting or hold point passes the station (arriving from above, say), it's steered round, 30° at a time, at the zone's edge, so it stays outside the zone until it's cleared (the same go-round as [The physics flyer: docking](#the-physics-flyer-docking), wider).
- **Departures leave above it.** A route craft leaving backs straight out of its port as before ([The physics flyer: leaving a station](#the-physics-flyer-leaving-a-station)). It then climbs on RCS to the top of the lane: the zone's edge on its own side of the station, raised to at least 45° above the station's horizon if it's lower (`exit = zone × normalize(level·cos 45° + up·sin 45°)`, keeping its compass direction; just `zone × its own direction` if it's already higher). Aiming for the point straight above the station instead made it cross over and come back to line up (test of 2026-11-07). It goes round the station if the straight way is blocked ([The physics flyer: docking](#the-physics-flyer-docking)'s go-round), at up to 1 m/s. At the top it doesn't stop: it keeps its climbing speed and adds [The physics flyer: leaving a station](#the-physics-flyer-leaving-a-station)'s separation push, 0.5 m/s backwards along the orbit. Climbing alone isn't enough: a purely upward velocity puts it on an orbit that crosses the station's again one orbit later (in the station's frame the ship goes up, back, down and forward again to where it started). The push makes it drift steadily away. Then, as in [The physics flyer: leaving a station](#the-physics-flyer-leaving-a-station), its path is checked to stay 100 m clear of the station until its first burn, with another push if not, and its plan is checked and made again from where it is. Reaching the top is for good: an earlier version stopped there and went back to climbing whenever it overshot by 10 m, round and round, burning half a shuttle's monopropellant (test of 2026-11-07). Without RCS it leaves as in [The physics flyer: leaving a station](#the-physics-flyer-leaving-a-station).

The two lanes are on opposite sides, so a route craft can come down to its waiting point while another climbs out. Only the last few hundred metres, inside the zone, are one at a time.

Once docking has started, a route craft only gives way to one leaving: it holds still till that one is clear, and the wait doesn't count towards the 10 minutes after which docking gives up.

**Without traffic control.** No clearance, lanes or queue. Each route craft comes in to a hold point on the side it arrives from ([The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station)) and docks at a free port. Other route craft count as obstacles for its safety stop ([The pilot's safety stop](#the-pilots-safety-stop)). The craft says so once: "No traffic control at X (no pilot aboard X)".

Example: two shuttles of about 10 m at Mun Station (about 30 m across, a pilot aboard). The first is cleared, holds max(50, 30 + 10 + 15) = 55 m below the station, and docks. The zone is max(200, 30 + 10 + 50) = 200 m, so the second waits **200 m** below, and a third 260 m below. Meanwhile a shuttle undocking from the top of the station backs out 30 m, then climbs to 200 m above. It is 400 m from the waiting shuttle.

- Code: `TrafficScenario.ClaimedPorts`, `Ships.PickPorts`, `.PickStationPort`, `RailsDocking.Dock`, `CrewRules.HasTrafficControl`, `PhysicsFlyer.GiveWayTo`, `.ZoneRadius`, `.Up`, `.QueuePlace`, `PhysicsFlyer.Pilot.Approach`, `.BackOff`, `.Dock` (mod/KspFlightPlanner.AddOns)
- Source: [The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station), [The physics flyer: docking](#the-physics-flyer-docking), [The physics flyer: leaving a station](#the-physics-flyer-leaving-a-station); real stations use the same idea: approach from below (R-bar), keep-out sphere, departures away from approaches

### Crew rules and the flight log


No maths here, only counting, but the rules decide when the maths above runs at all.

**The rules gate.** Two questions, asked in one place: may this ship fly this route, and does this station direct its traffic? Each answers yes or no with a reason. A route is refused at its start (before anything undocks) when the ship may not fly it, and the physics flyer leaves a ship alone near the player for the same reason, saying why once. A station has traffic control when a pilot is aboard ([Station traffic](#station-traffic)). May-fly is asked once, when the route starts, with the craft's own crew. A crewed craft needs a pilot aboard who is cleared for the trip, in that direction, counted from the flight log below. The counting rules are in [TRAFFIC-CONTROLLER.md](TRAFFIC-CONTROLLER.md#route-qualification). The controller rules for probe craft go into the physics flyer's take-over question later.

**The flight log (Kerbin Resources).** Every crewed flight in the save, in the order they arrived, each with who was aboard:

```
flight = departed, arrived (game times), from, to, craft, autopilot or by hand, crew (name and trait of each)
place  = a craft by its game id (a station), or a named place: "Kerbin (launch)", "Mun orbit",
         or the body a route left free-flying from
```

How often a kerbal has made a trip is counted from the list: the flights with them aboard, from the same place to the same place. Directions count apart. Places that are craft are matched by id, so a renamed station still counts as the same place. Keeping each flight, not just a count, lets later rules see the order things happened in, such as who was aboard with whom on a training flight.

Two ways onto the log:

- **Route craft** (Traffic Controller). The kerbals aboard are noted when the route starts. When it docks at its target (on rails or in physics), or makes its last burn into its orbit, the trip is logged once, as autopilot. A route planned again on the way (off course, [MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again)) is still the same trip, from where and when it first left.
- **Flights the player flies.** A crewed craft undocking, or launching, opens a trip. Docking somewhere else closes it, and the flight is logged with the crew aboard at that moment. Docking back where it left (moving ports, a called-off approach) closes it without logging. At an undocking or a docking, KSP sees two craft. The lighter one is taken as the one travelling and the heavier as the place: a shuttle leaves a station, never the other way round. So a shuttle grabbing a small tank on the way doesn't end its trip, because the tank is the lighter one. A craft flying a route is left to Traffic Controller. A big crewed ship undocking from a smaller uncrewed depot is the one case this gets wrong (it thinks the depot left).

Example: Jeb and Bill fly Kerb Shuttle mk2 by hand from Mun Station (180 t) to Kerb Station. At undocking the shuttle (22 t) is the lighter half, so its trip opens from Mun Station. It docks at Kerb Station (150 t): logged, Mun Station → Kerb Station, flown by hand, Jeb (Pilot) and Bill (Engineer). Jeb has flown it 4 times before as a route, so he's now at 5. Kerb Station → Mun Station is still 0 for him.

- Code: `CrewRules.CanFly`, `.HasTrafficControl`, `TrafficScenario.Credit`, `.FliesRoute`, `TrafficService.StartRoute`, `KerbinResources.FlightLog`, `KerbinResourcesScenario.OnUndocking`, `.OnCouple`, `.OnLaunch`, `KerbinResourcesService.Flights` (mod/KspFlightPlanner.AddOns)
- Source: none (game rules)

### The pilot's safety stop


The station traffic ([Station traffic](#station-traffic)) orders route craft that use the same station. Anything else near a route craft, such as the player's ship, debris, a route craft bound somewhere else or one whose autopilot gave up, is left to the pilot's own reaction. It runs while the craft is coming in, docking or backing off, at any station, with or without traffic control.

Each frame it looks at every loaded vessel except its station and, at a station with traffic control, the route craft the traffic orders with it. Without traffic control, other route craft count too. Route craft stopped for each other would wait for ever (three leaving at once can stop in a circle: A for B, B for C, C for A), so every route craft uses one order, and a route craft goes on past any stopped route craft below it, whatever that one stopped for, while it holds or backs away. The order: one leaving a station first (it frees a port); then the one whose next burn comes first; then the lower ship id. The top one always goes, so there's no circle. Example: A, B and C all leave Kerb Station, with first burns in 23 min, 14 min 18 s and 14 min 13 s. C goes first, then B, then A. Each one is treated as moving straight on at its speed now (fine over 20 s and a few hundred metres, where orbital curvature barely shows). With `r` the other's position and `v` its velocity, both relative to the craft:

```
t*      = clamp(−(r·v) / |v|², 0, 20 s)        time of closest approach, within the next 20 s
gap     = |r| − size_ship − size_other        now
closest = |r + v·t*| − size_ship − size_other at its closest
stop if min(gap, closest) < 30 m
```

Sizes are each vessel's furthest part from its origin ([The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station)). Stopped, it:

- cuts its engines and holds its attitude;
- on RCS, holds still against its station (matching the other's velocity instead, it followed the other away, 5 m behind it for good, in the test of 2026-11-08: a route craft pushing clear dragged the two stopped behind it along);
- if the other is coming closer (`r·v < 0`), it also backs away at 0.5 m/s, sideways out of the other's path: the part of `−r` across `v` (`−r + v̂ (r·v̂)`), or any way across `v` when it's coming head on (within 1 m). Backing straight away along the line kept it in the other's way (the test of 2026-11-08).

Every 10 s it looks again, and goes on once nothing is inside 30 + 10 = 40 m (the extra 10 m stops it starting and stopping at the edge). After six looks (a minute) it says it's blocked, and keeps holding and looking. Waiting doesn't count towards docking's 10 minutes. It doesn't stop while coming in faster than 5 m/s: that's the braking burn, which RCS couldn't hold anyway, and the braking is stopping it.

Example: a route craft (size 10 m) is docking. The player's ship (size 8 m) is 70 m away, drifting straight at it at 2 m/s:

```
t*      = −(70 × −2) / 2² = 35 s → 20 s
gap     = 70 − 10 − 8 = 52 m
closest = |70 − 2 × 20| − 18 = 30 − 18 = 12 m   < 30 m: stop
r·v < 0 (coming closer): match its 2 m/s and back away at 0.5 m/s
```

The player's ship is now closing at 0.5 m/s instead of 2 m/s. The craft goes on when it stops or passes, and the gap stays over 40 m for the next 20 s.

- Code: `PhysicsFlyer.Pilot.SafetyStop`, `.Hazard`, `.InTrafficAt`, `.GivesWayToMe`, `.Outranks`, `.Translate` (mod/KspFlightPlanner.AddOns)
- Source: closest approach of two straight-line paths (standard kinematics); [Station traffic](#station-traffic)

### Booking a port, and waiting for one


A route craft books a port at the station it's going to, so it never arrives at a station with no port free for it. How far ahead a station's ports can be known is the catch: on a trip of a few minutes it's much as it will be, but on a 10-day trip from Minmus, or months from Duna, it means nothing. So the booking is made by time to arrival, not at departure:

```
book when   t_meet − now ≤ 1 hour
```

- **Short trips** (the meeting within the hour when the route starts, from a station). The port is booked before the craft undocks. With none free, the route isn't started: the craft stays docked, and the planner shows "No free port at X for when it gets there".
- **Long trips.** The craft leaves on time. An hour before the meeting it books a port, and with none free it looks again every 5 s.

A booked port is free, fits one of the craft's free ports (same size), and isn't claimed by another route craft. It counts as a claim from then on ([Station traffic](#station-traffic)), until the craft docks or its route stops. A route planned again on the way ([MATH.md §10.3](MATH.md#103-staying-on-course-and-planning-again)) keeps its booking. If the booked port is taken anyway (the player docks something there), the craft books another.

At the station, the booked port is the one it lines up on ([The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station)), docks at on rails ([Docking and undocking on rails](#docking-and-undocking-on-rails)), or docks at in physics ([The physics flyer: docking](#the-physics-flyer-docking)).

**No free port when it arrives.** On rails, the craft parks on the station's own orbit, 3 km behind it:

```
behind_s = 3000 m / v_station           its mean anomaly set back by n × behind_s (n = mean motion)
```

The same orbit means it holds its place without fuel. At 3 km it's outside physics range (2.2 km here, [The physics flyer: flying a burn](#the-physics-flyer-flying-a-burn)), so it stays on rails even when the player is at the station. Every 5 s it looks for a free port. When one frees up, it comes in as it would have on arriving: lined up on the port if the player is at the station, docked on rails if nobody's near either ship, otherwise parked beside the station. In physics (the player riding along), it waits at the keep-out zone's edge instead ([Station traffic](#station-traffic)), and if the player leaves, it goes to the 3 km point.

**A day's wait.** After a day of game time (6 hours on Kerbin's clock) it says so once, in the game. The planner's trip list shows "waiting 3 km behind it for a free port, since …", with "over a day: free a port there" after that. It keeps waiting.

Example: Mun Station orbits at about 540 m/s. A route craft arriving with all three free ports booked by others waits 3000 / 540 = **5.6 s** behind it on its orbit, 3 km back. When one of the others docks somewhere else and its booking clears, the waiting route craft books that port within 5 s and comes in.

- Code: `PortBooking.Due`, `.Try`, `.Wait`, `.WatchWaiting`, `TrafficScenario.ClaimedPorts`, `RailsFlyer.Arrive`, `.ComeIn`, `.LineUp`, `.WatchArrival`, `.WatchApproach`, `RailsDocking.FreePorts`, `.CraftPortPart`, `.Dock`, `TrafficService.StartRoute`, `PhysicsFlyer.Pilot.Approach` (mod/KspFlightPlanner.AddOns)
- Source: [The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station), [Docking and undocking on rails](#docking-and-undocking-on-rails), [Station traffic](#station-traffic); `Ships.FollowOrbit` for the place on the orbit ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft))

### Which route craft may use a port


Some ports aren't for every route craft: the port on the end of a station's fuel tank is for tankers, not for a shuttle full of kerbals. Each docking port has a setting and each route craft has a role, both right-click settings in the game (in flight and in the editor). The add-on's ModuleManager patch, `TrafficPorts.cfg`, adds them to every docking port and every command pod and probe core, so it needs ModuleManager installed.

| Port setting ("Route craft") | Open to |
|---|---|
| Any route craft (default) | every route craft |
| Crew & supplies only | crew & supplies route craft |
| Tankers only | tankers |
| No route craft | none (dock there by hand) |

A route craft's role ("Traffic role") is Crew & supplies (default) or Tanker. A ship is a tanker if any of its command parts says so. A docked craft's role comes from its own parts, not its station's. The role is read when the route starts and kept with the route.

Wherever a port is picked (booking [Booking a port, and waiting for one](#booking-a-port-and-waiting-for-one), lining up [The physics flyer: coming in to the station](#the-physics-flyer-coming-in-to-the-station), docking in physics [The physics flyer: docking](#the-physics-flyer-docking), on rails [Docking and undocking on rails](#docking-and-undocking-on-rails), the stand-in), the ports not open to the craft's role are left out, along with those other route craft have claimed ([Station traffic](#station-traffic)). A station with no free port open to its role is like one with no free port: a short trip stays docked ("No free port open to tankers at X"), and a long one waits 3 km behind it ([Booking a port, and waiting for one](#booking-a-port-and-waiting-for-one)). Both settings are saved with the part, so they survive docking, undocking and saves, and they're read from the save when a station isn't loaded.

Example: Kerb Station has free ports 14 and 20, and port 20 is on the end of its fuel tank, set to Tankers only. A crew shuttle books port 14. A second shuttle, with 14 booked, finds no port open to it and waits behind the station. A tanker arriving at the same time books port 20.

- Code: `ModuleTrafficPort`, `ModuleTrafficRole`, `PortAccess.Allows`, `.NotOpenTo`, `.RoleOf`, `TrafficScenario.PortsNotFor`, `RailsDocking.CraftRole`, `TrafficService.StartRoute`; patch `TrafficPorts.cfg` (mod/KspFlightPlanner.AddOns)
- Source: [Station traffic](#station-traffic), [Booking a port, and waiting for one](#booking-a-port-and-waiting-for-one)

### Controller rules: critical burns and controller time


The rules themselves are in docs/TRAFFIC-CONTROLLER.md ("Controllers"). Two pieces of maths sit under them.

**Is a burn critical?** A burn is critical (priority 1) when skipping it would lose the craft. The add-on compares two orbits at the burn's time *t_b*: the one the craft coasts on without the burn, and the one it would be on after it (velocity + Δv along prograde, normal and radial, as in [MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)). Either test below makes the burn critical:

```
escapes(o) = e ≥ 1  or  r_ap > r_SOI                          leaves the body's sphere of influence
low(o)     = r_pe < R + h_atm,  and the periapsis is still ahead   (on an escape path, not already passed)

critical   = (escapes(coast) and not escapes(after))  or  (low(coast) and not low(after))
```

A burn the planner titled as a capture is always critical.

Example (Mun: R = 200 km, r_SOI = 2,429.6 km). A probe arrives on a hyperbola with e = 1.15 and periapsis 30 km. The capture leaves it on 30 × 500 km, which is closed with an apoapsis well inside the SOI. Coasting escapes and the orbit after the burn doesn't, so the capture is critical. A transfer burn out of a 100 km circular orbit is the other way round: coasting stays closed, so skipping it loses nothing. It isn't critical.

**A rails burn's time.** The rails flyer makes a burn instantly ([MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft)), but the controller's log counts the time it would have taken at full thrust. The burn is already made, so the craft's mass now, *m₁*, is the mass after it. From the rocket equation m₀ = m₁·e^(Δv/vₑ), and the time is the propellant burnt over the flow rate F/vₑ:

```
t = (m₀ − m₁)·vₑ / F = m₁·vₑ/F · (e^(Δv/vₑ) − 1)        vₑ = Isp·g₀
```

Example: one Terrier (60 kN, Isp 345 s) on a probe left at 2.0 t after a 300 m/s burn. vₑ = 345 × 9.80665 = 3,383 m/s, Δv/vₑ = 0.0887, e^0.0887 − 1 = 0.0927. t = 2.0 × 3,383 / 60 × 0.0927 = 10.5 s. The physics flyer logs the real time from the start of the burn to its end.

**Each burn's SOI on the planned path.** For departure approval and the load forecast, each burn still to come is placed in the SOI it happens in. The path is followed with KSP's patched conics, the earlier burns applied as the rails flyer applies them (`Ships.PathAt`). The burn's body is the one that path is round at the burn's time. Whether it's critical is worked out on that same path.

**Slots.** A controller's capacity *C* comes from how much they've handled, *n* (burns, hand-offs and clearances on their log):

```
C = 2 if n < 10,   4 if 10 ≤ n < 20,   8 if n ≥ 20          (all six numbers are settings)
```

A burn of length *T* at time *t_b* holds a slot over

```
[ t_b − T/2 ,  t_b + T/2 + 60 s ]         T: the burn at full thrust ([The physics flyer: flying a burn](#the-physics-flyer-flying-a-burn)), at least 2 s
```

A controller's load over a span is the number of their uploaded burns whose slots overlap it, plus the craft they're handling in their station's zone when the span starts within 5 minutes. A burn may be uploaded to a controller whose load over its slot is under *C*. With none free, it takes the slot of the least urgent overlapping burn with a higher priority number (ties: the later one), which loses its upload. A zone clearance needs a controller with no craft in the zone and a load under *C* over the next 5 minutes.

Example: Fermy has handled 3 things, so *C* = 2. Two probes' burns are uploaded to Fermy: a phasing burn (priority 5) over 10:00:00–10:02:30 and a correction (priority 3) over 10:01:00–10:02:20. A capture (priority 1) of 40 s at 10:01:30 needs 10:01:10–10:02:50. That overlaps both, so the load is 2 = *C*. The phasing burn has the highest number, so it gives up its slot, and the capture is uploaded.

**Departure approval's forecast.** For each burn of the new trip, round body *B*, with slot [*a*, *b*]:

```
supply = Σ C over the Traffic Controllers on duty covering B now
demand = burns of other trips round B, not yet made, with priority number ≤ this one's, whose slots overlap [a, b]
uncovered  if  no controller covers B,  or  demand ≥ supply
```

Planned burns count whether they've been uploaded yet or not. Who will be on duty later, and CommNet links, can't be known ahead, so they aren't part of it.

- Code: `CrewRules.IsCritical`, `.Escapes`, `.Low`, `.PlanControl`, `.Upload`, `.AfterBurn`, `.HandOff`, `.Linked`, `.Capacity`, `.Load`, `.PeakLoad`, `.Uncovered`, `.Occupies` (Traffic/Controllers.cs); `Ships.FullThrustSeconds`, `.PathAt`; `RailsFlyer.DoBurn`; `PhysicsFlyer.Pilot.EndBurn` (mod/KspFlightPlanner.AddOns)
- Source: [MATH.md §10.1](MATH.md#101-flying-a-burn-without-flying-the-craft); Tsiolkovsky rocket equation (Curtis 11.2)

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
| `StationView.cs`, `StationWindow.cs` | The Station window: reading a station loaded or not, the window in flight and the tracking station, and taking over the Administration building |
| `../PowerHeat/` | Power and heat: reading the parts (`PowerReader`), day and night (`Sunlight`), the sums (`PowerBudget`), splitting an editor station into modules (`ModuleSplit`), the shared drawing (`PowerView`), and the editor's Power & Heat window ([POWER-AND-HEAT.md](POWER-AND-HEAT.md)) |
| `Ships.cs`, `RcsBalance.cs`, `TrafficPanel.cs`, `../PanelWindow.cs` | Ship helpers, RCS balance, the in-game Traffic Controller panel, shared window resizing |
| `Refueler/FuelTransfer.cs`, `RefuelerScenario.cs`, `RefuelerView.cs`, `RefuelerPanel.cs` | Splitting a stack (loaded or saved) into station tanks and craft and moving fuel; the saved pump jobs that run on rails; the controls shared with the Station window; the in-game Refueler window |
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
