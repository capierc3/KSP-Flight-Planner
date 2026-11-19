# Science gaps (plan)

> Status: stage 1 (the table, committed), stage 2 (the map), stage 3 (upcoming passes) and stage 4 ("Scan now" in game) built 2026-11-15 on branch `science`, not yet tried in game. Stage 5 (crew and links) is still a plan. The sums are under [How the numbers are worked out](#how-the-numbers-are-worked-out).

Running the gravioli detector over every biome just to find out whether it still pays is tedious. The planner already knows what the game knows: which experiment has earned what, where. This feature puts that on a map and tells you **when** the vessel you're flying (probe, station or ship) is over somewhere that still pays, so you press the button then and not at random.

## What the game does (KSP 1.12, checked against ScienceDefs.cfg and the Protogen save)

- **A subject** is one experiment in one situation, sometimes in one biome: `gravityScan@MinmusInSpaceLowLowlands`, `infraredTelescope@KerbinInSpaceHigh`. The save keeps one record per subject anyone has collected, under `SCENARIO { name = ResearchAndDevelopment › Science { id, sci, cap, sbv, scv, dsc } }`.
  - `sbv`: the body's multiplier for that situation (Kerbin space high 1.5, Minmus space low 4).
  - `cap`: the experiment's `scienceCap` × `sbv` (gravity scan 22 × 1.5 = 33).
  - `sci`: science earned so far. `scv`: what's left as a fraction, 1 − sci/cap (16.8 of 33 → 0.491).
  - A subject with no record has never been collected, so all of it is still there.
- **Situations** are bits: landed 1, splashed 2, flying low 4, flying high 8, space low 16, space high 32. Each experiment has a `situationMask` (where it works) and a `biomeMask` (where biome matters). The boundaries are per body: the atmosphere's flying-high line and the space-high line.
- **Stock experiments** (from `GameData/Squad/Resources/ScienceDefs.cfg`):

| Experiment | Works | Biome matters | Base / cap |
|---|---|---|---|
| Gravity scan (gravioli) | landed, splashed, space low, space high | all of them | 20 / 22 |
| Seismic scan | landed | yes | 20 / 22 |
| Temperature scan | everywhere | landed, splashed, flying low | 8 / 8 |
| Barometer | everywhere | landed, splashed | 12 / 12 |
| Mystery goo, materials bay | everywhere | landed, splashed | 10 / 13, 25 / 32 |
| Crew report | everywhere | landed, splashed, flying low | 5 / 5 |
| Atmosphere analysis | landed, flying low/high (needs air) | same | 20 / 24 |
| Infrared telescope | space high | no | 15 / 22 |
| Magnetometer | space low, space high | no | 45 / 45 |

  The gravioli detector is the one that pays most for checking biomes from orbit: biome matters even in space high.
- **Data carried but not yet home** isn't in the R&D records. It sits on vessels in the save as `ScienceData { subjectID, data, ... }` inside experiment parts and containers. A second copy of the same subject is worth less when it comes home, so the planner counts what's aboard as already taken.
- **Transmitting** gives a fraction of the value (each experiment part's `xmitDataScalar`; a carried copy keeps it as `xmit`), and only what it earned fills the subject, so sending the same subject again still pays something. Recovering gives all of it. A probe that never comes home only ever transmits.

## The planner's rules

**What's left** in a subject = cap − sci − (what carried copies will earn). One run now earns base × sbv × (1 − earned ÷ cap) recovered, or that × the part's transmit fraction sent home; carried copies are counted first, the same way ([below](#what-a-run-of-an-experiment-earns)). KSP's `GetSubjectValue` was checked: it's exactly 1 − earned ÷ cap. The save's career science multiplier (`ScienceGainMultiplier`, 1 in Protogen) isn't applied.

**Experiments aboard** come from the vessel's parts (`ModuleScienceExperiment` and its `experimentID`), so modded experiments count if they use the stock module. Crew report needs crew. EVA report and surface sample need a kerbal outside and aren't part-based, so they get their own line: "EVA here pays N".

### 1. The Science pane (Tools › Science)

- **The game's data first.** The experiment list, which parts run which (and each part's transmit fraction), and each body's multipliers, biomes and atmosphere come from the game once per save, through the add-on's `PlannerScience` service. The pane fetches them on its own the first time it sees the live link, or with *Fetch science data from KSP*. Kept in the database (`science_catalogs`), so after that it works without the game.
- **Count as: sent home, recovered, or both.** A probe that won't come back only sends, so its recovered value means nothing. Picking a vessel sets this: *Sent home* for an uncrewed one, *Both* (shown "recovered / sent", ranked and coloured by recovered) for a crewed one or "Any experiment". The transmit fraction is the best of the vessel's parts that run the experiment (the kerbal's for EVA experiments), or of every part in the game for "Any experiment". Everything below (table, best places, map, passes) follows it.
- **Pick a vessel, a body and an experiment.** The vessel list has every vessel in the save that carries an experiment, plus "Any experiment". Picking a vessel moves the body to where it is. The experiment box narrows the table to one experiment; the default is everything aboard.
- **The table**: one row per biome, then KSC's buildings (landed only, marked "(KSC)"), then *Anywhere* for experiments whose subject has no biome in that situation (the infrared telescope, or the goo in space). One column per situation the experiments can use at that body (no flying where there's no air, no splashed without sea). Each cell is what one run of each experiment would earn there, counted as picked; ✓ means under 1 point; hover for each experiment's recovered and sent values, what's left of its cap and copies already aboard.
- **Best places to scan**: the eight cells that pay most, with which experiments pay what.
- **The map** shows the body flattened (north up, 180° W on the left), each biome coloured by what one run pays there in the situation picked above the map. It follows the experiment box, so it's one experiment or all of them.
  - It starts on the situation whose biomes pay most in all.
  - Red pays most and blue least; grey is done (under 1 point), dark is where nothing can run.
  - Hover over it for the biome, the spot and what it pays.
  - The legend names the best biome and adds what the *Anywhere* row pays in that situation, since that isn't on the map.
  - KSC's buildings are too small to show.
  - Where the biomes are comes from the add-on's `BiomeGrid` on a 1° grid, once per body per save (`science_biome_maps`, kept with the body's rotation for stage 3). It's fetched on its own when the body is picked while connected, or with *Fetch biome map from KSP*.
- **Done** cells (under 1 point left) show greyed out, so what's left stands out.
- **Upcoming passes** for the picked vessel, worked out from its orbit, the body's rotation and the altitude lines ([below](#when-an-orbit-passes-over-somewhere-that-pays)). The ground track and biome lookup are the same ones ore coverage uses. For example:
  - "now, for 6m · 80 · Lowlands, space low · Gravity Scan 80"
  - "in 19m, for 12m · 50 · Midlands, space high · Gravity Scan 50" (an elliptical orbit crosses the space-high line, so that changes too)

  - It covers the next 3 orbits, from the vessel's orbit in the save (or the live game's clock when connected), and follows the experiment box.
  - Only stretches that pay at least 1 point and last at least 5 seconds are listed. Where no experiment's subject depends on the biome (the magnetometer), a whole situation is one stretch.
  - It needs the body's biome map, and the vessel in a closed orbit around the picked body. It stops where the orbit meets the ground.
  - This is also how a probe in the background is planned for: the game can only run experiments on the vessel you're flying, so the pane tells you when it's worth switching to it.

### 2. "Scan now" in game

Part of the add-on (`mod/KspFlightPlanner.AddOns`, new `Science/` folder) and runs in game, so it works without the app open. It asks the game directly for the active vessel's biome and situation and the R&D records, which is more exact than polling over kRPC.

- When the active vessel enters a biome or situation where something aboard still pays at least the threshold, it puts up a screen message. An uncrewed vessel is judged by what sending the result home earns ("Scan now: over Minmus Lowlands (space low): Gravity Scan 32 sent"); a crewed one by the recovered value, with the sent value after it ("Gravity Scan 80 (sent 32)"). It shows once per entry (and on switching vessels), not every frame. It looks twice a second.
- What counts: each working experiment part aboard (`ModuleScienceExperiment`, not inoperable), the crew report only with crew, and with crew aboard the EVA report and surface sample, marked "(EVA)". Copies of the same subject already in the vessel's parts count as taken, as in the pane. KSC's buildings count as their own biomes when landed on them, the way the game names subjects.
- Settings, per save, in KSP's difficulty settings under *KSP Flight Planner › Science*: *Scan now alerts* (on) and *Alert when a run pays at least* (1 point). Career and science saves only; sandbox keeps no science.
- It doesn't press any buttons. Running the experiment stays your call.
- Code: `mod/KspFlightPlanner.AddOns/Science/ScanNow.cs`, `ScienceSettings.cs`.

## How the numbers are worked out

### What a run of an experiment earns

A subject is one experiment in one situation at one body, sometimes in one biome. Its cap is the experiment's science cap times the body's multiplier for that situation, and each result earns less as the subject fills up:

```
cap      = scienceCap × m                        (m: the body's multiplier for the situation, the save's sbv)
value    = data ÷ dataScale × m × (1 − earned ÷ cap)
one run  = baseValue × dataScale of data, so it earns  baseValue × m × (1 − earned ÷ cap)
sent     = value × x                             (x: the part's transmit fraction, xmitDataScalar)
```

`1 − earned ÷ cap` is the save's `scv`. KSP's `ResearchAndDevelopment.GetSubjectValue` is exactly this, with no floor, so the planner never lets a value go below 0. Data a vessel is still carrying hasn't been counted in `earned` yet. The planner brings each carried copy home first, in turn, and works out the next run from what's left after them. Sent home, a result earns its value × x, and only that much is added to `earned`, so the subject fills more slowly. For the sent figure the carried copies are sent too, each with its own x (the save's `xmit`).

Worked example (gravity scan: baseValue 20, scienceCap 22, dataScale 3):

1. Kerbin, space high over Grasslands: m = 1.5, so cap = 22 × 1.5 = **33**. The save has earned 16.8, so the next run earns 20 × 1.5 × (1 − 16.8 ÷ 33) = 30 × 0.491 = **14.73**, and 33 − 16.8 = 16.2 is left
2. Kerbin, space high over Highlands, never scanned: the first run earns 20 × 1.5 = **30** of 33
3. Minmus, space low over Lowlands (m = 4, cap 88), nothing earned yet, but a probe carries one run (60 data). That copy will earn 60 ÷ 3 × 4 × 1 = 80, leaving 8. Another run would earn 20 × 4 × (1 − 80 ÷ 88) = **7.27**
4. The same, but the probe only sends (the gravioli detector's x = 0.4): the copy earns 80 × 0.4 = 32, leaving 56. Another run sent earns 20 × 4 × (1 − 32 ÷ 88) × 0.4 = 80 × 0.636 × 0.4 = **20.4** (`Sending_home_earns_the_parts_share_and_fills_the_subject_only_that_much`)

- Code: `ScienceGaps.Entry`, `.Value`, `.TransmitFractions` (src/KspFlightPlanner.Core/Ksp/Science.cs); in game, `ScanNow.WhatPays` (mod/KspFlightPlanner.AddOns/Science/ScanNow.cs)
- Source: KSP 1.12's `ResearchAndDevelopment.GetSubjectValue` and the save's `Science { sci, cap, sbv, scv }` records (scv = 1 − sci ÷ cap in every record); experiment values from `GameData/Squad/Resources/ScienceDefs.cfg`

### When an orbit passes over somewhere that pays

The vessel's place over the ground is the same ground track as a survey pass ([SURVEY.md “What a pass maps”](SURVEY.md#what-a-pass-maps)): position r(t) from its orbit, latitude φ = asin(r_z / |r|), longitude λ = atan2(r_y, r_x) − θ₀ − ω·t. The biome is the biome map's nearest grid point to (φ, λ). The situation comes from the altitude h = |r| − R, the way KSP works it out for a craft in flight:

```
h < atmosphere height       →  flying: low below the flying-high line, high above it
otherwise                   →  space:  low below the space-high line, high above it
```

At each step the pass's value is the biome's cell plus the "Anywhere" cell for that situation (above). Only a biome where some experiment's subject depends on it starts a new stretch; where none does, the whole situation is one stretch. A stretch is shown when it pays at least 1 point and lasts at least 5 s (shorter ones are biome edges flickering on the 1° grid). The walk covers the next 3 orbits, in steps of half a grid degree over the ground at the current height, Δt = 0.5° / (h_orbit / |r|² + ω) with h_orbit = √(μ a (1 − e²)), and never longer than T / 720 so the altitude lines aren't stepped over. It stops where the orbit meets the ground.

Worked example — Minmus (μ = 1.7658 × 10⁹ m³/s², R = 60 km, space-high line 30 km, multipliers space low 4 and space high 2.5), gravity scan with nothing earned, a map split into a northern and a southern biome:
1. **20 km circular polar**: a = 80 km, T = 2π√(a³/μ) = 2π × 538.5 = **3,383 s**. The vessel is always space low, so it's 20 × 4 = **80** in each biome, alternating every half orbit (**1,692 s**) as it crosses the equator (`A_polar_orbit_alternates_hemispheres_in_space_low`)
2. **10 × 100 km**: a = 115 km, e = 45 / 115 = 0.391, T = **5,831 s**. It's below 30 km within true anomaly ±acos((a(1 − e²)/(R + 30 km) − 1)/e) = ±77.9° of periapsis, which by Kepler's equation is **1,218 s** of each orbit: space low there (80 a biome), space high (20 × 2.5 = **50**) the rest (`An_elliptical_orbit_crosses_the_space_high_line`)

- Code: `SciencePasses.Upcoming`, `ScienceBody.InFlightAt` (src/KspFlightPlanner.Core/Ksp/Science.cs). Tested in `SciencePassTests`
- Source: [SURVEY.md “What a pass maps”](SURVEY.md#what-a-pass-maps); KSP's `ScienceUtil.GetExperimentSituation` (flying below the atmosphere's edge, `flyingAltitudeThreshold`, `spaceAltitudeThreshold`)

## Pieces (expected)

- **Core:** save reading for R&D subjects and carried `ScienceData`; `ScienceSubjectId` (parse experiment@Body+Situation+Biome; biome names have no spaces in the id); `ScienceGaps` (what's left per biome × situation for a set of experiments); upcoming passes, reusing the survey's ground-track sampling plus altitude-line crossings.
- **Add-on:** kRPC calls for experiment definitions (masks, base, cap, data scale, from the game, so modded experiments come through), body science multipliers, altitude lines and where the atmosphere ends; biome grid already exists. The in-game "Scan now" watcher.
- **Data:** experiment definitions and body multipliers per save, fetched once from the game like the ore grids. Subjects are read fresh from each save update, so a quickload just shows what the save says.
- **App:** `KspLink.Science.cs`, Science pane (view model + view), registered in Panes.

## Stages

1. **Gap table from the save.** Subjects + carried data + definitions → per-biome table for a body. Needs only the save and one fetch of definitions. Built.
2. **Map.** Colour the biome grid by what's left. Built.
3. **Upcoming passes** for a picked vessel. Built.
4. **Scan now** in game. Built.
5. **Crew and links** (built 2026-11-15, not tried in game). A Science Officer on duty at a station, or at KSC for Kerbin's SOI, runs and sends the experiments of linked probes you aren't flying: same SOI and a CommNet path. The rules are in [KERBIN-RESOURCES.md “Survey and science roles”](KERBIN-RESOURCES.md#survey-and-science-roles).

## Open questions

- KSC's mini-biomes (the buildings around the space centre) are biomes too. Show them, or leave them out of the map and list them separately?
- Science labs: data processed in a Mobile Processing Lab earns separately and doesn't use up the subject. Out of scope for the first cut?
- Asteroid and comet samples: per-object subjects, out of scope for now.
