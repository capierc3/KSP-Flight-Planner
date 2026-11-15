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

**A surveying station** orbits a body and has an **Engineer** aboard (crew trait from the save's roster). Coverage only builds while that's true. *Agreed 2026-11-15, not built:* the engineer must be on duty as the station's Survey Engineer, and they also work the scanners of linked probes in the same SOI (or, from KSC, around Kerbin). See [KERBIN-RESOURCES.md “Survey and science roles”](KERBIN-RESOURCES.md#survey-and-science-roles). If the engineer leaves part way, the strips they finished stay, and the rest stays blank until an engineer is back.

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

### When a body is done (agreed 2026-11-15, not built)

Today nothing happens when a body is finished: the stations keep surveying ground they've already mapped. The game itself has no "map complete" moment either. Its M700 orbital survey is one button press, done at once ("Orbital survey complete"), and that is the only notice it gives.

**Done** means the narrow-band has covered **95% of the body's surface** (by area, so the crowded grid points near the poles don't count extra), and every biome has its narrow-band report. Mapping the last few percent can take weeks when the tracks nearly repeat, which is why it stops at 95%. The figure is a setting.

**1. Writing the report.** When a body is done, the engineers who were on duty surveying it start writing the survey report.

- Their job title becomes **Writing the Minmus survey report**.
- It takes **one Kerbin day (6 hours)** of game time while at least one of them is on duty in the lab. Coming off duty pauses it, and going back on picks it up again. The length is a setting.
- The planner works out when the body is done, but the add-on gives job titles, so the planner tells the add-on over the live link: the body, and who's writing. If the game isn't connected at that moment, it tells it the next time it is. The add-on keeps it in the save.

**2. The final report.** When the writing is done, the report is filed:

- In the Ore survey pane, and as a screen message in game: "Bill Rockmore filed the Minmus survey report".
- **Ore:** each biome's figure (rough words, the estimate, or true values where analysed) and the **top five mining spots** (the ones in *Spots to try*), with *Send to KSP as waypoints*.
- **Places of interest:** every landmark the survey found on the body (see below), each one a place you can save and land at.
- The body's survey goes into each writer's survey log in Kerbin Resources.

**3. After the report.** The body needs no more surveying.

- A station there with nothing else to survey gives its engineers no job, so they show as **Awaiting orders**.
- Its scanners no longer give anyone the Survey Engineer title for that body, and neither do its linked probes.
- A station at another body that isn't done yet still has work.

**Landmarks are found by the survey, then identified by visiting** (agreed 2026-11-15).

- **The list comes from the game.** Every anomaly is a surface object on its body. The add-on reads each body's list once per save, the same way as the ore and biome maps: name, position, and the game's own display name. Mods' anomalies come too. The wiki's 28 anomalies are only a partial list, so they're replaced. Its natural features (the Mohole, each body's highest point) and launch sites stay as they are.
- **Launch sites** are always shown. They're the company's own.
- **Three stages for an anomaly**, much like the game's KerbNet, which shows them as "Unknown Object":
  1. **Hidden** until a narrow-band strip covers its spot.
  2. **Spotted:** "Unknown object on Minmus at 23.8° N 61.0° E". It gets its own report at once ("Bill Rockmore spotted something odd on Minmus"), and you can save it or land there, but what it is stays unknown.
  3. **Identified** once a craft visits it: landed, or flying, within **3 km** (a setting). Then the real name and the wiki's note show, if there is one.
- **Natural features** (the Mohole, a highest point) aren't a mystery, so they show by name as soon as they're spotted.
- **Where it applies:** everywhere landmarks are listed, the body's facts in Locations and Land at a spot.
- **Kerbin:** its anomalies are hidden too, until something surveys Kerbin.
- **Final report:** lists every spotted place by its stage ("Unknown object", or its name once identified).
- **Setting:** *Hide landmarks until surveyed* (on) shows them all, named, for players who'd rather not hunt.
- **How a visit is seen:** the add-on checks every 10 seconds whether any craft is within range of a spotted anomaly, loaded or not, and records it in the save. The planner reads that.

**Run orbital survey.** When the body hasn't been surveyed and the picked craft has a survey scanner, the pane offers *Run orbital survey*. The game needs no crew for it, so a probe will do (any orbiting craft with a scanner is in the station list). Before pressing the part's button over kRPC it checks, and lists everything that's missing: connected to KSP, the craft is the one you're flying, a survey scanner aboard, inclination ≥ 80°, periapsis and apoapsis inside the band (with the numbers for that body). The game then sends the data; the save shows the body scanned the next time KSP writes it.

## How the numbers are worked out

The sums behind the survey: where the ore is, and what a pass maps. They were in MATH.md until 2026-11-15; MATH.md keeps only flight-planning maths.

### Ore abundance, and what the scanners show


KSP generates ore from the save's seed. The planner's kRPC add-on asks the game itself (`ResourceMap.GetAbundance`), so the numbers are the game's, not a copy of its generator. Read from KSP 1.12's code:

```
true abundance  A(φ, λ, h)  = what a drill at (φ, λ) mines: asked at its own height h, lock check off
before analysis A_est(biome) = B(biome) / 1.5     B = the biome's base value (no local variation), the same everywhere in it
```

