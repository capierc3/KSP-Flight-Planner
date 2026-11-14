# Power and heat

Planning a base or station means two questions the stock game doesn't answer while you build: do the batteries last the night, and can the radiators keep up with the drills and the ISRU? The planner's in-game add-on (`mod/KspFlightPlanner.AddOns`, folder `PowerHeat/`) answers both. It works in the editor while you build, and in the Station window once the craft is out there. Everything is worked out in game from the parts themselves, so modded parts that use the stock modules count too. The maths is in [MATH.md §13](MATH.md#13-power-and-heat-in-game).

## In the VAB and SPH: the Power & Heat window

A toolbar button (a lightning bolt) opens the window for the craft being built.

- **Where it's going.** ◀ ▶ step through the bodies (Minmus is picked first). **Surface** takes the equator. **Orbit** takes a circular orbit at the altitude you type in km, with its longest night. The pick is kept until you quit the game.
- **Power.** What the panels make in full sun there (their rating scaled by the distance from the Sun), what generators and fuel cells add, and what the loads use. Then whether the batteries last the night, and how long full sun takes to refill them.
- **Heat.** What each drill and ISRU needs from the radiators at its goal temperature, against what the radiators give.
- **What-ifs.** Every part starts *On*: panels deployed, drills running, radiators cooling. Click a part's **On** to switch it off and see the numbers change. That's how to try "ISRU by day only" or "lab only at night". **Reset switches** puts everything back.
- **One recipe per ISRU.** A part with several recipes (an ISRU's Lf+Ox, Monoprop, LiquidFuel, Oxidizer) gets one switch that picks which recipe runs, starting with the first. Click it to step through the recipes and then Off. KSP itself lets every recipe run at once, each using its own charge and adding its own heat; the plan assumes one at a time. The Station window shows what's really running.

### Modules

You can build the whole station as one craft and launch it a module at a time. The window then shows each module on its own, since each one has to get through its nights alone until it's docked.

- **Where it splits:** only where two docking ports are attached port to port. Each port stays with the parts on its own side, so a module runs from port to port, or from a port to the end of the craft. A single port between ordinary parts doesn't split anything. With no port pair, the window says so.
- **Names:** each module is named by the first of these it has: drills (**Drilling**), an ISRU (**ISRU**), a science lab (**Science**), a crewed part with controls (**Command**), other seats (**Habitat**), engines (**Ship**: the launcher or a tug), panels, generators or 1,000 EC or more of battery (**Electric**), tanks (**Fuel**), a probe core (**Probe**). Two of the same get numbers: Electric module 1, Electric module 2.
- **The list:** **Whole station** and then each module (shown once the craft splits), with its part count and one line: what it makes and uses, its battery, whether it lasts the night where you picked, and whether its radiators keep up. Click one to see it in detail below. Your what-if switches apply everywhere.

Each module is checked in the place you picked. To check a module in its parking orbit on the way up, pick **Orbit** and that altitude.

## In flight and at the KSC: the Station window

The Station window (Traffic Controller, [TRAFFIC-CONTROLLER.md](TRAFFIC-CONTROLLER.md)) has two more sections, **Power** and **Heat**. They cover the whole docked stack, since docked craft share one electrical system.

- **The craft you're flying** (loaded): parts as they are switched now. The panels' output this moment, the charge flow measured from the batteries, and the Sun where the craft really is. Landed, that means the next sunset or sunrise at its latitude. In orbit, the next shadow on its real orbit. In the dark, it says whether the charge left lasts until sunrise at the measured draw. Heat shows each core's temperature and how fast its converters run at it.
- **Unloaded craft** (at the KSC, or far away): rates from the parts, on/off from the save. The night is the equator's on the surface, or the longest at its mean altitude in orbit.

## What counts

| Kind | Parts (module) | When |
|---|---|---|
| Solar | panels (`ModuleDeployableSolarPanel`) | In sunlight, deployed and not broken. Rated facing the Sun at Kerbin's distance, scaled by the panel's own curve. Fixed panels make their rating only when the craft faces the Sun, so they're called out. |
| Generators | RTGs (`ModuleGenerator`) | Day and night |
| Fuel cells | converters that make charge (`ModuleResourceConverter`) | Only to cover what's missing at night (they burn fuel), not counted by day |
| Loads | probe cores (`ModuleCommand`, less when hibernating), lights, the science lab's research, drills and ISRU (charge in their recipe), radiators | While switched on |

Not counted: antennas (charge only while sending), reaction wheels (only while turning), ion engines, and parts with their own modded modules. The measured flow on a loaded craft does catch all of them.

**Heat.** A drill or converter heats its part's core. Radiators take that heat only once the core reaches its goal temperature, so what matters is the heat at the goal. Each core asks for its running converters' heat there, added up (two recipes at once make twice the heat), and can't draw more than its *max cooling*. Folding radiators pool their cooling for every core on the craft. Flat radiator panels only cool the part they're mounted on and that part's other children, so the window warns when such panels aren't next to any drill or converter. Short of cooling, the core climbs past its goal, the converters slow down, and they stop at the shutdown temperature.

## Not yet

- The planet eclipsing its moon (the Mun goes dark behind Kerbin once a month), and crater walls.
- Several separate craft making up one base. Each craft has its own charge (KSP doesn't share it between craft that aren't docked), so look at each one in turn.
- Sending these numbers to the planner app. The app's Engineering › Power budget works the same sums from saved craft.
