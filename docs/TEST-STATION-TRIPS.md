# Station trips test plan (0.2.2)

Everything on the `station-trips` branch, tested together once it's all in. Each update adds its section here as it lands.
Tick items as you go and jot down anything odd (numbers, messages, screenshots). Then hand this file back to Claude.
Easiest way: open `docs/checklist.html` in Edge or Chrome and pick this file. Ticks and notes save straight back into it.

The user story: the **Kerb Shuttle mk1** is docked at **Kerb Station** (Kerbin) and flies to **Mun Station**, or to a relay
around the Mun, in Fly mode and in Plan mode.

---

## 0. Setup

- [ ] Publish the branch (`pwsh tools/publish.ps1`) and start `publish\station-trips\KspFlightPlanner.exe`
- [ ] Help › About shows **0.2.2**
- [ ] Start KSP, load the **Protogen** save, start the kRPC server, **KSP › Connect to KSP**
- [ ] The Kerb Shuttle mk1 is docked at Kerb Station (as in the save on 2026-10-27)

Notes:

## 1. Docked craft: telling the shuttle from the station

The save keeps a docked stack as one vessel. The planner splits it back into the vessels that docked. A piece counts as a
craft of its own when it's a Ship, Lander, Rover or Plane with a pod or probe core **and** an engine. Anything else
(Probe, Relay, Station, Base, Debris) is part of the station. docs/MATH.md §8.16.

### Vessels pane

- [ ] Under Kerbin, **Kerb Station** has four rows below it:
      Kerb Station - Electrical (*probe · station part*), Kerb Shuttle mk1 (*ship · docked craft · ~2,062 m/s*),
      Kerbin Station - Fuel Tank (*probe · station part · ~5,094 m/s*), Homeward Bound 2 (*ship · docked craft · ~533 m/s*)
- [ ] Under the Mun, **Mun Station** has **Grand Lander** (*lander · docked craft*)
- [ ] Click **Kerb Shuttle mk1**: its page says "Docked at Kerb Station…", and its Δv, fuel and engines are the shuttle's
      alone. Compare with KSP's Δv readout for the shuttle **after undocking** (or with its design in the VAB).
- [ ] Click **Kerb Station**: its page lists what's docked there, and shows the whole stack (as KSP flies it)
- [ ] On the shuttle's page, **Counts as** → *Part of Kerb Station*: its row now says *station part*. Restart the
      planner: still *station part*. Set it back to *Automatic (its own craft)*.
- [ ] On the Fuel Tank's page, **Counts as** → *Its own craft*: its row says *docked craft*, and it shows up in the
      Rendezvous craft list (below). Set it back to *Automatic*.

Notes:

### Fly mode: Plan for

- [ ] In KSP, fly the **Kerb Station** stack (switch to it in the Tracking Station, or control from the shuttle's pod)
- [ ] The **From here** pane shows **Plan for** with *Kerb Station (the whole stack)*, *Kerb Shuttle mk1 (docked)* and
      *Homeward Bound 2 (docked)*
- [ ] Pick **Kerb Shuttle mk1**: the top line reads "Kerb Shuttle mk1, docked at Kerb Station · … around Kerbin"
- [ ] **Go to… Mun** (or any burn) and **Plan**: burn times and the "not enough fuel" / "short by" note use the
      **shuttle's** fuel and engine, not the whole station's
- [ ] Switch back to *Kerb Station (the whole stack)*: the plan clears; plan again and the burn times change to the stack's
- [ ] Fly a vessel with nothing docked (a KerbLink relay): no **Plan for** row
- [ ] Go back to the station stack: the shuttle is still picked

Notes:

### Rendezvous and linked places

- [ ] **Rendezvous** pane, Craft list: *Kerb Shuttle mk1* is there, its line ending "· docked at Kerb Station"
- [ ] While flying the stack with **Plan for** on the shuttle, Rendezvous' **use the craft you're flying** picks the shuttle
- [ ] **Import vessels** offers the Kerb Shuttle mk1 (docked) as something to import as a location
- [ ] Optional: import the shuttle, then undock it in KSP and let KSP save (switch vessel or go to the Space Center):
      the location follows the undocked shuttle (by its name) rather than saying it's no longer in the save

Notes:

### Things to watch for

- [ ] A docking flown since KSP last saved doesn't show until KSP writes the save (scene change, vessel switch). Orbits
      stay live. Note how long it took to show up.
- [ ] Anything named oddly (e.g. "… (docked part)"): that's a docked piece whose port didn't say what it came with

Notes:

## 2. Fuel planning: Δv as fuel, and refuelling a craft

The Rocket pane (Plan mode) checks a saved design **or a craft in flight** against the mission, and says what the Δv
means in fuel, in KSP's units (one unit of liquid fuel or oxidizer is 5 L). docs/MATH.md §7.9.

### Set up a station-to-station mission

- [ ] In the **Vessels** pane, import **Kerb Station** and **Mun Station** as places if they aren't already (they show
      under Locations, linked to the save)
