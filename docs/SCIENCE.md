# Science gaps (plan)

> Status: stage 1 (the table) built 2026-11-15 on branch `science`, not yet tried in game. Stages 2–5 are still plans. The value formula is in [MATH.md §14.1](MATH.md#141-what-a-run-of-an-experiment-earns).

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
- **Transmitting** gives a fraction of the value (each experiment part's `xmitDataScalar`). Recovering gives all of it.

## The planner's rules

**What's left** in a subject = cap − sci − (what carried copies will earn). One run now earns base × sbv × (1 − earned ÷ cap), recovered; carried copies come home first ([MATH.md §14.1](MATH.md#141-what-a-run-of-an-experiment-earns)). KSP's `GetSubjectValue` was checked: it's exactly 1 − earned ÷ cap. The save's career science multiplier (`ScienceGainMultiplier`, 1 in Protogen) isn't applied.

**Experiments aboard** come from the vessel's parts (`ModuleScienceExperiment` and its `experimentID`), so modded experiments count if they use the stock module. Crew report needs crew. EVA report and surface sample need a kerbal outside and aren't part-based, so they get their own line: "EVA here pays N".

### 1. The Science pane (Tools › Science)

- **The game's data first.** The experiment list, which parts run which, and each body's multipliers and biomes come from the game once per save, through the add-on's `PlannerScience` service. The pane fetches them on its own the first time it sees the live link, or with *Fetch science data from KSP*. Kept in the database (`science_catalogs`), so after that it works without the game.
- **Pick a vessel, a body and an experiment.** The vessel list has every vessel in the save that carries an experiment, plus "Any experiment". Picking a vessel moves the body to where it is. The experiment box narrows the table to one experiment; the default is everything aboard.
- **The table**: one row per biome, then KSC's buildings (landed only, marked "(KSC)"), then *Anywhere* for experiments whose subject has no biome in that situation (the infrared telescope, or the goo in space). One column per situation the experiments can use at that body (no flying where there's no air, no splashed without sea). Each cell is what one run of each experiment would earn there, recovered; ✓ means under 1 point; hover for each experiment's share, what's left of its cap and copies already aboard.
- **Best places to scan**: the eight cells that pay most, with which experiments pay what.
- **The map** colours each biome by what's left, for one experiment or all of them, in the picked situation. It uses the same biome grid the ore survey already fetches.
- **Done** cells (under 1 point left) show greyed out, so what's left stands out.
- **Upcoming passes** for the picked vessel, worked out from its orbit, the body's rotation and the altitude lines. The ground track and biome lookup are the same ones ore coverage uses. For example:
  - "in 4 min: Highlands, space low, gravioli 38"
  - "in 19 min: Midlands, space high, gravioli 12" (an elliptical orbit crosses the space-high line, so that changes too)

  It covers the next few orbits. This is also how a probe in the background is planned for: the game can only run experiments on the vessel you're flying, so the pane tells you when it's worth switching to it.

### 2. "Scan now" in game

Part of the add-on (`mod/KspFlightPlanner.AddOns`, new `Science/` folder) and runs in game, so it works without the app open. It asks the game directly for the active vessel's biome and situation and the R&D records, which is more exact than polling over kRPC.

- When the active vessel enters a biome or situation where something aboard still pays at least the threshold, it puts up a screen message: "Over Minmus Lowlands (space low): Gravity scan 34, Thermometer 8". It shows once per entry, not every frame.
- Threshold: 1 point by default, settable in the add-on's window.
- It doesn't press any buttons. Running the experiment stays your call.

## Pieces (expected)

- **Core:** save reading for R&D subjects and carried `ScienceData`; `ScienceSubjectId` (parse experiment@Body+Situation+Biome; biome names have no spaces in the id); `ScienceGaps` (what's left per biome × situation for a set of experiments); upcoming passes, reusing the survey's ground-track sampling plus altitude-line crossings.
- **Add-on:** kRPC calls for experiment definitions (masks, base, cap, data scale, from the game, so modded experiments come through), body science multipliers and altitude lines; biome grid already exists. The in-game "Scan now" watcher.
- **Data:** experiment definitions and body multipliers per save, fetched once from the game like the ore grids. Subjects are read fresh from each save update, so a quickload just shows what the save says.
- **App:** `KspLink.Science.cs`, Science pane (view model + view), registered in Panes.

## Stages

1. **Gap table from the save.** Subjects + carried data + definitions → per-biome table for a body. Needs only the save and one fetch of definitions.
2. **Map.** Colour the biome grid by what's left.
3. **Upcoming passes** for a picked vessel.
4. **Scan now** in game.
5. **Later: crew and links.** A station's crew directing probes in the same SOI over a CommNet link (engineer → ore scanners, scientist → science alerts for the probe's experiments). This is the remote-probe idea, planned separately.

## Open questions

- KSC's mini-biomes (the buildings around the space centre) are biomes too. Show them, or leave them out of the map and list them separately?
- Science labs: data processed in a Mobile Processing Lab earns separately and doesn't use up the subject. Out of scope for the first cut?
- Asteroid and comet samples: per-object subjects, out of scope for now.
