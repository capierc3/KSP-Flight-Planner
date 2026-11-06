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

## Round 4: the physics flyer turns the ship (do it with round 3)

New: when a hauler with a trip is loaded near you with physics on, the add-on's autopilot flies it. So far it only turns the ship to face its next burn and holds it there (the burn itself still happens the old way, in one jump). A **Haulers** button (a green box and arrow) is on KSP's toolbar, top right in flight.

This fits into round 3: after you press **Fly it in the background** you're still flying the shuttle, so the autopilot takes it straight away.

- [ ] Right after starting the trip: the shuttle turns by itself to face burn 1 and holds still. Open the **Haulers** panel: "Autopilot: facing burn 1 (…) in …: 0.4° off" or so. Note how long the turn took and whether it wobbled or overshot.
- [ ] Watch for a few seconds: the error stays near 0°, no slow spin or drift.
- [ ] Touch any control (WASD, QE, IJKL, throttle): "Autopilot off: you took the controls", and the panel's switch is off. This is how you take over; now do round 3's nudge.
- [ ] Turn the switch back on in the panel: it takes the shuttle again and turns back to burn 1. Turn it off again before you nudge.
- [ ] After round 3's off-course replan, the panel shows the new burn 1, and the autopilot (back on once you've left the shuttle and it's on rails) faces it the next time you're near.
- [ ] Optional: fly Kerb Station with the shuttle parked less than 200 m away and its trip still to go. The shuttle should turn by itself while you watch from the station.
- [ ] In `KSP.log`, look for "Autopilot: axis … turned the other way; its sign is flipped". If it's there, that axis's direction was guessed wrong and fixed itself; tell me which.

## Round 5: the physics flyer flies a burn (after rounds 3 and 4)

New: a hauler near you with physics on flies its burns for real: it faces the burn, lights its engine half the burn's length early, steers on the Δv still to go and cuts off when it's done, burning its own fuel. Time warp stops by itself 30 s before a burn starts when the hauler is near you.

Start a fresh trip for this (a quickload from before round 3, or plan the shuttle again), and this time don't touch the controls.

- [ ] Press **Fly it in the background** while flying the shuttle, and leave it alone. It turns to face burn 1 (round 4).
- [ ] Warp towards burn 1 (any rate). About 30 s before it starts: "Time warp stopped: burn 1 starts in …".
- [ ] At the start: "Burn 1 (…): flying it for real, … m/s to go". The engine lights, with flame and sound, and the panel counts the Δv down with the throttle easing off at the end.
- [ ] At the end: "Burn 1 flown in … s: … of … m/s, 0.0x m/s off" in the log. Note the "off" number and whether it wobbled while burning.
- [ ] Switch to the tracking station: "Back on rails: on course, meets Mun Station within …" in the log (the course check tidying up after a real burn). The rest of the trip goes on the old way.
- [ ] Optional: stay on the shuttle through burn 2 as well. Before it starts, the log has "Before burn 2: on course…" (checked because it never went back on rails).
- [ ] Optional: touch the controls mid-burn. The engine cuts, "Autopilot off", and "Burn N cut short…". Leave the shuttle: the course check replans it.

Send me the `[KspFlightPlanner]` lines, and any "Autopilot: axis … turned the other way" warnings.

## Round 6: watching it come in to the station

New: if you're at the station when a hauler arrives (autopilot on), it isn't parked any more. It's put 2.5 km straight out from a free port that fits, coming in at 20 m/s, just out of sight. It comes into view, flips round, brakes to a stop 150 m off the port, and then docks (still the jump-to-the-port docking for now; flying the docking for real is next).

Any trip works. The round 3 trip ends at Mun Station, so be flying Mun Station for its end.

