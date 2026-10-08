# KSP Flight Planner — Math Guide

Every formula the app uses, where it lives in the code, where it comes from, and a worked example you can do yourself with a calculator. Examples use stock Kerbin unless noted.

> Tip: work in metres, seconds and m/s. Altitudes in KSP are measured from the surface, but almost every formula wants the radius r measured from the body's centre: r = R + altitude.

## 1. Constants and body data

| Symbol | Meaning | Value / source |
|---|---|---|
| G | Gravitational constant | 6.67408 × 10⁻¹¹ m³ kg⁻¹ s⁻² (value KSP uses) |
| g₀ | Standard gravity (used by Isp) | 9.80665 m/s² |
| μ | Gravitational parameter, μ = G·M | Kerbin 3.5316 × 10¹² m³/s², Kerbol 1.1723328 × 10¹⁸ |
| R | Equatorial radius | Kerbin 600,000 m |

KSP defines each body by its surface gravity and radius, so μ is derived from those:

```
μ = g · R²            (g in m/s², i.e. surface gravity in g × 9.80665)
M = μ / G
```

Worked example — Kerbin: g = 1.00034160493135 × 9.80665 = 9.81 m/s², so μ = 9.81 × 600,000² = 3.5316 × 10¹² m³/s².

- Code: `BodySeeder` (μ from the wiki data), `CelestialBody.SurfaceGravity` (g = μ/R²)
- Source: KSP wiki `<Body>/Param` pages; Eric Meyer's Resonant Orbit Calculator data (`systems.js`)

## 2. Circular orbits: speed and period

```
v = √(μ / r)
T = 2π · √(r³ / μ)
```

Worked example — 80 km Kerbin orbit, r = 680,000 m:

- v = √(3.5316e12 / 680,000) = √5,193,529 = **2,278.9 m/s**
- T = 2π · √(680,000³ / 3.5316e12) = 2π · 298.4 = **1,874.8 s (31 min 15 s)**

