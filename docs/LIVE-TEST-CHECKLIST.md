# Live game test checklist

Everything here was built and unit-tested without the game running. These checks confirm it against real KSP.
Tick items as you go and jot down anything odd (numbers, messages, screenshots). Then hand this file back to Claude.
Easiest way: open `docs/checklist.html` in Edge or Chrome and pick this file. Ticks and notes save straight back into it.

**Rough priority:** sections 2 and 5 matter most. A wrong number there sends burns the wrong way.

---

## 0. Setup

- [x] Start KSP, load the **Protogen** save
- [x] kRPC window → **Start server** (auto-accept is already on)
- [x] Start the planner (`publish\<branch>\KspFlightPlanner.exe`, from `tools/publish.ps1`)
- [x] Have a craft **in orbit around Kerbin** you don't mind adding/removing maneuver nodes on (a KerbLink relay is fine)
- [ ] Optional for later sections: a second craft in Kerbin orbit to use as a **target**, and a carrier with a few
      decouplable probes for the **resonant deployment** test

## 1. Connecting

- [x] **KSP › Connect to KSP**: status bar dot goes amber, then green: "KSP: Space Center · Year 1, Day …"
- [x] Status bar date **ticks forward** every couple of seconds while time passes in game
- [ ] Switch scenes (Space Center → Tracking Station → flight): status bar scene name follows
- [x] **KSP › Disconnect from KSP** → grey dot, "not connected". Connect again works.
- [x] Quit KSP while connected → planner shows "Lost the connection to KSP" in red (it shouldn't freeze or crash)
- [x] Optional: **Settings › Live connection**: enter a bad port (e.g. `abc`) → red message and OK greyed out;
      **Restore kRPC defaults** puts 127.0.0.1 / 50000 / 50001 back

Notes:

## 2. Live orbits match the game ⚠ important

In flight, with a craft in orbit:

- [ ] **Rendezvous pane**: the "Craft" dropdown shows your vessels with Pe/Ap. For the craft you're flying, compare with
      KSP's map view (Pe/Ap of the orbit). **Should match within ~1 km.**
- [x] "Save time" line now reads **"Game time: … · live from KSP"**
- [x] Status bar (left) says **"live from KSP"** instead of "read 1:57 PM"
- [x] Burn a little prograde in game (change Ap by 50+ km) → within ~10 s the planner's Pe/Ap for that craft updates
- [x] While just coasting, the Rendezvous plan should **not** keep recalculating every few seconds (progress bar
      flickering constantly = bad). It may refresh about once a game-minute.
- [x] Launch something new (not in the save yet) and reach orbit → it appears in the Rendezvous craft list as
      "… · not saved yet"
- [ ] **Locations pane**: if you have a linked location for a vessel you're moving, its orbit follows

Notes (paste any mismatched numbers):

## 3. Active vessel and target (Rendezvous pane)

- [ ] Left panel shows **"In KSP: flying X · target Y"** (or "no target set")
- [ ] **Use the craft I'm flying** switches to "A craft already in orbit" with your craft selected
- [ ] Set another Kerbin-orbit craft as target in KSP → **Add KSP's target** adds it to the targets list
- [ ] Target a planet or moon instead → the line shows the body name; Add KSP's target stays disabled (correct)
- [ ] Two craft with the **same name** (e.g. two "KerbLink"s): check the right one is picked. This is the
      shakiest part, because kRPC gives no vessel IDs and the planner matches by name and closest orbit.

Notes:

## 4. Fly a rendezvous plan (Flight pane)

- [ ] With a plan in the Rendezvous pane, click **Fly this plan** → switches to **Fly** and opens the **Flight** document with the new flight
- [ ] Flight shows "Flown by …", the burns with **countdowns that tick down**, first one marked **Next**
- [ ] **Send next burn** → node appears in KSP; the burn's tag changes to **On the craft** (within ~2 s)
- [ ] **Send all burns** → asks before replacing existing nodes; all nodes appear in KSP in order
- [ ] Send while flying a **different** craft than the flight's → it asks "You're flying X, but this flight is flown by Y"
- [ ] Drift line reads **"On plan: … m"** while coasting before the first burn (should stay well under 1 km)
- [ ] Fly the first burn (roughly is fine) → drift grows; once over 1 km **Re-plan from here** turns accent-coloured
- [ ] **Re-plan from here** → new burns from your real orbit, "Re-planned from …" message
- [ ] Close and reopen the planner → the flight is still in the list
- [ ] **Delete flight** asks first and removes it (nodes already in KSP stay)

Notes:

## 5. Node direction check ⚠ most important

This is the one thing that can't be tested without the game: do the planner's prograde/normal/radial land the same
way in KSP?

- [ ] On the Rendezvous pane, switch to **Exact nodes**. Pick a burn with a noticeable **normal** or **radial** part
      (a "Match planes" burn is ideal). Note its **"Orbit after"** Pe/Ap.
- [ ] Send it (via Fly this plan → Send to KSP). In KSP's map view, check the node's **predicted orbit**:
  - [ ] Pe/Ap ≈ the planner's "Orbit after"
  - [ ] For a plane change: relative inclination to the target drops to ~0° (not doubles)
  - [ ] In the node editor, the prograde/normal/radial numbers match the planner's (signs too!)
- [ ] If a sign is flipped (inclination doubles, or radial goes the wrong way), **stop and tell Claude** which axis

Notes (node values in planner vs KSP):

## 6. Resonant deployment

- [ ] Resonant Orbits pane: Kerbin, 3 satellites, **Use synchronous** → **Fly this deployment**
- [ ] If you were flying a craft around Kerbin, the flight has burns straight away; otherwise it says to choose the
      carrier. Switch to the carrier → **Use the craft I'm flying** → burns appear
- [ ] Burns: **Enter the carrier orbit** (~+84.6 m/s if starting circular at 2,863 km), then **Circularize satellite
      1–3** (~−84.6 m/s), one carrier lap (~7h 58m) apart
- [ ] If your orbit doesn't reach 2,863 km, a warning says so
- [ ] Send the carrier burn, fly it, check KSP shows Ap ≈ 4,327.7 km / Pe ≈ 2,863 km
- [ ] Before satellite 1's time: decouple a probe, switch to it, **Send to KSP** on "Circularize satellite 1"
      (sending it while flying the carrier should ask first)
- [ ] After flying it, the satellite's orbit is **circular** at ~2,863 km
- [ ] After satellite 1, **Re-plan from here** (with the carrier selected) → the plan picks up at satellite 2
- [ ] Re-plan with the carrier a minute or two before the next pass → satellite 2 is still on **that** pass (not a lap later)
- [ ] Release a satellite a minute before the pass, switch to it, From here › **Circularize at periapsis** → the node is on
      this pass, a minute away, with a "the burn is only … away" note

Notes:

## 7. Landing spots (save file, no kRPC needed)

- [ ] Mission pane: add a **Mun — Surface** stop → **Land at a spot**. The list shows the Mun's wiki landmarks, then
      your flags and landed craft on the Mun from the save (e.g. **New Horizons 1 (Flag)** if it is on the Mun)
- [ ] Picking a flag fills in its **ground height** (from the save's `alt`)
- [ ] In game, open **KerbNet** on a probe orbiting the Mun, add a **custom waypoint**, then save (or quicksave) →
      it appears in the list as **(KerbNet waypoint)** with the right latitude/longitude. ⚠ The waypoint field names
      (`celestialName`, `latitude`, `longitude` under `ScenarioCustomWaypoints`) are from memory, not yet seen in your save
- [ ] Optional: land near a planned spot and compare where you came down (Vessels pane / flag position) with the target

## 8. Launch into an inclination (no kRPC needed)

- [ ] Mission: Kerbin — Surface → Kerbin — Low orbit (80 km), pick **Polar**. The launch leg shows about **+182 m/s**
      and a heading (≈ 0°/360° north or ≈ 180° south)
- [ ] Launch flying that heading. In map view, the orbit's **inclination ≈ 90°**, not doubled or mirrored
- [ ] Optional: compare the Δv you actually used with an equatorial launch of the same rocket (should be ≈ 180 m/s more,
      give or take your ascent)

Notes:

## 9. Launching from KSC to meet a craft (Rendezvous pane) ⚠ unverified constant

The planner assumes Kerbin is **rotated 90° at UT 0** (`LaunchSite.Ksc`, docs/MATH.md §4.5). If that's wrong, every
lift-off time for a launch rendezvous is off.

- [ ] Rendezvous pane: start **from the launch pad**, target a craft in an inclined orbit (≥ 5°), choose the
      ascending- or descending-pass launch
- [ ] Wait until the planner's **lift-off time** and launch on its heading → once in orbit, the **relative inclination**
      to the target is small (under ~1°). A large miss that grows with the time of day = wrong rotation constant

Notes:

## 10. Bake surface data (KSP › Bake Surface Data…) ✅ baked 2026-10-09

Done: all 17 bodies baked at **1°** in about **22 minutes** (from the bake timestamps). Results, read from the
planner's database:

- [x] Bake runs and stores every body: heights, biomes (every point named), rotation, air
- [x] Terrain comes back outside flight (no "all read 0 m"). Zeros only where expected: oceans on Kerbin, Eve and
      Laythe, plus the Minmus flats; Eeloo is the exception (below)
- [x] Total time for all bodies at 1°: **≈ 22 min**
- [x] Kerbin's rotation at UT 0: **90°**, the value the planner assumed (section 9's lift-off times start from it)
- [x] Jool at 0 m: **5,066.25 kPa (50 atm)**. The wiki's pressure table was right; its /Param page's 15 atm is the
      10 km value. The planner now uses the game's value (docs/WIKI-COMPARISON.md conflict settled)
- [x] Day length, radius and μ match the planner exactly for all 17 bodies
- [x] Game vs wiki merge (docs/MATH.md §1.1): only **Eeloo's highest point** is beyond its threshold
      (game 1,245 m vs wiki 3,874 m, −68%), so the wiki's is kept and flagged

Still to look at in the planner and in game:

- [ ] Locations pane › Mun: a **"From your game (surface bake)"** group: "Compared with the wiki", each value as
      "Wiki … · game … · using game/wiki", highest/lowest grid point and biomes
- [ ] Locations pane › Eeloo: its highest point is flagged ⚠. In game, fly over 27.95° N 24.34° E (the wiki's highest
      point) and note the terrain height: ________ m (the bake reads about 260 m there; 22% of Eeloo read exactly 0 m)
- [ ] Mission with a **Mun — Surface** landing on a wiki landmark (no height of its own): the landing text now says
      **"ground … m (surface bake)"** and shows "Highest terrain under the braking burn"
- [ ] **Cancel** mid-bake stops within a row; closing the window also cancels; the body in progress stays "Not baked"
- [ ] Disconnect from KSP mid-bake → it stops with a message, nothing freezes

Known limit: a 1° grid misses small features. The KSC pad reads 47 m (really about 68 m), the Island Airfield 0 m
(its cell is mostly sea), and grid peaks run up to ~15% under the real summits. Flags carry their own height.

Notes:

## 10a. Re-planning during a rendezvous ⚠ important

Fly a rendezvous flight from the Flight pane, connected. Burn a little sloppily on purpose.

- [ ] ~90 s after each burn's node time, the burns update by themselves and the status line says what changed
      (e.g. "Course correction 3.0 m/s: without it you'd pass 5 km…"). Nothing is sent to KSP by itself
- [ ] After the transfer burn: a **Course correction** appears (or "On course" if you nailed it). **Send all burns**
      → KSP's closest-approach marker drops to well under 1 km after the correction node
- [ ] After the correction: Match velocity at the meeting leaves you within a few hundred metres, nearly at rest
      relative to the target. Distance: ________ m (was 9.7 km before this change)
- [ ] **Re-plan from here** mid-leg keeps the steps (no new plane match or phasing orbit appears)
- [ ] Inside 2 min of the meeting: "fly the last part by eye", no new burns

Notes:

## 10b. From here (Fly mode, right side) — no plan needed

Fly mode's default layout changed, so your Fly panes start from the default once. **View › Flying › From here** reopens the pane.

- [ ] The top line shows the craft, its orbit and the target, and follows the game as you switch craft or target
- [ ] **Circularize at apoapsis** on an elliptical orbit → **Send to KSP** → the node sits on the Ap marker, and KSP's
      predicted orbit after it is round (Pe ≈ Ap)
- [ ] Right after lift-off, coasting up with Ap ~80 km and Pe underground, Ap under 2 min away: **Circularize at
      apoapsis** → the node is on **this** Ap (not next orbit), with a short-notice note
- [ ] Same, but just after passing Ap → "Circularize now", 30 s out, with a radial part; KSP's predicted orbit is round
- [ ] **Circularize at periapsis** with Pe in the atmosphere → refused, telling you to use the apoapsis
- [ ] **Change the apoapsis** to e.g. 250 km → the node is at Pe and KSP's predicted Ap reads ~250 km
- [ ] **Change the periapsis** to 30 km → node at Ap, predicted Pe ~30 km. Then try an apoapsis below your Pe → refused
      with a note
- [ ] **Match planes** with a targeted craft, then with the Mun or Minmus targeted → KSP's AN/DN markers read ~0° after it
- [ ] **Rendezvous with the target** → its burns match the Rendezvous tool's for the same craft
- [ ] **Keep as a flight** → it opens in the Flight pane with countdowns. Re-plan is greyed for a one-off burn
- [ ] The Flight pane no longer sits blank when a flight exists: the first flight is selected

Notes:

## 10c. Go to… (From here)

From a round orbit around Kerbin (80–100 km). See docs/MATH.md §3.7 and §3.8 for what to expect.

- [ ] **Go to…** → the To list starts with **KSP's target**, then Mun and Minmus, then every other planet and moon (each planet
      followed by its moons; no Sun). With a target set, that entry is picked and
      names it; change the target in KSP and the entry follows (and the height box takes the new body's parking height)
- [ ] KSP's target set to Minmus → Plan it → the same burns as picking Minmus from the list
- [ ] KSP's target set to a craft around Kerbin → "(rendezvous)", no height box; Plan it → the same burns as **Rendezvous with
      the target**
- [ ] Each burn shows how long it takes ("burn 1m 24s"), for the craft you're flying as the save last recorded it. If the trip
      needs more Δv than the craft has, a note says how much it's short
- [ ] Mun, 30 km → **Plan it** → two burns: Transfer to Mun (~840 m/s) and Capture at Mun (~260 m/s)
- [ ] **Send to KSP** → the transfer node's predicted path shows a Mun encounter with Pe ≈ 30 km, going the way the Mun spins
      (KSP's encounter readout under the node)
- [ ] The capture node also lands on the Mun patch, at the periapsis (KSP may put a node on a later patch, or may not: note which)
- [ ] Fly the transfer roughly. On the way out, **Plan it** again → a small **Course correction** (or "already on course" with only
      the capture)
- [ ] Inside the Mun's sphere of influence: the top line shows "a … km pass, escaping"; **Circularize at periapsis** → node
      at Pe, and KSP's predicted orbit after it is round at ~30 km
- [ ] Same, but **Change the apoapsis** to 500 km → "Capture at Mun, apoapsis 500 km", cheaper than circularizing
- [ ] Minmus, 20 km → **Ways to go** lists two: *Cheapest* (waits about 6 days) and *Leaves soonest* (~130 m/s dearer,
      with a large normal part). Picking one shows its burns
- [ ] **Keep as a flight** → opens in the Flight pane; both burns show countdowns
- [ ] Coming home: in a round orbit around the Mun, the To list shows Kerbin, picked by default. 80 km → **Leave Mun for Kerbin**
      (~260 m/s, from 30 km) and **Capture at Kerbin** (~850 m/s). Sent, KSP's predicted path leaves the Mun with a Kerbin Pe ≈ 80 km
- [ ] Same with 35 km → no capture burn, and a note that the atmosphere does the braking
- [ ] After the escape burn, still inside the Mun's sphere, **Plan it** again → a small correction, or only the capture
- [ ] Duna, 100 km, from Kerbin orbit → **Ways to go** lists up to three (*Cheapest* ~1,690 m/s in all, *Shortest flight*,
      *Leaves soonest*), each with when it leaves, how long it flies and its Δv. Each has a **Leave Kerbin for Duna** burn and a
      **Capture at Duna**; the cheapest also has a small course correction
- [ ] Send the cheapest's ejection and look at KSP's predicted path: does it reach Duna's sphere (an encounter)? Note how close
      KSP says the Duna periapsis is
- [ ] After the ejection, once around the Sun: **Go to…** Duna again → a small **Course correction** and the capture
- [ ] From the Mun, **Go to…** Duna → the trip stops in Kerbin orbit (80 km) first; the later burns say to plan again from there
- [ ] Mun → Minmus → a hop across around Kerbin, with a small course correction
- [ ] **Transfer Planner › Fly this window** (Kerbin → Duna) while flying a craft in Kerbin orbit → Fly mode opens the Flight
      pane with a "Kerbin → Duna" flight flown by that craft: Leave Kerbin for Duna, a course correction, Capture at Duna
- [ ] Same with no craft (or not connected) → planned from a round orbit at the parking height, with a note to click "Use
      the craft I'm flying" once up; doing that plans it from the real orbit
- [ ] Click a different point on the porkchop plot, then Fly this window → the flight leaves at that point's date
- [ ] The return leg's **Fly this window** (Plan return trip ticked) → a "Duna → Kerbin" flight leaving at the return
      window's date. With Aerobrake (the default) there's no capture burn and the note says the periapsis is aimed at 30.1 km;
      send the ejection from Duna orbit and check KSP's predicted Kerbin periapsis
- [ ] After the ejection, out of Kerbin's sphere: the Flight pane says the craft is around Kerbol and lights **Re-plan**;
      Re-plan → a course correction and the capture

Notes:

## 10d. Land at a spot (From here, and Fly this landing) ⚠ unverified frame

From a round 30 km orbit around the Mun, inclined about 15°. See docs/MATH.md §2.9. The burns are timed with the Mun's
rotation at UT 0 (θ₀): from the surface bake if there is one, else asked from the game. If the spot comes out on the other
side of the Mun, or mirrored east/west, the longitude convention is wrong: note where you came down.

- [ ] **Land at a spot** → the Spot list has the Mun's landmarks, then flags, landed craft and waypoints from the save. A flag
      targeted in KSP is picked for you
- [ ] Pick a landmark (or plant a flag first and target it) → **Plan it** → **Deorbit for …**, **Swing the track over …**
      (a few m/s normal, sometimes none), **Brake to land at …** (~600 m/s for TWR 2). The deorbit is ~17 m/s
- [ ] **Send to KSP** → the deorbit node's predicted path has its periapsis ~1 km above the spot's ground, a little short of it
      (KSP's map view: the periapsis marker and the spot/flag line up along the track)
- [ ] After the deorbit and the track burn, the predicted impact point (KSP shows it on the map, or use a mod like Trajectories)
      lies on the spot's line, a few tens of km beyond it (the braking burn stops you before that)
- [ ] Fly the braking burn from the periapsis, holding surface retrograde and level → note how far from the spot you stop
- [ ] From an equatorial orbit, a spot at 40° N → the first burn is **Tilt the orbit to 41°** at the equator; **Ways to land**
      lists *Least Δv* (may wait many hours) and *Sooner* (a bigger track burn), when the two differ by more than a few m/s
- [ ] Not connected and no bake for the body → a note says the body's rotation is needed (Kerbin uses the stock 90°)
- [ ] **Keep as a flight** → opens in the Flight pane as "Land at …"; **Re-plan from here** plans it again from the real orbit
- [ ] Plan mode: a mission with a Mun landing spot → **Fly this landing** on the spot's card → Fly mode opens the Flight pane
      with "Land at …". In Mun orbit with that craft: the burns are planned straight away. Otherwise a note says to click
      "Use the craft I'm flying" once in orbit there, and doing that plans them
- [ ] Duna (air) → no braking burn, and the note says the spot is a rough estimate

Notes:

## 10e. Flight log (View › Flying › Flight log)

- [ ] On the pad the pane says the rocket is on the pad and recording starts at launch
- [ ] Launch → "Recording <name> · MET … · … m/s used" counts up; Δv used looks close to KSP's own Δv readout drop
- [ ] Reach orbit → the log in the list says "In orbit of Kerbin", grouped under the rocket's name
- [ ] Time warp past ×4 for a while → the log shows "Data lost in warp: ×… for …"
- [ ] Disconnect (KSP › Disconnect) mid-flight, wait, reconnect → "Flight disconnected: … not recorded", same log carries on
- [ ] Switch to another vessel and back → one log, with the gap noted
- [ ] Recover a landed vessel → its log says Recovered (after KSP writes the save); revert a launch → Reverted
- [ ] Turn off "Record a flight log while flying" in Settings → the pane says recording is off; nothing new is logged
- [ ] Pick a log → the Flight report opens: Δv to orbit, ascent losses, max Q/g, a card per touchdown
- [ ] Charts: switch kind (altitude, speed, Δv used…) and part of the flight (ascent, each landing); hover shows values and staging lines
- [ ] Burns table matches what you flew; the Δv by stage looks right against the design's stages
- [ ] A Mun trip with long warps: no "Switched to another vessel" at the SOI changes; warp shows as a dashed curve on the altitude chart
- [ ] Map › Around Kerbin: the path goes out to the Mun's orbit and meets the Mun where it was; Around Mun shows the flyby or capture
- [ ] Fly the same rocket twice → Compare this rocket: both flights in the table (same design letter), best figures in green, charts overlaid
- [ ] Add a part in the VAB and fly again → the new flight gets the next design letter; the Rocket pane says how often the design has flown
- [ ] Launch to orbit → Flight report › Ascent coach: launch TWR, turn start and 45° altitude match what you flew; the pitch chart's blue line is your nose (90° on the pad)
- [ ] The best-climb box gives a model check within a few %; try its advice (turn start speed, 45° altitude) on the next launch and compare
- [ ] Release a relay, rename it in orbit, let KSP save → its log row says "Now <new name>" and the search finds the new name
- [ ] Pick that log → "Vessel: <new name> (found by launch time)"; Show vessel opens it in the Vessels pane and Vessel page
- [ ] On the Vessel page, Flight log lists its flights; clicking one opens the Flight log and the Flight report on it
- [ ] Edit log… → pick another vessel (or None of them) and change the rocket name → the row, Vessel page and Compare follow; keep flying the vessel and the edit stays

Docking (fly a ship up to the station and dock, then undock it later):
- [ ] Fly the ship in and dock → within a few seconds the station's log gets "<ship> docked" and the ship's row says "Docked to <station>" (not Lost, even after KSP saves)
- [ ] Note which vessel KSP kept (the docked stack's name in the map view): that one's log carries on recording the stack
- [ ] Switch away and back to the docked stack → still one log for the stack, no new log for the ship
- [ ] Undock and fly the ship away → its own log picks up again: "Undocked from <station> after …", MET runs on from its launch (not from 0), the docked days show as a break in the charts
- [ ] The station's log gets "<ship> undocked"; the ship's Vessel page lists its flight again
- [ ] If KSP renamed either vessel on undocking, write down what it did: both logs should still follow the right vessel
- [ ] Dock while the planner is closed, open it, let KSP save → the ship's log says Docked (found from the save's docking ports), not Lost

Notes:

## 10f. Fly a whole mission ⚠ new

A mission Kerbin surface → Minmus surface (with a landing spot) → Kerbin surface, saved. See docs/MATH.md §8.15.

- [ ] Plan mode: **Fly this mission** on the Mission pane (or **Fly** in Saved missions) → Fly mode opens the Flight pane with
      six steps: Launch to 80 km, Go to Minmus, Land at …, Launch to 10 km around Minmus, Go to Kerbin straight into the
      atmosphere, Land on Kerbin
- [ ] Fly mode: the Flights pane's mission picker + **Fly** does the same (missions saved since show when the list opens)
- [ ] On the pad with the rocket → the craft is picked for you. Step 1 says "launch when you're ready"; step 2 already has a
      transfer planned from a round 80 km orbit
- [ ] Launch to a slightly different orbit (say 75 × 78 km) → once it's stable, step 1 turns Done **by itself** and step 2 is
      planned again from the real orbit; the status says "Step 1 done" and the Δv (was …)
- [ ] Step 2's burns: **Send all burns** → the transfer node in KSP reaches Minmus with the periapsis near 10 km
- [ ] Fly the transfer a bit off → Re-plan lights up; **Re-plan from here** plans a course correction
- [ ] Inside Minmus's sphere of influence → **Re-plan from here** gives the capture at periapsis. Once in orbit, step 2
      turns Done and the landing (step 3) is planned from the real orbit
- [ ] Land → step 3 turns Done when the craft is on the ground; step 4 says to launch when ready
- [ ] Lift off from Minmus to orbit → step 5 (home) is planned from the real orbit, aiming at ~30 km over Kerbin with no capture
- [ ] **Back a step** → that step stays the one flown, even though the craft has done it; **Next step** moves on
- [ ] Close and reopen the app mid-mission → the flight comes back on the same step

Notes:

## 11. Other bits

- [ ] **Transfer Planner › Today** sets Year/Day to the game date
- [ ] **Vessels pane** still works while connected (it rebuilds when orbits change; your selection should stay)
- [ ] **Connect to KSP when the planner starts** (Settings) → connects on launch if KSP is up; stays quiet if not
- [ ] Leave it connected for a while in flight with lots of vessels: does KSP or the planner stutter every ~10 s?
      (Every vessel's orbit is read every 10 s.)

Notes:

---

**Known gaps (not bugs):** a whole mission can't be flown yet (its legs can't be sent as one flight; use From here, or
Fly this plan / deployment / window in the Rendezvous, Resonant Orbits and Transfer Planner panes). kRPC can't see the VAB craft you're building.

**Not built yet:**
- **Exact height of a spot** asked from the game when connected (one call per spot), so small features like the KSC
  pad aren't averaged away by the 1° grid.
- **Clickable surface map** drawn from the bake (hover for lat/lon/height/biome, click to set a landing spot).
- **Timing a landing's deorbit burn** from the craft's orbit (§2.6 step 3). The bake now stores each body's rotation
  at UT 0, which it needs.
- **Send a landing spot to the game** as a waypoint, so it shows on the navball.
- **Drag model** for landings on bodies with air (they're marked as rough estimates until then).