A biome counts as analysed once a Surface Scanning Module has run *Perform biome analysis* landed in it (the save's `BIOME_LOCK_DATA`). Until then the narrow-band scanner, KerbNet and the ore overlay all show A_est. Height only matters for resources set up to fade with altitude; Minmus ore doesn't.

Worked example — Min Station over Minmus, Greater Flats not yet analysed, 29.3 km up (in game, 2026-11-04):
- KerbNet showed **7.03%** at 23°59′ S 23°08′ E (and the same at another spot, in another biome, with the same base).
- The add-on gave **10.51%** there, at ground height and at 29.3 km alike.
- 7.03% × 1.5 = **10.55%**: the base. The 10.51% is the base plus the spot's own small variation.

- Code: `mod/KspFlightPlanner.AddOns/Surveys/SurveyService.cs` (`Abundance`, `AbundanceGrid`, `EstimateGrid`), `KspLink.FetchSurveyMapAsync`, `SurveyMap`
- Source: KSP 1.12 `ResourceMap.GetAbundance`, `ModuleResourceScanner`, `ModuleResourceHarvester`, `KerbNetModeResource` (decompiled)

### What a pass maps


The station's ground track comes from its orbit ([MATH.md §1.4](MATH.md#14-where-things-are-at-a-given-time-keplers-equation)) and the body's rotation ([MATH.md §2.9](MATH.md#29-landing-at-a-spot-from-where-you-are)'s θ₀ and ω = 2π / T_rot):

```
r(t)  = the station's position at time t (Kepler, [MATH.md §1.4](MATH.md#14-where-things-are-at-a-given-time-keplers-equation))
φ(t)  = asin(r_z / |r|)
λ(t)  = atan2(r_y, r_x) − θ₀ − ω·t                  wrapped to −180°…180°
```

A grid point (φ_g, λ_g) is mapped at the first t its great-circle distance from (φ(t), λ(t)) is within ρ = 5° (KerbNet's narrowest view):

```
cos d = sin φ · sin φ_g + cos φ · cos φ_g · cos(λ − λ_g)  ≥ cos ρ
```

The track is followed in steps of half a grid degree over the ground (Δt = 0.5° / (v_p / r_p + ω), the fastest the track moves, at periapsis). Each row only checks the longitudes inside the strip, |λ − λ_g| ≤ acos((cos ρ − sin φ sin φ_g) / (cos φ cos φ_g)), or the whole row when the strip takes in a pole. The narrow-band only maps while |r| − R ≤ 500 km. The walk stops early once every point is mapped, or after 20 turns of the body (and 20 orbits) with nothing new, when the pattern only repeats.

Between two readings of a station the time is credited only when both show an engineer and the same body; a level needs its scanner in both (and, for rough, the body's orbital survey done). If the orbit changed between them, the first orbit flies the first half of the time and the second the rest.

A biome reports at a level once half its points are mapped at that level; the report's time is when the point that made it half was mapped.

What turns the ground under the orbit is the body's own spin (T_rot is measured against the stars). The orbit's plane stays fixed against the stars too, so the body's trip around its planet doesn't move the track.

Worked example — Minmus (μ = 1.7658 × 10⁹ m³/s², R = 60 km, T_rot = 40,400 s ≈ 1.9 Kerbin days), polar orbits, time to map all of it (simulated with `SurveyCoverage.Follow`, Kerbin days of 6 h):
- **50 km circular**: a = 110 km, T = 2π√(a³/μ) = 2π × 868.2 = **5,455 s**. Each orbit the track lands 360° × 5,455 / 40,400 = **48.6°** further west: 90% in 4.7 days, all of it in **8.3 days** (`A_polar_orbit_maps_everything_within_a_few_weeks`).
- **30 km circular**, the bad case: T = 2π × 642.5 = **4,037 s**, **36.0°** a pass. Ten orbits are 40,370 s, within 30 s of one Minmus day, so the tracks nearly repeat and the last gaps close slowly: 90% in 46 days, all in **92 days**.
- **28.8 × 30.0 km** (Min Station, 2026-11-04): T = 3,998 s, 35.6° a pass, just far enough from ten orbits a day: all of it in **8.4 days**.

- Code: `SurveyCoverage.Follow`, `.Credit`, `.Mark`; `SurveyReports.Build`; `SurveyTracker`. Tested in `SurveyTests`
- Source: [MATH.md §1.4](MATH.md#14-where-things-are-at-a-given-time-keplers-equation), [MATH.md §2.9](MATH.md#29-landing-at-a-spot-from-where-you-are); great-circle distance: [MATH.md §1.9](MATH.md#19-surface-positions-latitude-longitude-and-distance)

## Pieces

- Add-on: `Abundance`, `AbundanceGrid` (true, at ground height), `EstimateGrid`, `BiomeNames`, `BiomeGrid`, `IsBodyScanned`, `IsBiomeAnalysed`.
- Core: save reading (roster traits, part module names, scanned bodies, analysed biomes); `SurveyCoverage` (ground-track strips into a first-covered-time grid at 1°); `SurveyStation` (does a vessel survey, which levels); `OrbitalSurveyCheck`; `SurveyReport` (per-biome levels and reports).
- Data: the body's ore and biome grids per save (fetched once from the game), coverage per save, body and level, the last reading of each surveying station, and the reports.
- App: `SurveyTracker` (reads every save update, credits coverage, raises reports) and the Survey pane (Tools › Ore survey).
- Math guide: the coverage and abundance rules, with a worked example.