- Code: `OrbitMath.CircularVelocity`, `OrbitMath.Period`
- Source: Curtis, *Orbital Mechanics for Engineering Students*, ch. 2; [Circular orbit (Wikipedia)](https://en.wikipedia.org/wiki/Circular_orbit)

## 3. Vis-viva: speed anywhere on an orbit

```
v = √( μ · (2/r − 1/a) )        a = semi-major axis = (rₚ + rₐ) / 2
```

This is the workhorse: it gives your speed at radius r on any orbit with semi-major axis a. A circular orbit is the special case a = r.

Worked example — speed at periapsis of an 80 km × 500 km orbit: rₚ = 680,000, rₐ = 1,100,000, a = 890,000.
v = √(3.5316e12 × (2/680,000 − 1/890,000)) = **2,533.6 m/s**

- Code: `OrbitMath.VisViva`
- Source: [Vis-viva equation (Wikipedia)](https://en.wikipedia.org/wiki/Vis-viva_equation); Curtis ch. 2

## 4. Hohmann transfer: changing circular orbit altitude

The cheapest two-burn way between circular orbits of radius r₁ and r₂. Burn 1 raises (or lowers) the far side of your orbit to r₂; half an orbit later, burn 2 circularizes.

```
Δv₁ = √(μ/r₁) · ( √(2·r₂ / (r₁ + r₂)) − 1 )
Δv₂ = √(μ/r₂) · ( 1 − √(2·r₁ / (r₁ + r₂)) )
t   = π · √(a³ / μ)              a = (r₁ + r₂) / 2
```

Use absolute values when going down. Going down costs the same total as going up.

Worked example — space station at 150 km, starting from an 80 km orbit: r₁ = 680,000, r₂ = 750,000.

1. √(μ/r₁) = 2,278.9;  2r₂/(r₁+r₂) = 1,500,000 / 1,430,000 = 1.04895;  √ = 1.02418
2. Δv₁ = 2,278.9 × (1.02418 − 1) = **55.1 m/s**
3. √(μ/r₂) = 2,170.0;  2r₁/(r₁+r₂) = 0.95105;  √ = 0.97522
4. Δv₂ = 2,170.0 × (1 − 0.97522) = **53.8 m/s**
5. Total **108.9 m/s**, transfer time π·√(715,000³/μ) ≈ **17 min**

Same steps for a 500 km station: Δv₁ = 254.6, Δv₂ = 225.6, total **480.2 m/s** (≈ 23 min).

Sanity check against the community Δv map: 80 km → keostationary (2,863 km) by Hohmann is ≈ 1,099 m/s; the map lists 1,115 m/s. Map values are rounded and include a little slack, so expect differences of that size.

**Coming back down with aerobraking.** If you're heading for the surface of a body with an atmosphere, you only need the first burn of the downward transfer (the deorbit burn, which drops your periapsis); the atmosphere replaces the circularization. From 500 km that's the 225.6 m/s burn. The app charges this "deorbit burn" on aerobraked legs from custom orbits. Real deorbits usually aim periapsis at 30–45 km, a few m/s more than aiming at 80 km.

- Code: `OrbitMath.Hohmann`, `CustomLocations.CreateNode` (custom orbits = Hohmann from the body's map low orbit)
- Source: [Hohmann transfer orbit (Wikipedia)](https://en.wikipedia.org/wiki/Hohmann_transfer_orbit); Curtis §6.2

## 4b. Custom orbits around the Sun (Kerbol)

An orbit around the Sun is an interplanetary destination, so the Mission Builder attaches it where every other interplanetary trip starts: Kerbin's escape node (next to the Duna, Eve and Moho intercepts), not Kerbol's 610 km low orbit. A solar orbit only "belongs" to Duna if it is inside Duna's sphere of influence — then it's a Duna orbit (§4).

Steps — combine §4 (around the Sun), §7 (leaving Kerbin) and the map's 950 m/s low-Kerbin-orbit → escape edge:

```
r₁ = Kerbin's orbit radius,  r₂ = R_Kerbol + altitude,  a = (r₁ + r₂)/2      (around Kerbol, μ_sun)
v∞          = | √(μ_sun·(2/r₁ − 1/a)) − √(μ_sun/r₁) |       departure excess speed
ejection    = √(v∞² + 2μ_Kerbin/r_park) − √(μ_Kerbin/r_park)   from 80 km Kerbin orbit
circularize = | √(μ_sun/r₂) − √(μ_sun·(2/r₂ − 1/a)) |        no planet there, so no Oberth bonus
edge from Kerbin escape = (ejection − 950) + circularize
```

Worked example — "Eye of Kerbol", 10,000,000 km above Kerbol: r₁ = 13,599,840,256 m, r₂ = 261,600,000 + 10,000,000,000 = 10,261,600,000 m.

1. a = 11,930,720,128 m; Kerbin's speed √(μ_sun/r₁) = 9,284.5 m/s; transfer speed at r₁ = 8,610.6 m/s → v∞ = **673.9 m/s** (an inward transfer: you slow down relative to Kerbin)
2. Ejection from 80 km = √(673.9² + 2 × 3.5316e12/680,000) − 2,278.9 = **1,013.7 m/s** → beyond the map's 950: **64 m/s**
3. Circularize at r₂: √(μ_sun/r₂) − speed on the transfer at r₂ = **723.2 m/s**
4. Edge = 64 + 723 = **787 m/s**; flight time π√(a³/μ_sun) ≈ **175 Kerbin days**. Total from Kerbin's surface: 3,400 + 950 + 787 = 5,137 m/s.

Check: put the orbit at Duna's distance and step 2 gives 122 m/s — the map's own Duna intercept edge is 130 m/s.

Coming home, Kerbin's atmosphere can capture you (the map marks every interplanetary return as aerobrakeable), so only the 723 m/s burn to leave the custom orbit is charged when aerobraking.

- Code: `CustomLocations.Analyze` (star case), `OrbitMath.TransferToEllipse`, `OrbitMath.HyperbolicBurnDv`
- Source: Curtis §8.2–8.7 (patched conics); same method as the Transfer Planner's Hohmann estimate (§8)

## 4c. Elliptical custom orbits

A custom location stores only its **independent** values — periapsis altitude, apoapsis altitude and inclination. Everything else is derived, so the data can't contradict itself:

```
rₚ = R + periapsis altitude,  rₐ = R + apoapsis altitude
a  = (rₚ + rₐ) / 2                     semi-major axis
e  = (rₐ − rₚ) / (rₐ + rₚ)              eccentricity
T  = 2π · √(a³ / μ)                     period
vₚ = √(μ · (2/rₚ − 1/a)),  vₐ = √(μ · (2/rₐ − 1/a))     speeds at periapsis / apoapsis
```

Getting there from a circular orbit r₀ takes two burns. Burn 1 at r₀ puts the far side of your orbit at one apsis of the target (a transfer orbit with a₁ = (r₀ + r_meet)/2); burn 2 there sets the target's other apsis:

```
burn 1 = | √(μ·(2/r₀ − 1/a₁)) − √(μ/r₀) |
burn 2 = | √(μ·(2/r_meet − 1/a)) − √(μ·(2/r_meet − 1/a₁)) |
```

The app tries both orders (r_meet = rₐ, or r_meet = rₚ) and keeps the cheaper. When rₚ = rₐ this is exactly the Hohmann transfer of §4.

Worked example — a 100 km × 2,000 km orbit of Kerbin, from the 80 km low orbit (r₀ = 680,000, rₚ = 700,000, rₐ = 2,600,000):

1. a = 1,650,000 m, e = 0.5758, T = 7,086 s (1 h 58 m), vₚ = 2,819.6 m/s, vₐ = 759.1 m/s
2. Apoapsis first: a₁ = 1,640,000 → burn 1 = **590.5 m/s**, burn 2 (raise periapsis from 80 to 100 km at apoapsis) = **8.6 m/s** → **599 m/s**
3. Periapsis first: 16.5 + 589.8 = 606 m/s — so the app uses the apoapsis-first order

**Inclination** is charged as a plane change (§14) at the target's apoapsis, where you are slowest. For the orbit above at 10°: 2 × 759.1 × sin 5° = **132 m/s** — doing the same 10° in the 80 km orbit would cost 397 m/s. The Mission Builder adds this when "Include worst-case plane changes" is on.

**Validation** checks only the independent values: periapsis above the atmosphere (or the highest terrain on airless bodies), apoapsis inside the sphere of influence, apoapsis ≥ periapsis, inclination 0–180°.

- Code: `OrbitShape`, `OrbitMath.TransferToEllipse`, `OrbitMath.PlaneChangeDv`, `CustomLocations.Analyze`, `CustomLocations.Validate`
- Source: Curtis §6.2–6.4 (Hohmann and general coplanar transfers) and §6.10 (plane changes)

## 5. Synchronous (stationary) orbit

Choose a = r so the period equals the body's sidereal day T_day:

```
r = ∛( μ · T_day² / (4π²) )        altitude = r − R
```

Worked example — Kerbin, T_day = 21,549.425 s: r = ∛(3.5316e12 × 21,549.425² / 39.478) = 3,463,334 m → altitude **2,863,334 m**.

- Code: `OrbitMath.SemiMajorAxisFromPeriod`, `CelestialBody.SynchronousAltitudeM`
- Source: Eric Meyer's Resonant Orbit Calculator; [Geosynchronous orbit (Wikipedia)](https://en.wikipedia.org/wiki/Geosynchronous_orbit)

## 6. Resonant orbits for satellite constellations

To drop n satellites evenly into one circular orbit from a single carrier, the carrier's orbit must drift by 1/n of a revolution each pass:

```
Resonant (apoapsis raised):  T_carrier = T_sat · (n + 1) / n
Dive (periapsis lowered):    T_carrier = T_sat · (n − 1) / n
a_carrier = ∛( μ · T_carrier² / (4π²) )
other apsis radius = 2·a_carrier − r_sat
Injection Δv = | √(μ/r_sat) − √(μ · (2/r_sat − 1/a_carrier)) |
```

Minimum altitude where neighbours can see each other over the body (k = occlusion factor, 1 by default):

```
altitude_min = R·k / cos(π/n) − R
```

Worked example — 3 satellites at synchronous altitude above Kerbin: T_carrier = 21,549.4 × 4/3 = 28,732.6 s, apoapsis **4,327,727 m**, injection **84.57 m/s**. For 3 satellites, cos(60°) = ½, so altitude_min = R = **600 km**.

- Code: `ResonantOrbitCalculator.Calculate`, `OrbitMath.MinimumLineOfSightAltitude`
- Source: [Eric Meyer, Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) (MIT)

## 7. Escaping a body and arriving at another (hyperbolic burns)

Far from a planet you have a leftover speed v∞ (hyperbolic excess speed). Burning at periapsis of a low parking orbit (radius r) to reach it costs:

```
Δv = √( v∞² + 2μ/r ) − √( μ/r )
```

The same formula gives the capture burn at the destination (circularizing at radius r). Escape speed is the special case v∞ = 0 measured from rest: v_esc = √(2μ/r).

Worked example — leaving 80 km Kerbin orbit for Duna (v∞ = 918.3 m/s, see §8):
Δv = √(918.3² + 2 × 3.5316e12 / 680,000) − 2,278.9 = **1,072 m/s** (the Δv map says 950 + 130 = 1,080).

- Code: `TransferPlanner.HyperbolicBurnDv`
- Source: Curtis §8.6–8.7 (patched conics); [Hyperbolic trajectory (Wikipedia)](https://en.wikipedia.org/wiki/Hyperbolic_trajectory)

## 8. Interplanetary Hohmann: phase angle, timing, v∞

Treat both planets as circular orbits around the Sun (Kerbol), radii r₁ (origin) and r₂ (target):

```
a_t         = (r₁ + r₂) / 2
t_transfer  = π · √(a_t³ / μ_sun)
phase angle = 180° − 360° · t_transfer / T_target     (target ahead of you by this much at launch)
v∞ (depart) = | √(μ_sun·(2/r₁ − 1/a_t)) − √(μ_sun/r₁) |
synodic period (how often windows repeat) = 1 / |1/T₁ − 1/T₂|
```

Worked example — Kerbin → Duna: r₁ = 13,599,840,256 m, r₂ = 20,726,155,264 m.

1. a_t = 17,162,997,760 m → t_transfer = **6,524,003 s (302 Kerbin days)**
2. T_Duna = 17,315,400 s → phase angle = 180 − 360 × 6,524,003 / 17,315,400 = **44.36°**
3. Kerbin's speed 9,284.5 m/s, transfer speed 10,202.8 m/s → v∞ = **918.3 m/s**
4. T_Kerbin = 9,203,545 s → windows repeat every **19,645,697 s (2 years 57 days)**

- Code: `TransferPlanner.Hohmann`
- Source: Curtis §8.2–8.3; [Synodic period (Wikipedia)](https://en.wikipedia.org/wiki/Orbital_period#Synodic_period)

## 8b. Transfers to custom locations

A custom location can be an origin or destination in the Transfer Planner. It's treated like a tiny planet with **no gravity** (μ = 0), which changes two things:

```
arrival burn   = v∞ at arrival               (rendezvous: match its speed directly; √(v∞² + 2·0/r) − 0 = v∞)
departure burn = v∞ at departure             (no Oberth effect when leaving it either)
```

**Planned or placed.** A *planned* location doesn't exist yet, so wherever you arrive becomes its position — every departure date is equally good and the planner uses the ideal Hohmann transfer (§8) from your earliest date. A *placed* location exists in your save; once its position is known it moves on its orbit like a planet, so the planner searches launch windows with the porkchop method (§11).

**Entering a position.** Two ways:

- **Orbital elements** at a date — longitude of ascending node Ω, argument of periapsis ω and mean anomaly M (Kerbal Engineer and MechJeb display all three for a vessel). The app stores M at that date and propagates with Kepler's equation (§10).
- **Phase angle** at a date — "θ degrees ahead of Kerbin". For a circular orbit in Kerbin's plane (i = 0, Ω = 0, ω = 0) the mean anomaly equals the longitude, so `M(date) = λ_Kerbin(date) + θ`, where λ_Kerbin is Kerbin's longitude from its own orbital elements.

The gap to Kerbin then changes by `(n_location − n_Kerbin)` per second, where `n = 2π / T`.

Worked example — the 10,000,000 km "Eye of Kerbol" (r = 10,261,600,000 m), planned:

1. Hohmann from Kerbin: 175 Kerbin days; ejection from 80 km **1,013.7 m/s**, arrival (rendezvous) **723.2 m/s** — the same numbers as the Δv map edge in §4b
2. Its period is 6,032,218 s (279 days) against Kerbin's 9,203,545 s, so it pulls ahead of Kerbin by **0.444° per day**
3. If it were placed, the ideal phase angle at launch would be 180° − 360° × 175.1 / 279.3 = **−45.7°** (45.7° *behind* Kerbin — it's an inner orbit, so it catches up during the trip), and windows would repeat every **810 days**

- Code: `CustomLocationOrbits.ToElements`, `CustomLocationOrbits.ToBody`, `CustomLocationOrbits.PhaseAngleFromHome`, `TransferPlanner.PlanAnyTime`
- Source: Curtis §3.4 (mean anomaly), §8.2 (phase angles), §8.3 (synodic period)

## 8c. Importing vessels from a KSP save

KSP stores every vessel's orbit in the save (`saves/<name>/persistent.sfs`) as an `ORBIT` block with the same Keplerian elements a placed custom location uses — so importing is a unit conversion, not an orbit fit:

```
SMA  semi-major axis a (m, from the body's centre)     ECC  eccentricity e
INC  inclination (°)    LAN  Ω (°)    LPE  argument of periapsis ω (°)
MNA  mean anomaly M at the epoch (radians)    EPH  epoch (UT seconds)    IDENT / REF  the body orbited

periapsis altitude = a(1 − e) − R        apoapsis altitude = a(1 + e) − R        (R = body's equatorial radius)
mean anomaly (°)   = MNA × 180/π,  wrapped into 0–360°  (Ω and ω are wrapped the same way)
date of position   = EPH
```

From there the location moves exactly like the vessel: Kepler's equation (§10) from M at EPH. Only `ORBITING` vessels with e < 1 import; landed, sub-orbital and escaping vessels don't have an orbit to place. When KSP writes the save again, a linked location's orbit and position are replaced by the new values (name, description and images are kept).

Worked example — the relay "KerbLink Mk2 A" around Kerbin (R = 600,000 m):

1. SMA = 3,470,794.97 m, ECC = 0.0027130 → a·e = **9,416.3 m**
2. Periapsis = 3,470,794.97 − 9,416.3 − 600,000 = **2,861,378.7 m** (2,861.4 km); apoapsis = 3,470,794.97 + 9,416.3 − 600,000 = **2,880,211.2 m** (2,880.2 km)
3. MNA = 4.0125185 rad × 57.29578 = **229.900°** at EPH = 816,532.3 s (Year 1, Day 38)
4. INC 0.560°, LAN 281.711°, LPE 160.828° are copied as they are

- Code: `KspSaveReader.Parse`, `SaveImport.Convert`, `SaveImport.Refresh`; tested in `KspSaveTests`
- Source: KSP 1.12 save format (`ORBIT` node written by `OrbitSnapshot`); Curtis §4.4 (orbital elements)

## 9. Ejection angle: where in your parking orbit to burn

Your escape hyperbola bends by θ∞ between the burn (periapsis) and the direction you leave in:

```
e  = 1 + r · v∞² / μ             (eccentricity of the escape hyperbola)
θ∞ = arccos(−1/e)
```

Burn θ∞ before the direction you want to leave in: before **prograde** (planet's direction of travel) for outer planets, before **retrograde** for inner planets.

Worked example — Kerbin → Duna from 80 km: e = 1 + 680,000 × 918.3² / 3.5316e12 = 1.1624 → θ∞ = **149.4°**, so burn about 149° before Kerbin's prograde direction. (The porkchop optimum in the app uses the real, slightly-off-direction v∞, so it reports ~153°.)

- Code: `TransferPlanner.EjectionBurnFor`
- Source: Curtis §8.7; [Hyperbolic trajectory (Wikipedia)](https://en.wikipedia.org/wiki/Hyperbolic_trajectory)

## 10. Where planets are: Kepler's equation

Each planet has orbital elements (semi-major axis a, eccentricity e, inclination i, longitude of ascending node Ω, argument of periapsis ω, mean anomaly M₀ at time 0). To find it at time t:

```
n = √(μ / a³)                       mean motion
M = M₀ + n·t                        mean anomaly
M = E − e·sin E                     solve for E (Newton's method)
ν = 2·atan2( √(1+e)·sin(E/2), √(1−e)·cos(E/2) )     true anomaly
r = a·(1 − e·cos E)
```

Then rotate the in-plane position by ω, i and Ω to get 3D coordinates.

- Code: `Kepler.StateAt`, `Kepler.SolveEccentricAnomaly`
- Source: Curtis §3.4 and §4.6; [Kepler's equation (Wikipedia)](https://en.wikipedia.org/wiki/Kepler%27s_equation); orbital elements from the KSP wiki

## 11. Real launch windows: Lambert's problem and porkchop plots

The Hohmann numbers assume circular, flat orbits. For the real best window the app asks: *for this departure date and this flight time, what orbit connects where Kerbin is now to where Duna will be?* That is Lambert's problem. Solving it for thousands of (departure, flight time) pairs and colouring by total Δv gives a **porkchop plot**; the darkest spot is the best window.

- Method: universal-variable formulation with Stumpff functions C(z), S(z) (Curtis Algorithm 5.2), solved by bisection on z for robustness; single revolution, prograde
- Code: `Lambert.Solve`, `TransferPlanner.Plan`
- Sources: Curtis §5.3; [Lambert's problem (Wikipedia)](https://en.wikipedia.org/wiki/Lambert%27s_problem); [alexmoon's KSP Launch Window Planner](https://alexmoon.github.io/ksp/) for the approach in KSP
- Verified against Curtis Example 5.2 in the test suite

## 12. Sphere of influence

Inside a body's SOI, KSP only simulates that body's gravity (patched conics). The radius is the Laplace sphere:

```
r_SOI = a · (m / M)^(2/5)         a = body's orbit radius, m/M = mass ratio (μ/μ_parent works too)
```

Worked example — Mun: a = 12,000,000 m, μ ratio = 6.5138e10 / 3.5316e12 = 0.018444 → r_SOI = **2,429,559 m**.

- Code: values seeded from the Resonant Orbit Calculator data (they match this formula exactly)
- Source: [Sphere of influence (Wikipedia)](https://en.wikipedia.org/wiki/Sphere_of_influence_(astrodynamics)); KSP wiki `Template:Body data/SOI`

## 13. Atmosphere

KSP's real pressure curves are custom, but the classic exponential model with the published scale height H is a good approximation:

```
p(h) = p₀ · e^(−h / H)
```

Worked example — Kerbin at 10 km: p = 1 atm × e^(−10,000 / 5,600) = **0.168 atm**.

- Code: `Atmosphere.PressureAtmAt`
- Source: KSP wiki `<Body>/Param` (sea-level pressure, scale height); [Scale height (Wikipedia)](https://en.wikipedia.org/wiki/Scale_height)

## 14. Plane changes

Changing inclination by Δi at speed v costs:

```
Δv = 2 · v · sin(Δi / 2)
```

It's cheapest where you're slowest (high up, or at apoapsis). Worked example — matching Minmus's 6° inclination while still in 80 km Kerbin orbit: 2 × 2,278.9 × sin 3° = **238.5 m/s**. The Δv map lists a worst-case 340 m/s for Minmus; the Mission Builder's "Include worst-case plane changes" adds those map values.

- Code: map values only (`DvMapNode.PlaneChangeDv`)
- Source: Curtis §6.10; [Orbital inclination change (Wikipedia)](https://en.wikipedia.org/wiki/Orbital_inclination_change)

## 15. The Δv map and mission budgets

The Mission Builder adds up legs from the community Δv map. Each map edge is a published Δv; aerobraking replaces a burn where the map allows it; plane changes are optional worst-case extras. A phase's budget is:

```
phase Δv = Σ(legs in the phase) × (1 + margin%/100) + extra
```

Known simplifications:

- The map is a tree, so moon-to-moon trips go through low Kerbin orbit and are overstated.
- Custom orbits around a planet hang off its low orbit, so arriving from another planet charges "capture to low orbit, then climb". Capturing straight into a high orbit would be cheaper.
- Custom orbit inclination is charged as a worst-case plane change at apoapsis (§4c); the real cost depends on where your ascending node is.
- Atmospheric ascent values (3,400 for Kerbin) are typical, not exact — they depend on your rocket.
- Map surface ↔ low orbit values are optimistic; many players budget more (e.g. 700 instead of 580 for the Mun). Edit them in the Δv map tab.

- Code: `DeltaVMap.Route`, `MissionPlanner.Summarize`
- Source: [KSP Δv map 1.0.4 by /u/swashlebucky](https://www.reddit.com/r/KerbalSpaceProgram/) (based on Kowgan's map; CC BY-NC-SA 4.0)

## 16. Coming next: the rocket equation

When parts are added, each phase's actual Δv will come from the Tsiolkovsky rocket equation:

```
Δv  = Isp · g₀ · ln(m_full / m_empty)
TWR = thrust / (m · g)
```

Worked example — a Terrier (Isp 345 s vacuum) stage that is half fuel by mass: Δv = 345 × 9.80665 × ln 2 = **2,345 m/s**.

- Source: [Tsiolkovsky rocket equation (Wikipedia)](https://en.wikipedia.org/wiki/Tsiolkovsky_rocket_equation); part stats from the KSP wiki

## Sources

- Howard D. Curtis, *Orbital Mechanics for Engineering Students* (Elsevier) — the reference for most formulas here
- Bate, Mueller & White, *Fundamentals of Astrodynamics* (Dover) — alternative derivations
- [KSP wiki](https://wiki.kerbalspaceprogram.com) — body data (`<Body>/Param` pages), part data
- [Eric Meyer — Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) — body constants, resonant-orbit method (MIT License)
- KSP Δv map 1.0.4 by /u/swashlebucky, based on Kowgan's map (CC BY-NC-SA 4.0) — mission leg values
- [alexmoon — KSP Launch Window Planner](https://alexmoon.github.io/ksp/) — porkchop approach for KSP
- Wikipedia articles linked in each section