- [ ] New mission: stop 1 **Kerb Station**, stop 2 **Mun Station**. The Rocket pane has **no Launch** phase (just
      Transfer and so on)

Notes:

### Checking the shuttle

- [ ] Rocket pane, **Check a design or craft**: below the saved designs, *In flight: …* rows, including
      *In flight: Kerb Shuttle mk1 (docked at Kerb Station, ~2,062 m/s left)*. No stations and no craft without engines.
- [ ] Pick the shuttle. The hint under the picker says it's checked with what's in its tanks now, as one stage.
- [ ] The verdict box says Enough / Not enough, and below it one of:
      *"Kerb Shuttle mk1 has the fuel aboard: the trip burns … of it"*, or
      *"Before leaving, pump in N Liquid Fuel, M Oxidizer…"*, or
      *"…more than Kerb Shuttle mk1's tanks hold even full…"*
- [ ] Each phase row ends with **"burns N Liquid Fuel, M Oxidizer"**. The rows add up to about the trip total.
- [ ] In KSP, transfer the amount it says to pump in from the station's tanks into the shuttle, let KSP save (switch
      vessel or go to the Space Center), and check the Rocket pane again: it now says the fuel is aboard.
- [ ] Raise a phase's margin: the amount to pump in goes up

Notes:

### Checking a design, and the launch hint

