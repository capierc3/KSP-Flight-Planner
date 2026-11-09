# Ore survey

A station with an engineer aboard surveys the body it orbits, the way a company's crew would: every pass over the ground fills in more of the map, and finds arrive as reports at the game time the station flew over them. It only ever shows what the game itself would show for what you've done: rough words, then the rounded estimate the narrow-band scanner gives, then true values once you've landed and analysed a biome.

The ore map comes from the game through the planner's kRPC add-on (`mod/KspFlightPlanner.AddOns`, "PlannerSurvey"). Everything else (crew, parts, orbits, what's been surveyed) comes from the save, so coverage is worked out from orbits and the game clock, including time warped through or spent flying something else.

## What the game does (KSP 1.12, read from its code)

- **Ore abundance** at a spot is `ResourceMap.GetAbundance`. A drill asks at its own altitude with the lock check off, so that's the true value. Altitude doesn't change Minmus ore.
- **Unsurveyed biomes.** Until a biome is analysed, the narrow-band scanner and KerbNet show one flat value for the whole biome, with no local variation, divided by 1.5. That's the add-on's *estimate* (the lock-free value with `ExcludeVariance`).
- **Orbital survey** (M700, `ModuleOrbitalSurveyor` › *Perform orbital survey*): needs to be ORBITING, inclination ≥ 80°, periapsis above max(R / 10, 25 km), apoapsis below min(5R, 1,500 km), and an antenna that can send the data. It marks the body scanned (save: `ResourceScenario › PLANET_SCAN_DATA { PlanetId }`), which turns on the ore overlay and KerbNet's ore view.
- **Biome analysis** (Surface Scanning Module, `ModuleBiomeScanner` › *Perform biome analysis*, landed): unlocks the biome the craft stands in (save: `BIOME_LOCK_DATA { PlanetId, BiomeName }`). After that the scanners show true values across that biome, also from orbit.
- **KerbNet** sees 5°–90° of the surface in orbit (narrow-band part), 160°–179.5° landed.

## The planner's rules

**A surveying station** orbits a body and has an **Engineer** aboard (crew trait from the save's roster). Coverage only builds while that's true. If the engineer leaves part way, the strips they finished stay, and the rest stays blank until an engineer is back.

**Coverage.** Each pass maps a strip **±5°** either side of the ground track (KerbNet's narrowest view). It's worked out from the station's orbit and the body's rotation between two readings of the save that both show the station surveying. Readings that disagree (the engineer came or went, the station changed body) credit nothing in between, since we don't know when it happened. If the station's orbit changed between them, the first orbit covers the first half and the second the rest. If the game clock goes back (a quickload or revert), coverage and reports after that time are taken away.

**Three levels per biome**, each needing more:

| Level | Needs | Report |
|---|---|---|
| Rough | Survey scanner aboard, the body's orbital survey done in game, and ≥ 50% of the biome covered by it | Words: Trace (< 1%), Poor (1–3%), Fair (3–6%), Good (6–9%), Rich (≥ 9%), from the estimate |
| Estimate | Narrow-band scanner aboard and below its 500 km reach, ≥ 50% of the biome covered by it | The estimate rounded to a whole percent: "≈ 7%" |
| Surveyed | The save shows a biome analysis for it | True average over the biome, and its best spots among the cells the narrow-band has covered (up to three, at least 3° apart), each one a place you can save and land at |

A scanner is a part whose modules include `ModuleOrbitalSurveyor` (survey scanner), or `ModuleResourceScanner` together with `ModuleKerbNetAccess` (narrow-band; the Surface Scanning Module has no KerbNet, so it doesn't count).

**Reports** arrive at the game time the biome crossed its threshold, newest first, with who was aboard: "Bill Kerman mapped Greater Flats with the narrow-band: ore ≈ 7%". A biome analysis upgrades it: "Greater Flats analysed: 10.5% at best, at 24.0° S 23.1° E". While connected, new reports also show on the game's screen: up to three one by one, more as one message (a long warp, or the first map).

**Spots to try.** The engineer's top five, best first:
- an analysed biome's richest spots (true values, among points the narrow-band has covered, at least 3° apart);
- for each biome the narrow-band has reported on but nobody has analysed, the spot deepest inside it (fewest grid steps from another biome, weighted by cos latitude to keep away from the poles), ranked by the biome's estimate. Before analysis the game itself has one flat value per biome, so this is where to land and run the analysis, not a guess at the richest point.

They're named "Ore 1: Greater Flats ≈ 7%" and so on. They show in Land at a spot ("Ore survey"), and *Send to KSP as waypoints* puts them on the game's map (sending again replaces the survey's earlier waypoints on that body).

**Run orbital survey.** When the body hasn't been surveyed and the picked craft has a survey scanner, the pane offers *Run orbital survey*. The game needs no crew for it, so a probe will do (any orbiting craft with a scanner is in the station list). Before pressing the part's button over kRPC it checks, and lists everything that's missing: connected to KSP, the craft is the one you're flying, a survey scanner aboard, inclination ≥ 80°, periapsis and apoapsis inside the band (with the numbers for that body). The game then sends the data; the save shows the body scanned the next time KSP writes it.

## Pieces

- Add-on: `Abundance`, `AbundanceGrid` (true, at ground height), `EstimateGrid`, `BiomeNames`, `BiomeGrid`, `IsBodyScanned`, `IsBiomeAnalysed`.
- Core: save reading (roster traits, part module names, scanned bodies, analysed biomes); `SurveyCoverage` (ground-track strips into a first-covered-time grid at 1°); `SurveyStation` (does a vessel survey, which levels); `OrbitalSurveyCheck`; `SurveyReport` (per-biome levels and reports).
- Data: the body's ore and biome grids per save (fetched once from the game), coverage per save, body and level, the last reading of each surveying station, and the reports.
- App: `SurveyTracker` (reads every save update, credits coverage, raises reports) and the Survey pane (Tools › Ore survey).
- Math guide: the coverage and abundance rules, with a worked example.
