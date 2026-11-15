# Power and heat

Planning a base or station means two questions the stock game doesn't answer while you build: do the batteries last the night, and can the radiators keep up with the drills and the ISRU? The planner's in-game add-on (`mod/KspFlightPlanner.AddOns`, folder `PowerHeat/`) answers both. It works in the editor while you build, and in the Station window once the craft is out there. Everything is worked out in game from the parts themselves, so modded parts that use the stock modules count too. The sums are under [How the numbers are worked out](#how-the-numbers-are-worked-out); how long the night is comes from [MATH.md §11](MATH.md#11-day-and-night).

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

## How the numbers are worked out

The sums behind the Power & Heat window and the Station window's Power and Heat sections: the same power budget as [Power through the longest night (power budget)](#power-through-the-longest-night-power-budget), worked out in game from the live parts, plus core heat. How long the night is comes from [MATH.md §11](MATH.md#11-day-and-night).

### Power through the longest night (power budget)

The **Power budget** adds up the electric charge (EC) a craft makes and uses where it's going, and checks its batteries last the longest night there. The part data gives each part's rates: solar panels (`chargeRate`), generators like the RTG, fuel cells (with the propellant they burn), and what probe cores, reaction wheels, antennas, lights, the lab, ISRU converters and drills use. Ion engines burn charge as a propellant at full thrust, ṁ · ratio_EC / Σ(ratio · density).

```
solar  = chargeRate · (d_home / d)²            (rated at Kerbin's distance from the Sun; d for a moon is its planet's)
used   = Σ loads switched on                   (only one antenna transmits at a time: the hungriest is counted)
dark   = generators + fuel cells (up to what's missing)
needed = (used − dark) · t_night               (when the dark doesn't cover it)
lasts  = battery / (used − dark)
refill = needed / (made in the sun − used)     (it must fit in the day)
```

**The longest night.**
- **In orbit**, the worst case is the Sun in the orbit's plane. The craft then crosses the body's full shadow, taken as a cylinder as wide as the body: t_night = T · asin(R / a) / π, with T = 2π √(a³ / μ).
- **On the surface**, night is half a solar day at the equator. The solar day is the rotation measured against the Sun, which the planet moves around once a year: S = P · Y / (Y − P). A moon uses its planet's year. A tidally locked moon turns once per orbit, so its day is about as long as its month.

Worked example (the numbers in `PowerBudgetTests`):

1. 80 km over Kerbin: T = 2π √(680,000³ / 3.5316 × 10¹²) = 1,874.8 s, and t_night = 1,874.8 × asin(600 / 680) / π = **645.0 s**, leaving 1,229.8 s of sunlight
2. Kerbin's year: Y = 2π √(13,599,840,256³ / 1.1723328 × 10¹⁸) = 9,203,545 s, so its solar day is 21,549.4 × Y / (Y − 21,549.4) = **21,600 s**, KSP's 6-hour day. Night is 3 h
3. The Mun (P = 138,984.4 s, Kerbin's year): S = 141,115 s, so **70,558 s** of night (19 h 36 min)
4. A probe there (an OKTO2 at 0.03 EC/s, two Z-100s for 205 EC including the core's 5, and two OX-4W panels at 1.64 EC/s). The night uses 0.03 × 645.0 = **19.35 EC**. The day refills that in 19.35 / (3.28 − 0.03) = **6.0 s**
5. At Duna the same panels make (13.60 / 20.73)² = **43.1%** of their rating
6. A Dawn ion engine at full thrust: ṁ = 2 / (4,200 × 9.80665) t/s, × 1.8 / (0.1 × 0.0001) = **8.74 EC/s**

Panels are assumed to face the Sun. Fixed panels (OX-STAT) only do that when the craft is turned to it. A moon's planet can also eclipse it, which isn't counted. Fuel cells only run to cover what's missing, as KSP stops them when the batteries are nearly full.

- Code: `PowerBudget` (`Work`, `InOrbit`, `OnSurface`, `SolarScale`, `EngineEcPerSecond`), `PartCatalogReader` (`PartPower`); tested in `PowerBudgetTests`
- Source: KSP 1.12 part modules (`ModuleDeployableSolarPanel`, `ModuleGenerator`, `ModuleResourceConverter`, `ModuleCommand`, `ModuleReactionWheel`, `ModuleDataTransmitter` packetResourceCost / packetInterval, `ModuleLight`, `ModuleScienceConverter`, `ModuleResourceHarvester`); the inverse-square solar law; synodic period for the solar day

### Charge through the night


The parts' rates come from their own modules: solar panels' `chargeRate` × `efficiencyMult`, scaled by the panel's own `powerCurve` at the distance from the Sun (inverse square if it has none). Then generators (RTGs), fuel cells (converters whose output is charge), and the loads: probe cores, lights, the lab's `powerRequirement`, drills and ISRU converters (charge in their recipe), and radiators. A loaded craft also measures the real flow from its batteries: the change in stored charge over the last 10 s of game time (not while they're full or empty).

```
day surplus = solar + generators − loads                       (fuel cells off: they burn fuel)
night drain = max(0, loads − generators − fuel cells)          (fuel cells cover only what's missing)
night need  = night drain · t_night
lasts       = batteries / night drain
refill      = night need / day surplus                          (must fit in the day)
```

Worked example: a Minmus base with three Gigantor panels (24.4 EC/s each, 73.2 in all; Minmus is at Kerbin's distance, so 100% of their rating), a Convert-O-Tron 250 making Lf+Ox (30 EC/s), a Drill-O-Matic (15 EC/s), four small Thermal Control Systems (0.025 EC/s each) and one Z-4K battery (4,000 EC). Night is 20,289 s ([MATH.md §11.1](MATH.md#111-night-on-the-surface-and-the-next-sunset)).

1. Everything on: loads = 30 + 15 + 0.1 = 45.1 EC/s. The day has 73.2 − 45.1 = **28.1 EC/s** spare
2. At night the batteries give 45.1 EC/s and the night takes 45.1 × 20,289 = **915,037 EC**: they run out in 4,000 / 45.1 = **89 s**
3. ISRU and drill switched off at night: the radiators' 0.1 EC/s takes **2,029 EC**, which fits. The day wins it back in 2,029 / 73.1 = **28 s**

- Code: `PowerReader.Of`, `PowerBudget.Of`, `.DistanceScale`, `PowerMeter.Sample` (mod/KspFlightPlanner.AddOns/PowerHeat)
- Source: KSP 1.12 part modules (`ModuleDeployableSolarPanel`, `ModuleGenerator`, `BaseConverter`, `ModuleScienceConverter`, `ModuleCommand`, `ModuleLight`, `ModuleActiveRadiator`); [Power through the longest night (power budget)](#power-through-the-longest-night-power-budget)

### Core heat and radiators


Drills and ISRU converters heat a core (`ModuleCoreHeat`). Radiators pull heat out of it only once it reaches its goal temperature, so the question is whether they can take all of it at the goal. KSP's heat figures are energy per 1/50 s, so dividing by 50 gives kW, the numbers its part info shows. Read from the game's code:

```
a core's need = Σ over running converters of TemperatureModifier(goal) / 50 · radiatorCoolingFactor
it draws at most MaxCoolant
a radiator gives maxEnergyTransfer / 50                          (only cooling, and extended if it folds)
enough when Σ radiators ≥ Σ min(need, MaxCoolant)
```

Every running converter adds its own heat to the core (`ResolveConverterEnergy`), so an ISRU making two things at once makes twice the heat. KSP's part info shows only one converter's worth.

Radiators pool their cooling for every core (shared out in proportion to MaxCoolant). The exception is the flat radiator panels (`parentCoolingOnly`), which only cool the part they're mounted on and that part's other children. Short of cooling, the core climbs past its goal. Converters slow down along their `ThermalEfficiency` curve (the ISRU is at 10% by 1,250 K) and stop at the shutdown temperature. Engineers aboard cut the heat a little; that isn't counted.

Worked example (the base in [Charge through the night](#charge-through-the-night)):

1. Convert-O-Tron 250: TemperatureModifier(1,000 K) = 10,000, so 10,000 / 50 × 1 = **200 kW** for one recipe, and MaxCoolant is 200. All four recipes at once would make 800 kW, more than it can shed, so it overheats whatever the radiators
2. Drill-O-Matic: TemperatureModifier(500 K) = 5,000, so **100 kW**, and MaxCoolant is 100
3. A small Thermal Control System: 2,500 / 50 = **50 kW**. Four give 200 kW of the 300 needed: **short 100 kW**. Six small ones, or a medium (12,500 / 50 = 250 kW) and a small, keep both cores at their goal

- Code: `PowerReader` (heat), `PowerBudget.Heat` (mod/KspFlightPlanner.AddOns/PowerHeat)
- Source: KSP 1.12 `ModuleCoreHeat.MoveCoreEnergyToRadiators`, `.GetInfo`, `ModuleActiveRadiator.GetInfo` (read from Assembly-CSharp); stock part configs (ISRU, RadialDrill, foldingRadSmall/Med)

## Not yet

- The planet eclipsing its moon (the Mun goes dark behind Kerbin once a month), and crater walls.
- Several separate craft making up one base. Each craft has its own charge (KSP doesn't share it between craft that aren't docked), so look at each one in turn.
- Sending these numbers to the planner app. The app's Engineering › Power budget works the same sums from saved craft.