- [ ] Pick a saved design (e.g. *Kerb Shuttle mk1* from the VAB list): each phase still shows its fuel, from the stage
      that burns it. No refuel line (that's only for craft in flight).
- [ ] Make stop 1 **Kerbin — Surface** and pick the shuttle again: a note says it's already in orbit and the mission
      counts a launch, with how to start where it is

Notes:

### Things to watch for

- [ ] The picker refreshes when KSP saves. If the selection jumps or flickers while the game is live, note it.
- [ ] Compare the Δv the pane gives the shuttle with KSP's own readout (stage Δv in vacuum) after undocking it

Notes:

## 3. Meeting a craft around another body (Rendezvous)

The trip there and the meeting are planned as one, from the craft's real orbit: the trip comes in near the target's
plane, is captured just under (or over) its height, and leaves when the target's plane lines up best. Offers
**Cheapest**, **Balanced** (soonest meeting within 10 % of the cheapest) and **Meets soonest**. docs/MATH.md §4.6, §3.9.

In the sandbox against the Protogen save (2026-10-27), Kerb Shuttle mk1 → Mun Station gave: Cheapest **1,108 m/s**
(838 transfer + 3 aim + 252 capture at 49.5 km + 15 to meet), Balanced · meets soonest 1,194 m/s, leaving in 49 min.

### Rendezvous pane (Plan mode)

- [ ] Tools › Rendezvous › Change… › **A craft already in orbit** › pick **Kerb Shuttle mk1 (… docked at Kerb Station)**,
      add target **Mun Station**
- [ ] The plan starts with **Fly from Kerbin to Mun**: a note on how it's planned, the ways to go (Cheapest / Balanced …)
      with total, leave time and meet time, then the burns: Transfer to Mun, Aim for Mun Station's orbit, Capture at Mun
- [ ] Then the meeting steps (Match planes, phasing/transfer, Match velocity): all small (tens of m/s)
- [ ] Pick the other way in the list: the burns and meeting change; the total matches the row
- [ ] The Budget lists "Fly from Kerbin to Mun" and its burns before "Meet Mun Station"
- [ ] A relay around the Mun (MunLink) as the target: compare the total with Go to Mun + a separate rendezvous (the old
      way). It should be far cheaper for a tilted relay.
- [ ] Start from a Mun craft (Grand Lander) with a Kerbin target (Kerb Station): plans the trip up the same way
- [ ] **Fly** it: the Flight pane shows the trip burns first, then the meeting

Notes:

### Flying it (Fly mode)

- [ ] Flying the station stack with **Plan for: Kerb Shuttle mk1**, target **Mun Station** in KSP
- [ ] From here › **Rendezvous with the target** (or Go to… › the target) › Plan: "Working out the way to Mun Station…",
      then **Ways to meet it** with the options, and the trip + meeting burns. The result line splits trip and meeting.
- [ ] **Send** the first burn (the transfer), undock, fly it. Then in the Flight pane **Re-plan from here** while still
      around Kerbin: it re-plans the trip from the real path (a course correction).
- [ ] After undocking (before and after KSP saves again), **Send** another burn: no "you're flying X, but this
      flight is flown by Y" warning, and the Flight pane still shows the shuttle's orbit, not the station's
- [ ] Switch to the station after undocking: **Plan for** no longer offers the shuttle, and the planner plans for the station
- [ ] Once captured around the Mun, Re-plan: plans the meeting from the real orbit
- [ ] Compare the actual Δv used with the plan (KSP's Δv readout or the flight log)

Notes:

### Things to watch for

- [ ] How long planning takes (it tries 12 departure times): note if the pane feels stuck
- [ ] If the trip can't be planned, the old Δv-map estimate shows instead, saying why. Note the message.
- [ ] Any burn that looks wrong in KSP's map view (e.g. the aim burn sending the path somewhere odd)

Notes:

## 4. Launching from the ground to meet a craft (KSC or any body)

Rendezvous' **Launch from the ground** lifts off from the KSC, or from a landed craft, flag or KerbNet waypoint on any
body, straight into the target's plane when it can. The climb's defaults follow the body (Kerbin 80 km / 6 min / 30°;
the Mun 10 km / 2 min / 7°): tune them to your own ascents. A body other than Kerbin needs its rotation, from a surface
bake or the live game. docs/MATH.md §4.5.

### KSC to Kerb Station

- [ ] Tools › Rendezvous › Change… › **Launch from the ground** › From: **Kerbin — Kerbal Space Center**, target **Kerb Station**
- [ ] Lift-off times with headings, the first step "Launch from Kerbal Space Center", then the meeting
- [ ] Fly it: at the planned lift-off, Kerb Station's AN or DN marker should be over the KSC. Climb at the heading, and
      note how far off the parking orbit's tilt and the meeting's Δv come out

Notes:

### A lander on the Mun to Mun Station

- [ ] Land a craft on the Mun (or use one there) and let KSP save. It's listed under From as *Mun — … (landed craft)*.
      Debris and surface experiments aren't.
- [ ] Connected to KSP (or with the Mun baked): pick it and add **Mun Station**. The targets list only shows Mun craft
      now. Parking 10 km, climb 2 min, downrange 7° are filled in.
- [ ] Not connected and not baked: the pane says it needs the Mun's rotation. Connect: it plans at once.
- [ ] Flying the lander: **Use the craft I'm flying** picks its spot and the launch start
- [ ] Fly it: lift off at the planned time and heading, note the parking orbit's tilt against the station's, and the
      meeting's Δv against the plan's. Adjust Climb / Downrange to your ascent and plan again.

Notes:

### Things to watch for

- [ ] Picking a spot on another body drops targets around the old one (expected); note anything else that resets
- [ ] A spot far from the equator (e.g. a flag at 30°) to a near-equatorial station: the plan should fall back to the
      due-east launch and fix the plane in orbit

Notes:

## 5. Where a trip's Δv goes

Fly mode's **Go to…** options now split each total: what it takes to leave, what's burned on the way (aiming for an
inclination, corrections) and what the capture takes. docs/MATH.md §3.8.

- [ ] From a round ~80 km Kerbin orbit, **Go to… Mun** (30 km): an option reads about
      *"… · 1,114 m/s (853 to leave + 262 to capture)"*. The 853 should match KSP's own transfer burn.
- [ ] With an inclination (e.g. 90°): a third part *"+ 35 on the way"* appears
- [ ] Already on the way (planned again after the transfer burn): the first part reads *"to correct"*
- [ ] The result line under the burns shows the same split
- [ ] A trip that aerobrakes (e.g. to Duna, low enough): *"no capture burn"*

Notes:
