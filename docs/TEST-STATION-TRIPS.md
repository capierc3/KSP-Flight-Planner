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
