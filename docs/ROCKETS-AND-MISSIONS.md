# KSP Flight Planner — Rockets, missions and flights

How the planner's mission, rocket and flight tools add up their numbers from the game's own data and rules: staging, mass and cost, CommNet, drag, mission legs and flights. The orbital mechanics underneath is in [MATH.md](MATH.md).

## How the numbers are worked out

### Leg tools: replacing map values with a planned leg

A mission leg runs from one stop to the next and is made of Δv-map steps ([MATH.md §6.1](MATH.md#61-the-δv-map-and-mission-budgets)). A leg tool works out part of the leg properly and puts its result in place of the map values for the steps it covers. The steps stay, so each one keeps its phase and aerobrake choice. A tool that no longer fits the leg (the stops changed) is ignored, and the map values come back.

The tools are transfer windows ([MATH.md §6.2](MATH.md#62-transfer-legs-a-chosen-launch-window)), constellation deployments ([MATH.md §6.3](MATH.md#63-deploy-steps-a-constellation-released-at-a-stop)), rendezvous ([MATH.md §6.4](MATH.md#64-rendezvous-legs-meeting-a-placed-craft)), landing at a chosen spot ([MATH.md §2.6](MATH.md#26-landing-at-a-chosen-spot)), launching into an inclined orbit ([MATH.md §2.7](MATH.md#27-launching-into-an-inclined-orbit)) and arriving at another body in an inclined orbit ([MATH.md §3.9](MATH.md#39-arriving-in-a-chosen-inclination-a-polar-orbit-at-the-mun)). A landing replaces the last step, from a low orbit down to the surface, with its deorbit and braking burns on airless bodies. An inclined launch adds its extra over a due-east launch to the launch step, on top of the map's value, and measures later plane changes around the body from its inclination. An inclined arrival adds the burn on the way that tilts the approach to the step that crosses into the body's sphere.

### A saved design's stage-by-stage Δv

Ships saved in the VAB or SPH (`saves/<save>/Ships/*.craft`) list every part with the stage that fires it (`istg`), the stage whose decoupler drops it (`sepI`), what it's attached to, its fuel, thrust limiters and fuel lines. The planner flies the staging the way KSP's stage Δv readout does:

```
for stage s = first … 0:
    attached  = parts not dropped yet          (sepI < s; launch clamps go at the first stage)
    engines   = attached engines fired in stage s or earlier
    burn in steps — each step lasts until the next tank runs dry:
        each engine draws from the tanks it can reach (below), soonest-dropped first
        Δv += (ΣF / Σṁ) · ln(m_start / m_end)        ([MATH.md §7.1](MATH.md#71-a-vessels-remaining-δv-rocket-equation): ΣF / Σṁ = Isp_eff · g₀)
    the stage ends when its engines run dry, or when the parts the next stage drops have all been spent
TWR(s) = ΣF / (m_start · g_Kerbin),  g_Kerbin = μ / R² = 9.81 m/s²
```

**Which tanks an engine reaches.** Solid fuel stays in its own part. Liquid fuel and oxidizer flow through attached parts that allow crossfeed — decouplers don't, unless their crossfeed is switched on — and along fuel lines from the part a line is attached to into the part it points at. Monopropellant and xenon reach the whole vessel. Tanks that will be dropped soonest are drained first, which is what makes asparagus staging and drop tanks work; tanks dropped together drain together.

**Sea level.** An engine's fuel flow is fixed by its vacuum rating, so at sea level the same flow gives thrust × Isp_ASL / Isp_vac. The "Δv ASL" column is the stage flown entirely at Kerbin sea level — only meaningful for the first stage.

Worked example 1 — a two-stage stack (the numbers in `StagingSimulationTests`): each half is a 0.5 t tank with 1 t of fuel and a 1 t engine (200 kN, Isp 300 s vacuum / 250 s sea level), joined by a 0.05 t decoupler that blocks crossfeed.

1. Launch mass = 2 × (0.5 + 1 + 1) + 0.05 = **5.05 t**; TWR = 200 / (5.05 × 9.81) = **4.04**
2. Stage 1 burns only the lower tank: Δv = 300 × 9.80665 × ln(5.05 / 4.05) = **649.2 m/s** (541.0 m/s at sea level), lasting 1 t ÷ (200 / (300 × 9.80665)) t/s = **14.7 s**
3. Stage 0 drops the lower half and starts at 2.5 t: Δv = 300 × 9.80665 × ln(2.5 / 1.5) = **1,502.8 m/s**

Worked example 2 — boosters that burn out first: a core (1 t tank with 4 t of fuel, the same engine) with two solid boosters (1 t dry + 0.75 t solid fuel, 100 kN, Isp 200 s) on 0.05 t radial decouplers. Launch mass 9.6 t.

1. Each booster's fuel lasts 0.75 ÷ (100 / (200 × 9.80665)) = **14.7 s**; the core burns 1 t in that time → 7.1 t
2. Together the engines give Isp_eff = 400 kN ÷ (9.80665 × Σṁ) = **240 s**, so Δv = 240 × 9.80665 × ln(9.6 / 7.1) = **710.0 m/s** — then the stage ends because everything the next stage drops is spent
3. Dropping the boosters and decouplers (2.1 t) leaves 5.0 t: Δv = 300 × 9.80665 × ln(5.0 / 2.0) = **2,695.7 m/s**. Carrying the empty boosters instead would give only 300 × 9.80665 × ln(7.1 / 4.1) = 1,615.5 m/s — about 1,080 m/s less.

- Code: `CraftReader.Parse`, `StagingSimulation.Simulate`; tested in `StagingSimulationTests`
- Source: KSP 1.12 craft format and fuel-flow rules (resource flow modes, `fuelCrossFeed`, `CModuleFuelLine`); MechJeb's fuel-flow simulation for the staging approach; Tsiolkovsky rocket equation ([MATH.md §7.1](MATH.md#71-a-vessels-remaining-δv-rocket-equation))

### Does a design have enough Δv for the mission?

In the Rocket pane you can pick a saved design. Its stages ([A saved design's stage-by-stage Δv](#a-saved-designs-stage-by-stage-δv), vacuum Δv, in firing order) are poured into the mission's phases ([MATH.md §6.1](MATH.md#61-the-δv-map-and-mission-budgets), in burn order): the first stage fills the first phase; whatever it has left carries into the next phase; when a stage runs out part-way through a phase, the next stage takes over — as in flight.

Margins are wiggle room, not a requirement, so the stages are poured twice — once against each phase's Δv **with** its margin, once **without** — and each phase gets one of three verdicts:

| Verdict | Meaning |
|---|---|
| ✓ Enough (green) | Covers the legs and the whole margin |
| ✓ Enough, inside the margin (amber) | Covers the legs, but uses part of the margin |
| ✗ Not enough (red) | Can't cover the legs themselves — the shortfall is shown before any margin |

The mission's verdict is its worst phase's.

```
for each stage, in firing order:
    while the stage has Δv left and a phase still needs some:
        use = min(stage Δv left, phase Δv still needed)
spare = Δv left in the stages once every phase is covered     (short = phase Δv nothing was left for)
```

Vacuum Δv is right here because the Δv map's launch figure (3,400 m/s to low Kerbin orbit) already includes atmospheric and gravity losses. The pane also shows the first stage's sea-level TWR — below 1 the rocket can't leave the pad.

Worked example — Beyonder Mk1 (stages 6: 2,040, 5: 3,322, 3: 1,793 m/s) against Kerbin → Mun surface → Kerbin with 10 % margins (Launch 3,740, Transfer 1,925, Lander 979 m/s):

1. Launch: stage 6 gives 2,040, stage 5 the other 1,700 → covered by stages 6, 5
2. Transfer: stage 5's remaining 1,622 + 303 of stage 3 → covered by stages 5, 3
3. Lander: 979 of stage 3's remaining 1,490 → covered; **511 m/s to spare beyond the margins** (7,155 − 6,644)

Worked example — KerbLink Mk2 (stages 2: 2,831, 0: 2,047 = 4,878 m/s) to keostationary orbit, which needs 4,515 m/s plus 452 m/s of margins (4,966):

1. With margins it's 89 m/s short of 4,966 — but the margin is only wiggle room
2. Without margins it covers the 4,515 with **363 m/s to spare**, so the verdict is "Enough — inside your margins" (amber). The real KerbLink Mk2 A reached its orbit with 480 m/s left.

- Code: `DesignCheck.Check`; tested in `DesignCheckTests`

### A design taken apart: mass, cost and what each stage drops

The **Engineering** document reads a saved design (.craft) and adds up what it's made of. Each part's mass is its dry mass plus what its tanks hold as loaded. Its cost is KSP's: a part's listed cost is with its tanks full, so the full contents come off and what's actually loaded goes back on.

```
part mass = dry mass + Σ amount · density
part cost = listed cost − Σ max amount · unit cost + Σ amount · unit cost
```

Each part in a .craft carries `sepI`, the stage whose firing drops it (−1 if it stays to the end). Parts are listed by that stage, in firing order. A stage's own decouplers fire as it starts, so in the stage table "Leaves behind" is what the *next* firing drops, the parts that stage used up. "Left at the end" is the mass once the last stage has burned ([A saved design's stage-by-stage Δv](#a-saved-designs-stage-by-stage-δv)).

It also flags:
- **An engine that never fires with fuel**: the staging ([A saved design's stage-by-stage Δv](#a-saved-designs-stage-by-stage-δv)) never burns it, because no stage lights it while a tank it can draw from still has propellant.
- **An engine dropped before its own stage fires** (`sepI ≥ istg`): a deployed payload, such as a satellite with its own engine. That's not a mistake, but the payload is flown on its own after it separates, so its Δv isn't counted.
- **A launch TWR below 1** for a VAB design: it won't leave the pad.
- **Parts the save hasn't unlocked yet** (from the save's research, as in the tech tree).

Worked example (the numbers in `CraftBreakdownTests`): a pod (0.8 t, √600) on a full tank (0.5 t dry, 90 LF + 110 Ox = 1 t, √500 full) and an engine (1 t, √1,000), with a decoupler (0.05 t, √200) dropping a **half-full** tank and a second engine when stage 0 fires. Fuel costs √0.8 per LF and √0.18 per Ox.

1. A full tank's fuel is worth 90 × 0.8 + 110 × 0.18 = √91.8, so the half-full tank costs 500 − 91.8 + 45.9 = **√454.1**
2. Dropped when stage 0 fires: decoupler, half-full tank and engine, 0.05 + 1.0 + 1 = **2.05 t** and 200 + 454.1 + 1,000 = **√1,654.1**
3. Stays to the end: pod, full tank and engine, **3.3 t** and **√2,100**. In all **√3,754.1**
4. By part type (dry): engines 2 t, tanks 1 t, pod 0.8 t, decoupler 0.05 t (3.85 t), plus 1.5 t in the tanks: 5.35 t at launch
5. Left at the end: the pod, the empty tank and the engine, **2.3 t**

- Code: `CraftBreakdown.Build`, `StagingSimulation` (which engines fired); tested in `CraftBreakdownTests`
- Source: KSP 1.12 craft format (`istg`, `sepI`, `RESOURCE` amounts) and part costs (`cost` includes full resources, as in the editor)

### Antenna range and signal (CommNet planner)

The **CommNet planner** works out how far a craft's antennas reach, and whether it can talk home from each body. It follows KSP's CommNet rules.

**A vessel's antenna power.** Every antenna has a power rating in metres. Antennas that combine add up, but with diminishing returns:

```
P = P_max · (ΣP / P_max)^e        e = Σ(P_i · e_i) / ΣP_i      (the power-weighted average combinability exponent)
```

An antenna that doesn't combine (the DTS-M1, for one) counts on its own, and the vessel uses whichever is stronger. A probe core's internal antenna is only used when there's no other antenna. Only **relay** antennas pass other vessels' signals on, so a relay's power for that is worked out from its relay antennas alone. The game's *Range Modifier* setting scales every antenna.

**Range and signal strength.** Two antennas can talk up to the geometric mean of their powers. The tracking station counts as an antenna of 2 Gm, 50 Gm or 250 Gm at levels 1, 2 and 3, scaled by the *DSN Modifier* setting. Signal strength falls smoothly with distance:

```
range    = √(P₁ · P₂)
x        = 1 − d / range        (clamped to 0…1)
strength = x² · (3 − 2x)        (100% close by, 50% at half the range, 0 at the range)
```

**How far a body gets from home.** Two bodies around the same parent are farthest apart on opposite sides of it, each at its apoapsis, a(1 + e). A moon adds its own apoapsis to its planet's. Home's moons are just their apoapsis away. The planner checks each body at its farthest, since that's the worst case for a craft there.

The planner reads the tracking station's level and both modifiers from the save. Career saves record the level; sandbox and science saves have every building upgraded, so it's level 3. Planets and moons blocking the line of sight aren't counted.

Worked example (the numbers in `CommNetTests`, and the MunLink Shuttle in the planner):

1. Two Communotron 16s (500 km, exponent 1): 500 km × (1,000 / 500)¹ = **1,000 km**. Two HG-55s (15 Gm, exponent 0.75): 15 × 2^0.75 = **25.227 Gm**
2. A Communotron 16 to a level 1 tracking station: √(500 km × 2 Gm) = **31.62 Mm**, so it loses Kerbin's signal past about 31 Mm
3. Three RA-2 relays (2 Gm, exponent 0.75): 2 × 3^0.75 = **4.56 Gm**, so √(4.56 Gm × 250 Gm) = **33.8 Gm** to a level 3 tracking station
4. Duna at its farthest: 20,726,155,264 × 1.051 + 13,599,840,256 = **35.4 Gm**. That's past 33.8 Gm, so out of range
5. Moho at its farthest: 5,263,138,304 × 1.2 + 13,599,840,256 = 19.9 Gm. x = 1 − 19.9 / 33.8 = 0.410, so strength = 0.410² × (3 − 2 × 0.410) = **37%**

- Code: `CommNet` (`CombinedPower`, `RelayPower`, `Range`, `Strength`, `FarthestFromHome`), `KspSaveReader` (`SavedCommNet`); tested in `CommNetTests`
- Source: KSP 1.12 CommNet (`ModuleDataTransmitter` antennaPower / antennaCombinable / antennaCombinableExponent, the tracking station's DSN levels, `CommNetParams`); the KSP wiki's CommNet page for the combining and strength formulas

### Comparing a rocket's flights

A rocket is the vessel's name at launch. The Compare flights document puts its flights side by side. Each row shows Δv used, Δv to orbit, time to orbit, ascent losses (gravity, and drag & steering, [MATH.md §8.4](MATH.md#84-the-flight-report-ascent-losses-burns-and-touchdowns)), max Q, max g and touchdown speed (vertical, the moment before contact, of the last touchdown). Lower is better for every one of these, so the lowest in each column is picked out.

**Versions of the design.** The same name can fly a changed rocket. On the pad, the recorder counts the vessel's parts (with its launch clamps). With the mass at the first sample after lift-off, that's the design's fingerprint. Flights are lettered A, B… in the order first flown. A flight joins a version when it has the same part count (or either count is unknown) and a launch mass within 1 %:

```
same version ⇔ (parts₁ = parts₂ or either unknown) and |m₁ − m₂| ≤ 0.01 · m₁
```

1 % lets a little more or less fuel, or a kerbal, count as the same rocket, while a new tank or engine doesn't. A flight joined under way has no launch to go by and gets "—".

**Overlaid charts.** For the ascent and the whole flight, MET lines the flights up at launch. For a landing, the time since its descent began (the last sample in orbit, [MATH.md §8.4](MATH.md#84-the-flight-report-ascent-losses-burns-and-touchdowns)) lines them up, and Δv used counts from that moment.

Worked example: three flights of Kerbal X at 30.0 t (42 parts), 30.2 t (42 parts) and 33.0 t (42 parts). 30.2 is within 1 % of 30.0 (0.3 t), so it's version **A**. 33.0 isn't (3 t over), so it's **B**. Their Δv to orbit of 3,400, 3,350 and 3,520 m/s have the 3,350 picked out, but the summary warns that two versions are being compared.

- Code: `FlightComparison.Designs`, `FlightComparison.Figures`, `FlightComparison.Best` (`Core/Ksp/FlightComparison.cs`), shown by `FlightCompareViewModel`; the Rocket pane's line comes from the same analysis; tested in `FlightComparisonTests`

### Flying a whole mission

**Fly this mission** (Mission pane), **Fly** (Saved missions) or **Fly** in Fly mode's Flights pane turns a mission's stops into steps, flown in order:

| From → to | Steps |
|---|---|
| A surface → anywhere else | **Launch** to the body's low orbit (or the stop's, if that's where it goes), at the stop's launch inclination ([MATH.md §2.7](MATH.md#27-launching-into-an-inclined-orbit)). Flown by hand: no burns |
| An orbit → an orbit around another body | **Go to** the body ([MATH.md §3.8](MATH.md#38-trips-anywhere-routes-launch-windows-and-options)), arriving at the stop's height; through the leg's chosen launch window ([MATH.md §6.2](MATH.md#62-transfer-legs-a-chosen-launch-window)) if it still lies ahead, else the next good one |
| → a placed craft | **Go to** its body's low orbit if needed, then **Meet** it ([MATH.md §4.1](MATH.md#41-how-a-rendezvous-is-planned)) |
| → another orbit around the same body | **Change orbit** (below), when the height, shape or a custom orbit's tilt differs from what the craft is in |
| → a surface | **Go to** the body's low orbit, then **Land** at the stop's spot ([MATH.md §2.9](MATH.md#29-landing-at-a-spot-from-where-you-are)). With no spot on a body with air, the trip aims straight into the atmosphere (periapsis at 0.43 × its height, as aerobraking in [MATH.md §3.8](MATH.md#38-trips-anywhere-routes-launch-windows-and-options)) and the landing has no burns. With no spot and no air, it's landed by hand |
| A constellation at a stop | **Release** the satellites ([MATH.md §5.2](MATH.md#52-burn-times-and-node-values)) after getting there |
| Anything else (an intercept, an escape orbit) | Flown by hand |

**The cascade.** The step being flown is planned from the craft's real orbit (live from the game, [SAVE-AND-LIVE-GAME.md “Following the live game (kRPC)”](SAVE-AND-LIVE-GAME.md#following-the-live-game-krpc)), no burn sooner than the lead time. Every step after it is planned from where the step before leaves the craft, no burn sooner than 300 s after that step ends (`Rendezvous.BurnGapS`). Each step ends in an orbit: after a trip, the orbit its capture burn leaves (or its path, with no capture); after a meeting, the target's orbit; after a launch still to fly, a round orbit at the launch height and inclination, reached 600 s after lift-off. So a launch that reaches 75 km instead of 80 changes the trip after it, and the trip's new arrival changes the landing after that. A step that can't be planned (a landing at a spot without the body's rotation, [MATH.md §2.9](MATH.md#29-landing-at-a-spot-from-where-you-are)) says why, and the steps after it wait for it.

**Moving on.** The step being flown moves on by itself while connected, then everything from there is planned again: a launch once the craft is in an orbit clear of the atmosphere; a trip once it's in orbit around the destination (or on the ground there, or on its way down when a landing comes next); a landing once it's on the ground there; an orbit change once the orbit is within 2 km of the one asked for at both apsides (and 0.1° of its inclination), or 90 s after its last burn; a meeting or a release 90 s after its last burn. **Next step** and **Back a step** do it by hand; after Back a step, the step stays put until you move on.

**Change orbit.** Two apsis burns ([MATH.md §2.1](MATH.md#21-hohmann-transfer-changing-circular-orbit-altitude), [MATH.md §2.8](MATH.md#28-quick-burns-from-where-you-are)): from the periapsis, move the far side to the new apoapsis; then from there, move the near side to the new periapsis. Going lower all round, the periapsis comes down first. A tilt to a custom orbit's inclination is flown at the equator ([MATH.md §2.3](MATH.md#23-plane-changes)), where the craft is slowest: after going up, before coming down.

Worked example: from a round 80 km Kerbin orbit to a round 250 km one at 10° (μ = 3.5316×10¹² m³/s², R = 600 km):

1. r₁ = 680,000 m, r₂ = 850,000 m, transfer a = 765,000 m
2. Raise the apoapsis: √(μ/r₁) = 2,278.9 m/s; √(μ(2/r₁ − 1/a)) = 2,402.2 m/s → **123.3 m/s**
3. Raise the periapsis: √(μ(2/r₂ − 1/a)) = 1,921.8 m/s; √(μ/r₂) = 2,038.4 m/s → **116.6 m/s**
4. Tilt 10° at 250 km: 2 × 2,038.4 × sin 5° = **355.3 m/s** (at 80 km it would be 397.2 m/s)

595 m/s in all, against 637 m/s tilting first.

The cascade in the tests: launch planned to 80 km, flown to 75 km. Once the craft is in that orbit the launch is done, and the Minmus trip is planned from it: transfer 924.5 m/s, capture 159.7 m/s into 10 km. The landing at the spot is planned from that capture orbit: deorbit 5.6 m/s, track 2.2 m/s, braking 190.2 m/s.

- Code: `MissionFlights.Steps`, `MissionFlights.Plan`, `MissionFlights.Current`, `TripPlanner.Plan`, `FlightViewModel.ReplanMissionAsync`/`AdvanceMission`; tested in `MissionFlightsTests`
- Source: the step planners' own ([MATH.md §2.1](MATH.md#21-hohmann-transfer-changing-circular-orbit-altitude), [MATH.md §2.3](MATH.md#23-plane-changes), [MATH.md §2.9](MATH.md#29-landing-at-a-spot-from-where-you-are), [MATH.md §3.8](MATH.md#38-trips-anywhere-routes-launch-windows-and-options), [MATH.md §4.1](MATH.md#41-how-a-rendezvous-is-planned), [MATH.md §5.2](MATH.md#52-burn-times-and-node-values))

### A craft's drag from its parts

KSP gives every part a **drag cube**: a box with an area and a drag value (0 to 1) on each of its six faces (+X, −X, +Y, −Y, +Z, −Z in the part's own axes), measured from the part's model when the game loads. KSP writes them to `PartDatabase.cfg` in its install folder. The rules that turn them into drag are curves in `Physics.cfg`, by Mach number. The planner reads both from your install.

For a craft moving along direction v̂, each visible face of each part adds:

  face drag area = A_face × f(facing, Mach) × Cd_curve(cube drag)^p(Mach)

- **facing** = (n̂·v̂ + 1) / 2, with n̂ the face's outward normal turned into the craft's axes by the part's rotation: 1 for a face pointing into the airflow, ½ side-on, 0 pointing away
- **f** blends three curves: from DRAG_TAIL (facing 0) to DRAG_SURFACE (facing ½) to DRAG_TIP (facing 1), in straight lines, times DRAG_MULTIPLIER
- **Cd_curve** is DRAG_CD at the face's cube drag value, raised to the power p = DRAG_CD_POWER at that Mach

The craft's drag area is the sum over every face, times `dragCubeMultiplier × dragMultiplier` (0.1 × 8 in stock). The force is then

  F = ½ρu² × drag area × PR(ρu)

with u the speed through the air and PR the DRAG_PSEUDOREYNOLDS curve, which adds drag in very thin air (×1.19 at ρu = 0.11 kg/m²/s, around 60 km on Kerbin at 3 km/s; ×1 by 45 km).

**Stacked parts hide each other.** Where two parts are joined on stack nodes (`attN` in a craft file or save), the faces they join on are covered: the smaller of the two areas comes off both. The face is the one the node points out of (`node_stack_…` in the part config). Radially attached parts aren't covered.

**Which way the craft points.** The nose is the command part's "up" (+Y); for a design, its root part's. *Engines first* flies along −nose, *nose first* along +nose, *side-on* along the broadest of 12 directions square to the nose. Parts that change shape use their folded-up cube (stowed panels, packed chutes). Wings' lift isn't counted. For a design, only the last stage counts: the parts no decoupler drops (sepI −1), and docking ports.

Worked example: a single 1 m block (every face 1 m², drag value 1), nose first at Mach 10. The curves give tip 4.000, tail 0.1451, surface 0.00259 and multiplier 0.85; Cd_curve(1) = 1, so the power doesn't matter:

1. Front: 1 × 4.000 = 4.000; back: 1 × 0.1451; four sides: 4 × 0.00259 = 0.0104
2. Sum 4.1555 × 0.85 = 3.532
3. × 0.1 × 8 = **2.83 m²**

Stack two such blocks end to end and the joined faces disappear: still one front and one back, now eight sides: 2.826 + 4 × 0.00259 × 0.85 × 0.8 = **2.833 m²**, almost no more. Side-on, the pair shows two fronts.

Not yet checked against the game. kRPC can ask KSP for the drag force on a craft (`SimulateAerodynamicForceAt`), which would test this.

- Code: `AeroDataReader.Read`, `AeroPhysics`, `CraftAero.Build`, `CraftAero.DragArea` (`Ksp/AeroData.cs`, `Ksp/CraftAero.cs`); tested in `AerobrakingTests.One_block_nose_first_at_mach_10`, `Stacked_blocks_hide_the_faces_they_touch`
- Source: KSP 1.12 `Physics.cfg` (its comments describe each curve) and `PartDatabase.cfg`
