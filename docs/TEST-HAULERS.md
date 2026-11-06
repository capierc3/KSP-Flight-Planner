# Test: a background trip (Mun Station → Kerb Station)

Goal: Kerb Shuttle mk2 flies from Mun Station to Kerb Station on its own while you watch from the tracking station.

Use the **Protogen Hauler Test** save only (a copy of Protogen made 2026-11-04; Protogen itself is untouched).

## Setup

- [x] Close KSP Flight Planner if it's running (the aerobraking build). This branch is built from survey, so its database may be rebuilt when you switch builds.
- [x] Start KSP and load **Protogen Hauler Test**. The planner's add-on (GameData/KspFlightPlanner) is already installed with the new trip code.
- [x] Start `publish\haulers\KspFlightPlanner.exe` (in this worktree) and connect to KSP (allow the kRPC prompt).

## Undock and plan

- [x] Go to Mun Station, undock **Kerb Shuttle mk2** (right-click its docking port › Undock), switch to the shuttle and back off a little.
- [x] Set **Kerb Station** as the target in KSP (map view › right-click › Set as target).
- [x] In the planner: Fly › **From here** › **Go to…** › destination "KSP's target: Kerb Station" › **Plan**. Keep the first option (Cheapest).
- [x] Press **Fly it in the background**. The planner says "The game is flying it…", and KSP shows "Kerb Shuttle mk2 is flying to Kerb Station on its own".

## Watch

- [x] Go to the tracking station, select the shuttle, and warp.
- [x] At each burn a message comes up at the top ("Kerb Shuttle mk2: Transfer burn, 312 m/s") and the orbit line changes to the new path.
- [x] It leaves the Mun's sphere of influence, then gets captured at Kerbin, then phases and meets the station, all on its own.
- [x] At the meeting: "Kerb Shuttle mk2 has arrived at Kerb Station". The shuttle sits a little behind the station on the same orbit.
- [x] Switch to the shuttle: less fuel than before, nothing broken, it can dock by hand.

## Tell me

- [x] The lines starting `[KspFlightPlanner] Kerb Shuttle mk2` in `KSP.log` (in the KSP folder): each burn's Δv, fuel used and lateness, and the arrival line, "came within … km of it at … m/s". That number is how well KSP followed the plan.
- [x] Anything odd: a jump in the wrong direction, a burn at the wrong place, the shuttle disappearing, a stopped route message.

Notes: the add-on flies the burns all at once (no visible burning yet), and docking at an assigned port is for later. Undocking was done by hand on purpose.

## Round 2: parking that holds, and docking when you're near

Round 1 passed (2026-11-04): 5 burns, 1,110 m/s, arrived within 0 km at 0 m/s, parked 109 m behind. Fixed since: the parking orbit drifted (it swung about 100–220 m each orbit and crept about 47 m a day), and the ship didn't dock.

Use the fresh copy **Protogen Hauler Test 2** (Protogen as it was before round 1). The add-on in GameData is updated; the app hasn't changed.

- [x] Fly the same trip as round 1 (undock Kerb Shuttle mk2 by hand, target Kerb Station, From here › Go to… › Plan › **Fly it in the background**), and watch it from the tracking station.
- [x] Arrival message: "…has arrived at Kerb Station: it docks when you're nearby".
- [x] Warp on a few orbits in the tracking station, then switch to the station or the shuttle: they should still be about 100–180 m apart (the navball's ~0.3 m/s target speed is normal on a shared orbit).
- [x] Without time warp, wait about 2 s: the shuttle jumps to a free port on the station with its port facing it, and the magnets pull it in. Message: "Kerb Shuttle mk2: Docked at Kerb Station."
- [x] If it doesn't catch within 10 s: "Didn't dock…", and the shuttle stays just off the port. Note which port it picked and how it was turned.
- [x] Check nothing exploded or got pushed, and the docked shuttle shows up as part of Kerb Station.
- [x] Optional, on the round 1 save (**Protogen Hauler Test**): switch to the shuttle there too. Its route is already "arrived", so it docks as soon as the station is within 200 m (its old orbit swings, so you may have to wait part of an orbit).

Send me the `[KspFlightPlanner]` lines from `KSP.log` again.

Result (2026-11-05): passed. 5 burns, arrived within 4 m at 0.0 m/s, parked 109 m behind. Flying the station, the shuttle docked about 2 s after the scene loaded. In the save, Kerb Station went from 208 to 274 parts (the shuttle's 66), the shuttle has about 30 LF / 37 Ox left, and the route state is Docked. No errors in KSP.log.

## Round 3: knocked off course, and planned again

New: the add-on checks the rest of the plan once when the trip starts, and again only after the shuttle (or its target) has been off rails, i.e. had physics running because you flew it or something near it. The check runs once both are back on rails (you left, or time warp packed them), or just before the next burn if you're still there. A trip nobody touches is never checked again. It uses KSP's own prediction (the map view's), with the burns still to come. If the ship would miss the target by more than 1 km or 5 m/s, the trip pauses, time warp stops, and the planner plans it again from where the ship is. **Keep the planner running and connected the whole time**: it does the planning.

Use **Protogen Hauler Test** (the round 1 save, shuttle refuelled), and fly the shuttle back: Kerb Station → Mun Station. Close KSP and the planner first: the add-on in GameData and `publish\haulers\KspFlightPlanner.exe` are both new.

- [ ] Load the save and go to Kerb Station. The shuttle may dock itself first (its old trip had arrived); if so, undock it and back off a little.
- [ ] Target **Mun Station**, then From here › **Go to…** › Plan › **Fly it in the background**. KSP's log line now ends "KSP's prediction meets Mun Station within … m". If instead it says "KSP's own prediction … not started", tell me the message.
- [ ] Before the first burn, stay on the shuttle and knock it off course: about 10 m/s with RCS or the engine, any direction.
- [ ] Warp (high enough that the shuttle goes on rails, 50× or more) or switch to the tracking station. Right away: "Kerb Shuttle mk2 is off course (…)", and time warp stops by itself.
- [ ] Within a few seconds: "Kerb Shuttle mk2 was planned again and is flying to Mun Station on its own".
- [ ] Go to the tracking station and warp through the trip. No checks should run there (nothing touches it on rails): the log has only the burns.
- [ ] Optional: switch to the shuttle between burns and leave it alone, then go back to the tracking station. The log gets "is off rails…" then "Back on rails: on course, meets Mun Station within …".
- [ ] Arrival, parking, and docking when you fly Mun Station, as in round 2.
- [ ] Optional: once it's in the Mun's sphere of influence on its way in, switch to it and nudge it again. It should be planned again, with a capture burn first if it's still on its way through.

Send me the `[KspFlightPlanner]` lines from `KSP.log`, and anything the planner showed.