- [ ] While the hauler is on its way (it's fine to warp in the tracking station), switch to the destination station a little before the meeting time. Warp is fine until the meeting.
- [ ] At the meeting: "… is coming in to …: watch for it", and time warp stops (it won't let you warp again until the hauler has stopped).
- [ ] Look out along the port it picked (the log line "coming in to …'s … from 2.6 km out" names it). The hauler comes into view at about 2.25 km, heading straight for the port.
- [ ] Open the **Haulers** panel: "coming in: … m from the port, … m/s, coasting", then "turning to brake" (it flips to face backwards), then "braking, throttle …%" from about 350 m out, with the engine lit.
- [ ] It stops about 150 m off the port: "Stopped … m off the spot in front of …: docking". About 2 s later it jumps to the port and docks, as in round 2.
- [ ] Note anything odd: coming in from the wrong side, swinging off the axis, stopping too close or too far, wobbling while braking, overshooting.
- [ ] Optional: switch away during the approach. It should park beside the station instead ("Coming in stopped (you left)").

## Round 7: docking for real with RCS (follows round 6)

New: a hauler with RCS docks itself for real once it has stopped off the port. It turns its port to face the station's, moves out to a point straight out from the port (going round the station if it has to), then slides in along the port's axis, slowing to 0.15 m/s for the magnets. Without RCS, or if it gives up after 10 minutes, it jumps to the port and docks as before.

- [ ] Round 6 again: after "Stopped … off the spot in front of …", you see "Docking for real: … to …'s …". (The Kerb Shuttle mk2 has RCS; make sure it has some monopropellant.)
- [ ] The **Haulers** panel shows "docking: out to the port's axis …", then "docking: … m to go, … m off the axis, closing at … m/s". RCS puffs, no main engine.
- [ ] The ports line up and the magnets pull it in: "Docked at …". It took about a minute from 30 m out in my sums; note how long it really took, and any wobble, sideways drift or bumping.
- [ ] The parked case, with the view on the hauler: start a trip, stay on the shuttle all the way (autopilot on, don't touch anything) and warp to the meeting. It parks behind the station (that jump is still there in this case), then docks itself the same way, going round the station first if it's on the wrong side.
- [ ] In `KSP.log`, look for "Autopilot: RCS axis … moved the other way" or "turning axis …". Tell me any you see: they mean my guess at KSP's axis direction was wrong and fixed itself.
- [ ] Optional: switch the autopilot off in the panel mid-docking. It stops moving; dock by hand, or switch it back on.

## Round 8: leaving a station (a full round trip)

New: you can start a trip with the hauler still docked. Pick it under From here's **Fly as** (the docked shuttle, not the whole stack), plan, and press **Fly it in the background**. The add-on checks the plan, undocks it, and backs it straight out on RCS until it's about 30 m clear, holding still. Only then does it turn for its first burn.

- [ ] With Kerb Shuttle mk2 docked (round 7 leaves it docked), fly the station, set **Fly as** to "Kerb Shuttle mk2 (docked)", target the other station, then Go to… › Plan › **Fly it in the background**.
- [ ] It undocks by itself ("Undocked from …" in the log), and the panel shows "backing off from …: … of 30 m" with RCS puffs. Then "Backed off … m from …: on its way", and it turns to face burn 1.
- [ ] Stay to watch burn 1 for real (round 5), or leave: either is fine. Then the trip, the arrival (be at the other station for round 6) and the docking (round 7). That's the whole round trip with no hands.
- [ ] Note how far it really backed off, whether it drifted sideways, and whether anything bumped.

## The Haulers panel's next burn and Warp to (check along the way)

The panel (toolbar, in flight and now in the tracking station too) shows each flying trip's next burn: "Next: burn 3 of 5, Transfer burn, 89 m/s", then the game date as KSP's clock shows it ("Y1, D197, 00:53:54") and how long till then. **Warp to … before it** warps to a minute before the burn, or, when the hauler is near you with the autopilot on, to 40 s before the real burn starts (so it can turn first).

- [ ] The date matches KSP's clock style, and the countdown goes down as you watch.
- [ ] **Warp to** stops at about the time it says, in flight and in the tracking station.
- [ ] After the last burn it shows "Next: meets …" with the meeting's date.

## Setting the target from the planner

New: From here has a **Target** list (for Go to, Rendezvous and Match planes) of every other craft in space, the ones round your body first. Picking one sets it as KSP's target, as clicking it in map view does, and Go to's **To** switches to "KSP's target". So from the station: **Plan for** the docked shuttle, **Target** the other station, Plan, Fly it in the background, all in the planner.

- [ ] The list shows the other stations and craft (no debris), and the one you pick becomes KSP's target in the game (the target marker shows on it).
- [ ] Go to's **To** reads "KSP's target: … (rendezvous)" straight after, and Plan works without touching map view.

## Round 9: refuel, then a smoother trip (2026-11-06 fixes)

The save's shuttle is **not** refuelled: the first step is the new Fuel section.

- [ ] Fly Mun Station. Open the **Haulers** panel: a **Fuel** box for Kerb Shuttle mk2 shows its tanks, the station's, and its Δv. Choose **Fill Kerb Shuttle mk2**, then **Start**: the shuttle's numbers rise and the station's fall, over about 30 s, until it's full (or the station runs out). **Stop** works part way.
- [ ] Try **Unload to the station** with "Keep at least 300 m/s": it stops by itself at about 300 m/s left. Then fill it again.
- [ ] **Balance RCS** on the stack: it reports the turning per push before and after (or "already balanced").
- [ ] Drag the panel's bottom-right corner: it resizes, and the trip list grows with it.
- [ ] Plan for the docked shuttle, target Kerb Station, pick **Meets soonest**, Fly it in the background. After backing off, a small miss now gives "Course correction added: … m/s …; the meeting stays as planned" instead of a replan, and the departure time doesn't jump.
- [ ] Waiting for a burn: the shuttle holds still (no spin) until 10 minutes before, then turns once to face it and holds. RCS stays off while it's only holding.
- [ ] After the escape burn and the capture: a course correction rather than a replan (or a replan that keeps "Meets soonest", with a note if fuel forced another way). Small corrections are flown on RCS, with no turning.
- [ ] Coming in to Kerb Station: it never speeds up towards it. It brakes gently (limiter well down, ~10%), and the last couple of m/s are on RCS.
- [ ] Docking: it rolls to line the sideways error up with one RCS axis; less wobble and bumping. Look for any "RCS axis … moved the other way" in the log (there shouldn't be any now).
- [ ] At the end, compare the planned "1d … · 1,2xx m/s" with what it took: the log's "Started" and "Course correction" lines.
