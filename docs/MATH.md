# KSP Flight Planner — Math Guide

Every formula the app uses, where it lives in the code, where it comes from, and a worked example you can do yourself with a calculator. Examples use stock Kerbin unless noted.

> Tip: work in metres, seconds and m/s. Altitudes in KSP are measured from the surface, but almost every formula wants the radius r measured from the body's centre: r = R + altitude.

Each section has the formulas, a worked example, the code that implements them and a source. References like §2.1 point to sections of this guide; book references are written without § (Curtis 6.2 is section 6.2 of Curtis).

## Contents

- **1. Foundations** — 1.1 Constants and body data; 1.2 Circular orbits: speed and period; 1.3 Vis-viva: speed anywhere on an orbit; 1.4 Where things are at a given time: Kepler's equation; 1.5 Orbital elements from position and velocity; 1.6 Sphere of influence; 1.7 Atmosphere; 1.8 Maneuver-node axes
- **2. Changing your orbit around one body** — 2.1 Hohmann transfer: changing circular orbit altitude; 2.2 Elliptical orbits; 2.3 Plane changes; 2.4 Synchronous (stationary) orbit; 2.5 Deorbiting
- **3. Going to another planet** — 3.1 Escaping a body and arriving at another (hyperbolic burns); 3.2 Interplanetary Hohmann: phase angle, timing, v∞; 3.3 Ejection angle: where in your parking orbit to burn; 3.4 Real launch windows: Lambert's problem and porkchop plots; 3.5 Orbits around the Sun (Kerbol); 3.6 Transfers to custom locations
- **4. Meeting a craft in orbit (rendezvous)** — 4.1 How a rendezvous is planned; 4.2 Matching planes; 4.3 Phase angle, waiting, and phasing orbits; 4.4 The intercept: Lambert's problem again; 4.5 When and where to launch; 4.6 Starting from a craft around another body
- **5. Deploying a satellite constellation** — 5.1 Resonant orbits for satellite constellations; 5.2 Burn times and node values
- **6. Budgeting a mission** — 6.1 The Δv map and mission budgets; 6.2 Leg tools: replacing map values with a planned leg; 6.3 Transfer legs: a chosen launch window; 6.4 Deploy steps: a constellation released at a stop; 6.5 Rendezvous legs: meeting a placed craft
- **7. Your rocket: Δv and TWR** — 7.1 A vessel's remaining Δv (rocket equation); 7.2 A saved design's stage-by-stage Δv; 7.3 Does a design have enough Δv for the mission?; 7.4 TWR at every landing and lift-off
- **8. Working with your save and the live game** — 8.1 Importing vessels from a KSP save; 8.2 Following the live game (kRPC); 8.3 Flying a plan: how far off it you are; 8.4 Re-planning a rendezvous

**Where to look for…**

- Raising, lowering or reshaping an orbit: §2.1, §2.2
- A plane change or an inclined target: §2.3, §4.2
- A trip to another planet, and its launch window: §3.2, §3.4
- A rendezvous or docking: §4.1
- A relay network / constellation: §5.1, §5.2
- Why the Mission Builder shows a given Δv: §6.1, §6.2
- Whether your rocket is good enough: §7.3, §7.4
- A vessel imported from your save, or the live game: §8.1, §8.2

## 1. Foundations

The building blocks every other chapter uses: body constants, orbital speed, where a craft is at a given time, and the axes of a maneuver node.

### 1.1 Constants and body data

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

### 1.2 Circular orbits: speed and period

```
v = √(μ / r)
T = 2π · √(r³ / μ)
```

Worked example — 80 km Kerbin orbit, r = 680,000 m:

- v = √(3.5316e12 / 680,000) = √5,193,529 = **2,278.9 m/s**
- T = 2π · √(680,000³ / 3.5316e12) = 2π · 298.4 = **1,874.8 s (31 min 15 s)**

- Code: `OrbitMath.CircularVelocity`, `OrbitMath.Period`
- Source: Curtis, *Orbital Mechanics for Engineering Students*, ch. 2; [Circular orbit (Wikipedia)](https://en.wikipedia.org/wiki/Circular_orbit)

### 1.3 Vis-viva: speed anywhere on an orbit

```
v = √( μ · (2/r − 1/a) )        a = semi-major axis = (rₚ + rₐ) / 2
```

This is the workhorse: it gives your speed at radius r on any orbit with semi-major axis a. A circular orbit is the special case a = r.

Worked example — speed at periapsis of an 80 km × 500 km orbit: rₚ = 680,000, rₐ = 1,100,000, a = 890,000.
v = √(3.5316e12 × (2/680,000 − 1/890,000)) = **2,533.6 m/s**

- Code: `OrbitMath.VisViva`
- Source: [Vis-viva equation (Wikipedia)](https://en.wikipedia.org/wiki/Vis-viva_equation); Curtis ch. 2

### 1.4 Where things are at a given time: Kepler's equation

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
- Source: Curtis 3.4 and 4.6; [Kepler's equation (Wikipedia)](https://en.wikipedia.org/wiki/Kepler%27s_equation); orbital elements from the KSP wiki

### 1.5 Orbital elements from position and velocity

The inverse of §1.4: given a craft's position r and velocity v at some moment, find its orbital elements. The rendezvous planner uses it to chain legs and to read a burn back as an orbit.

```
h = r × v                               angular momentum (plane normal)
a = 1 / (2/|r| − |v|²/μ)                vis-viva solved for a
e = ((|v|² − μ/|r|)·r − (r·v)·v) / μ    eccentricity vector (points at periapsis)
i = acos(h_z / |h|)
Ω = atan2(n_y, n_x),  n = ẑ × h         (Ω = 0 for an equatorial orbit)
ω, ν from the angles of e and r in the plane, measured from the node line
M = E − e·sin E,  E from ν              mean anomaly at this moment = the new epoch
```

- Code: `Kepler.ElementsFrom` (round-trips with `Kepler.StateAt`); tested in `RendezvousTests`
- Source: Curtis, Algorithm 4.2 (orbital elements from the state vector)

### 1.6 Sphere of influence

Inside a body's SOI, KSP only simulates that body's gravity (patched conics). The radius is the Laplace sphere:

```
r_SOI = a · (m / M)^(2/5)         a = body's orbit radius, m/M = mass ratio (μ/μ_parent works too)
```

Worked example — Mun: a = 12,000,000 m, μ ratio = 6.5138e10 / 3.5316e12 = 0.018444 → r_SOI = **2,429,559 m**.

- Code: values seeded from the Resonant Orbit Calculator data (they match this formula exactly)
- Source: [Sphere of influence (Wikipedia)](https://en.wikipedia.org/wiki/Sphere_of_influence_(astrodynamics)); KSP wiki `Template:Body data/SOI`

### 1.7 Atmosphere

KSP's real pressure curves are custom, but the classic exponential model with the published scale height H is a good approximation:

```
p(h) = p₀ · e^(−h / H)
```

Worked example — Kerbin at 10 km: p = 1 atm × e^(−10,000 / 5,600) = **0.168 atm**.

- Code: `Atmosphere.PressureAtmAt`
- Source: KSP wiki `<Body>/Param` (sea-level pressure, scale height); [Scale height (Wikipedia)](https://en.wikipedia.org/wiki/Scale_height)

### 1.8 Maneuver-node axes

KSP's maneuver node has three handles. For a craft at position r with velocity v:

```
prograde = v / |v|
normal   = (r × v) / |r × v|          "up" out of the orbit plane (purple triangle)
radial   = prograde × normal          in the plane, at right angles to prograde, pointing away from the body (cyan)
```

Each burn's Δv vector is split as (Δv·prograde, Δv·normal, Δv·radial). These are the numbers to type into the node editor in **Exact nodes** mode. On the Flight tab, **Send to KSP** puts the same three numbers and the node time on the craft you're flying (kRPC `Control.AddNode(ut, prograde, normal, radial)`, which uses the same axes). Later nodes chain off the earlier ones in KSP just as they do in the plan.

- Code: `NodeBurn.From`; tested in `RendezvousTests`

## 2. Changing your orbit around one body

Raising, lowering, reshaping and tilting an orbit without leaving the body you're around — and coming back down.

### 2.1 Hohmann transfer: changing circular orbit altitude

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
- Source: [Hohmann transfer orbit (Wikipedia)](https://en.wikipedia.org/wiki/Hohmann_transfer_orbit); Curtis 6.2

### 2.2 Elliptical orbits

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

The app tries both orders (r_meet = rₐ, or r_meet = rₚ) and keeps the cheaper. When rₚ = rₐ this is exactly the Hohmann transfer of §2.1.

Worked example — a 100 km × 2,000 km orbit of Kerbin, from the 80 km low orbit (r₀ = 680,000, rₚ = 700,000, rₐ = 2,600,000):

1. a = 1,650,000 m, e = 0.5758, T = 7,086 s (1 h 58 m), vₚ = 2,819.6 m/s, vₐ = 759.1 m/s
2. Apoapsis first: a₁ = 1,640,000 → burn 1 = **590.5 m/s**, burn 2 (raise periapsis from 80 to 100 km at apoapsis) = **8.6 m/s** → **599 m/s**
3. Periapsis first: 16.5 + 589.8 = 606 m/s — so the app uses the apoapsis-first order

**Inclination** is charged as a plane change (§2.3) at the target's apoapsis, where you are slowest. For the orbit above at 10°: 2 × 759.1 × sin 5° = **132 m/s** — doing the same 10° in the 80 km orbit would cost 397 m/s. The Mission Builder adds this when "Include worst-case plane changes" is on.

**Validation** checks only the independent values: periapsis above the atmosphere (or the highest terrain on airless bodies), apoapsis inside the sphere of influence, apoapsis ≥ periapsis, inclination 0–180°.

- Code: `OrbitShape`, `OrbitMath.TransferToEllipse`, `OrbitMath.PlaneChangeDv`, `CustomLocations.Analyze`, `CustomLocations.Validate`
- Source: Curtis 6.2–6.4 (Hohmann and general coplanar transfers) and 6.10 (plane changes)

### 2.3 Plane changes

Changing inclination by Δi at speed v costs:

```
Δv = 2 · v · sin(Δi / 2)
```

It's cheapest where you're slowest (high up, or at apoapsis). Worked example — matching Minmus's 6° inclination while still in 80 km Kerbin orbit: 2 × 2,278.9 × sin 3° = **238.5 m/s**. The Δv map lists a worst-case 340 m/s for Minmus; the Mission Builder's "Include worst-case plane changes" adds those map values.

- Code: map values only (`DvMapNode.PlaneChangeDv`)
- Source: Curtis 6.10; [Orbital inclination change (Wikipedia)](https://en.wikipedia.org/wiki/Orbital_inclination_change)

### 2.4 Synchronous (stationary) orbit

Choose a = r so the period equals the body's sidereal day T_day:

```
r = ∛( μ · T_day² / (4π²) )        altitude = r − R
```

Worked example — Kerbin, T_day = 21,549.425 s: r = ∛(3.5316e12 × 21,549.425² / 39.478) = 3,463,334 m → altitude **2,863,334 m**.

- Code: `OrbitMath.SemiMajorAxisFromPeriod`, `CelestialBody.SynchronousAltitudeM`
- Source: Eric Meyer's Resonant Orbit Calculator; [Geosynchronous orbit (Wikipedia)](https://en.wikipedia.org/wiki/Geosynchronous_orbit)

### 2.5 Deorbiting

To bring a target down (docked to your craft), burn retrograde at apoapsis r_a so the new periapsis r_p is inside the atmosphere (about 30 km on Kerbin; the default is 43% of the atmosphere's height, or the ground on airless bodies):

```
Δv = v_a − √(2μ·r_p / (r_a·(r_a + r_p)))       (v_a = speed at apoapsis now; the second term is vis-viva at r_a for the new orbit)
```

Worked examples — from 80 km circular to a 30 km periapsis: √(3.5316×10¹² / 680,000) − √(2 × 3.5316×10¹² × 630,000 / (680,000 × 1,310,000)) = **43.91 m/s**. From a 2,870 km relay orbit: **449.6 m/s**. The Δv is per m/s of the docked stack, which is heavier than your craft alone, so it burns more fuel than the same Δv would solo.

- Code: `Rendezvous.Deorbit`; tested in `RendezvousTests`
- Source: vis-viva (§1.3)

## 3. Going to another planet

Leaving one body's sphere of influence and arriving at another: the burns, when to make them, and where to point.

### 3.1 Escaping a body and arriving at another (hyperbolic burns)

Far from a planet you have a leftover speed v∞ (hyperbolic excess speed). Burning at periapsis of a low parking orbit (radius r) to reach it costs:

```
Δv = √( v∞² + 2μ/r ) − √( μ/r )
```

The same formula gives the capture burn at the destination (circularizing at radius r). Escape speed is the special case v∞ = 0 measured from rest: v_esc = √(2μ/r).

Worked example — leaving 80 km Kerbin orbit for Duna (v∞ = 918.3 m/s, see §3.2):
Δv = √(918.3² + 2 × 3.5316e12 / 680,000) − 2,278.9 = **1,072 m/s** (the Δv map says 950 + 130 = 1,080).

- Code: `TransferPlanner.HyperbolicBurnDv`
- Source: Curtis 8.6–8.7 (patched conics); [Hyperbolic trajectory (Wikipedia)](https://en.wikipedia.org/wiki/Hyperbolic_trajectory)

### 3.2 Interplanetary Hohmann: phase angle, timing, v∞

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
- Source: Curtis 8.2–8.3; [Synodic period (Wikipedia)](https://en.wikipedia.org/wiki/Orbital_period#Synodic_period)

### 3.3 Ejection angle: where in your parking orbit to burn

Your escape hyperbola bends by θ∞ between the burn (periapsis) and the direction you leave in:

```
e  = 1 + r · v∞² / μ             (eccentricity of the escape hyperbola)
θ∞ = arccos(−1/e)
```

Burn θ∞ before the direction you want to leave in: before **prograde** (planet's direction of travel) for outer planets, before **retrograde** for inner planets.

Worked example — Kerbin → Duna from 80 km: e = 1 + 680,000 × 918.3² / 3.5316e12 = 1.1624 → θ∞ = **149.4°**, so burn about 149° before Kerbin's prograde direction. (The porkchop optimum in the app uses the real, slightly-off-direction v∞, so it reports ~153°.)

- Code: `TransferPlanner.EjectionBurnFor`
- Source: Curtis 8.7; [Hyperbolic trajectory (Wikipedia)](https://en.wikipedia.org/wiki/Hyperbolic_trajectory)

### 3.4 Real launch windows: Lambert's problem and porkchop plots

The Hohmann numbers assume circular, flat orbits. For the real best window the app asks: *for this departure date and this flight time, what orbit connects where Kerbin is now to where Duna will be?* That is Lambert's problem. Solving it for thousands of (departure, flight time) pairs and colouring by total Δv gives a **porkchop plot**; the darkest spot is the best window.

- Method: universal-variable formulation with Stumpff functions C(z), S(z) (Curtis Algorithm 5.2), solved by bisection on z for robustness; single revolution, prograde
- Code: `Lambert.Solve`, `TransferPlanner.Plan`
- Sources: Curtis 5.3; [Lambert's problem (Wikipedia)](https://en.wikipedia.org/wiki/Lambert%27s_problem); [alexmoon's KSP Launch Window Planner](https://alexmoon.github.io/ksp/) for the approach in KSP
- Verified against Curtis Example 5.2 in the test suite

### 3.5 Orbits around the Sun (Kerbol)

An orbit around the Sun is an interplanetary destination, so the Mission Builder attaches it where every other interplanetary trip starts: Kerbin's escape node (next to the Duna, Eve and Moho intercepts), not Kerbol's 610 km low orbit. A solar orbit only "belongs" to Duna if it is inside Duna's sphere of influence — then it's a Duna orbit (§2.1).

Steps — combine §2.1 (around the Sun), §3.1 (leaving Kerbin) and the map's 950 m/s low-Kerbin-orbit → escape edge:

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
- Source: Curtis 8.2–8.7 (patched conics); same method as the Transfer Planner's Hohmann estimate (§3.2)

### 3.6 Transfers to custom locations

A custom location can be an origin or destination in the Transfer Planner. It's treated like a tiny planet with **no gravity** (μ = 0), which changes two things:

```
arrival burn   = v∞ at arrival               (rendezvous: match its speed directly; √(v∞² + 2·0/r) − 0 = v∞)
departure burn = v∞ at departure             (no Oberth effect when leaving it either)
```

**Planned or placed.** A *planned* location doesn't exist yet, so wherever you arrive becomes its position — every departure date is equally good and the planner uses the ideal Hohmann transfer (§3.2) from your earliest date. A *placed* location exists in your save; once its position is known it moves on its orbit like a planet, so the planner searches launch windows with the porkchop method (§3.4).

**Entering a position.** Two ways:

- **Orbital elements** at a date — longitude of ascending node Ω, argument of periapsis ω and mean anomaly M (Kerbal Engineer and MechJeb display all three for a vessel). The app stores M at that date and propagates with Kepler's equation (§1.4).
- **Phase angle** at a date — "θ degrees ahead of Kerbin". For a circular orbit in Kerbin's plane (i = 0, Ω = 0, ω = 0) the mean anomaly equals the longitude, so `M(date) = λ_Kerbin(date) + θ`, where λ_Kerbin is Kerbin's longitude from its own orbital elements.

The gap to Kerbin then changes by `(n_location − n_Kerbin)` per second, where `n = 2π / T`.

Worked example — the 10,000,000 km "Eye of Kerbol" (r = 10,261,600,000 m), planned:

1. Hohmann from Kerbin: 175 Kerbin days; ejection from 80 km **1,013.7 m/s**, arrival (rendezvous) **723.2 m/s** — the same numbers as the Δv map edge in §3.5
2. Its period is 6,032,218 s (279 days) against Kerbin's 9,203,545 s, so it pulls ahead of Kerbin by **0.444° per day**
3. If it were placed, the ideal phase angle at launch would be 180° − 360° × 175.1 / 279.3 = **−45.7°** (45.7° *behind* Kerbin — it's an inner orbit, so it catches up during the trip), and windows would repeat every **810 days**

- Code: `CustomLocationOrbits.ToElements`, `CustomLocationOrbits.ToBody`, `CustomLocationOrbits.PhaseAngleFromHome`, `TransferPlanner.PlanAnyTime`
- Source: Curtis 3.4 (mean anomaly), 8.2 (phase angles), 8.3 (synodic period)

## 4. Meeting a craft in orbit (rendezvous)

Getting from a launch, or from a craft already in orbit, to a standstill next to another craft. Used by the Rendezvous tab and by rendezvous legs (§6.5).

### 4.1 How a rendezvous is planned

The Rendezvous tab takes you from a launch (or a craft already in orbit) to a standstill next to each target in turn: a relay to repair, debris to deorbit. Every leg is the same four steps, and every burn is given both ways: **by feel** (which way to point, what to look for) and as **exact maneuver-node values** (time, prograde / normal / radial).

1. **Match planes** at the cheaper of the next two crossings of the orbits' planes (§4.2)
2. **Get into position**: wait for the window, or fly a phasing orbit to bring it sooner (§4.3)
3. **Transfer**: a Lambert intercept, searched around the Hohmann window (§4.4)
4. **Match velocity** at the meeting point

The positions come straight from the save: KSP stores each vessel's orbit as Keplerian elements (SMA, ECC, INC, LAN, LPE, MNA at epoch EPH), and §1.4 turns them into positions at any time. That is why the page asks you to plan from the save's current time — quicksave right before you plan.

- Code: `Rendezvous.Plan`, `Rendezvous.PlanLeg`; tested in `RendezvousTests`

### 4.2 Matching planes

Two orbits' planes cross along a line through the body's centre; you cross it twice per orbit, at the ascending node (AN, going "up" through the target's plane) and the descending node (DN). At the crossing your position lies in both planes, so you keep your speed and climb angle and just turn the horizontal part of your velocity into the target's plane:

```
v_new = v_r·r̂ + |v_h|·(n̂_target × r̂)        v_r = radial speed, v_h = horizontal velocity
Δv    = |v_new − v| = 2·|v_h|·sin(Δi/2)       Δi = angle between the planes (between the two h vectors)
```

That's §2.3 again, using the horizontal speed. Burn **anti-normal at the AN, normal at the DN**. The planner takes the cheaper of the next two crossings (the higher, slower one if your orbit is eccentric).

Worked example — circular 200 km orbit, target tilted 10°: v = √(3.5316×10¹² / 800,000) = 2,101.07 m/s → Δv = 2 × 2,101.07 × sin 5° = **366.24 m/s**.

- Code: `Rendezvous.MatchPlane`, `Rendezvous.RelativeInclinationDeg`; tested in `RendezvousTests`
- Source: Curtis 6.10

### 4.3 Phase angle, waiting, and phasing orbits

With both craft in the same plane, the Hohmann rule of thumb (§2.1) tells you when to burn. From your orbit's size a₁ to the target's a₂:

```
t_H = π·√(((a₁ + a₂)/2)³ / μ)            transfer time (half the transfer ellipse)
φ   = 180° − n₂·t_H                       lead angle: how far ahead the target must be when you burn
n   = √(μ / a³) = 360° / T                mean motion of each orbit
θ̇   = n₂ − n₁                             how fast the angle to the target changes
wait = (θ₀ − φ) / (n₁ − n₂)  (mod 360°)   time until the target is φ ahead (θ₀ = how far ahead it is now)
T_syn = 1 / |1/T₁ − 1/T₂|                  windows repeat every synodic period
```

Worked example — 100 km orbit to a 1,000 km relay: a₁ = 700 km, a₂ = 1,600 km → t_H = **2,061.6 s** (34 min), T₂ = 6,766.7 s so φ = 180° − 360° × 2,061.6 / 6,766.7 = **70.32°**; T₁ = 1,958.1 s → a window every **2,755.5 s** (46 min). Burns: **403.26 m/s** prograde, then **326.57 m/s** to match (§2.1).

When the window is far away (orbits of nearly the same size, like two relays in one constellation, never come into step), a **phasing orbit** brings it sooner: burn prograde or retrograde to change your orbital period to T_p, fly k whole laps back to the burn point, and arrive when the target is at the lead angle:

```
target ahead after k laps:  θ₀ + n₂·k·T_p ≡ φ  (mod 360°)
T_p = (Δ + 360°·m) / (n₂·k),   Δ = (φ − θ₀) mod 360°,   m = whole number
```

For each k = 1…6 the planner offers the m giving a period just below yours (**lower**: faster, you catch up) and just above (**higher**: slower, the target catches up), and skips any that dip into the atmosphere or leave the SOI. The burn changes your speed at the burn point to √(μ·(2/r − 1/a_p)) with a_p = ∛(μ·T_p² / 4π²) (§2.4). More laps means a smaller change, so it's cheaper but slower. The planner waits if the window is within 3 of your orbits; otherwise it picks the cheapest phasing orbit that still beats waiting. When the orbits are the same size (within 0.5%) the lead angle is 0°: the phasing lap brings you back next to the target, and a short hop of ¼ to under ½ an orbit (§4.4) closes what's left.

Worked example — both in 200 km circular orbits (T = 2,392.37 s), target 40° ahead, so Δ = 320°. With k = 6: m = 5, T_p = (320° + 5 × 360°) / (6 × 360° / 2,392.37 s) = **2,348.07 s** → a_p = 790,092.7 m, far side at **180.2 km** (lower), burn √(μ(2/800,000 − 1/790,092.7)) − 2,101.07 = **−13.21 m/s** (retrograde), and the same again to stop: about 26 m/s in total over 6 laps (3.9 h). With k = 1 it would be −87.64 m/s with the far side at 79.2 km, but only one lap.

- Code: `Rendezvous.Guide`, `Rendezvous.PhasingOptions`; tested in `RendezvousTests`
- Source: Curtis ch. 6 (Hohmann transfers and phasing maneuvers)

### 4.4 The intercept: Lambert's problem again

The rule of thumb assumes circular orbits. The real transfer is found like the Transfer Planner's (§3.4): for each burn time near the predicted window and each flight time (½ to 1½ × t_H), solve Lambert's problem from your position to where the target will be, and add up the two burns:

```
cost = |v_depart − v_you| + |v_target − v_arrive|
```

The cheapest cell is refined by zooming in, and drawn as a small porkchop plot you can click to fly a different transfer (later legs move with it). Two details matter here:

- **Prograde about your orbit**, not about the planet's north pole, so polar and retrograde orbits work (`Lambert.Solve` takes the reference direction)
- **180° transfers are unsolvable**: the transfer plane is undefined and the formulas divide by nearly zero, which can give a confidently wrong answer that looks cheaper than Hohmann. Every solution is therefore flown (propagated, §1.4) and thrown away if it ends more than 10 m from the target. That leaves dark gaps on the plot. Between coplanar circular orbits the best transfer sits right next to one, and is found to within a few cm/s of the Hohmann numbers (test: 403.26 / 326.57 m/s, arriving within 10 m)

- Code: `Rendezvous.PlanLeg`, `Rendezvous.Intercept`; tested in `RendezvousTests`
- Source: Curtis 5.3; the propagation check is this app's safeguard

### 4.5 When and where to launch

Kerbin turns under the fixed (inertial) frame the orbits are measured in. KSP stores each body's starting rotation; Kerbin starts at **90°**. The launch site's longitude in the inertial frame, comparable with a target's LAN (Ω), is:

```
λ(t) = longitude + initial rotation + 360° · t / T_sidereal        KSC: longitude −74.5575°, latitude −0.0972°
```

The pad is (nearly) on the equator, so it passes under the target's plane when λ = Ω (northbound: the ascending pass) or Ω + 180° (southbound). Lifting off then puts you straight into the plane, with no plane change. The compass heading uses the launch azimuth for inclination i (sin β = cos i / cos(latitude)), less the speed Kerbin's spin already gives you eastward:

```
β = 90° − i (northbound pass) or 90° + i (southbound)
v_orbit = √(μ / r_parking),  v_spin = 2π·R·cos(latitude) / T_sidereal
heading = atan2(v_orbit·sin β − v_spin, v_orbit·cos β)
```

You arrive in the parking orbit at the climb time you enter, at the downrange angle you enter (degrees around Kerbin from the pad). Fly a normal launch once and note both, so the plan matches your ascents. The planner lists the next two passes each way, plus a **due-east** launch at the moment that gives the shortest wait once in orbit (plane fixed later, §4.2). It picks the cheapest, or the earliest arrival among those within 10 m/s of it.

Worked example — KSC at UT 0: λ = −74.5575° + 90° = **15.4425°**; a quarter of a sidereal day (21,549.4 s) later, **105.4425°**. Target at i = 45° from an 80 km parking orbit: v_orbit = 2,278.93 m/s, v_spin = 174.94 m/s, β = 45° → heading = atan2(1,611.47 − 174.94, 1,611.47) = **41.7°**.

- Code: `LaunchSite.InertialLongitudeDeg`, `Rendezvous.LaunchOptions`; tested in `RendezvousTests`
- Source: launch azimuth from spherical trigonometry, sin β = cos i / cos φ ([Braeunig, Orbital Mechanics](http://www.braeunig.us/space/orbmech.htm)); KSP's body `initialRotation` (Kerbin 90°) and `rotationAngle` = initial rotation + 360°·UT / rotation period, the same convention kOS exposes as `BODY:ROTATIONANGLE`. Check it in game the first time: at the planned lift-off, the target's AN or DN marker should be right over the KSC

### 4.6 Starting from a craft around another body

The Rendezvous tab can start from a craft (or the craft you're flying in KSP) that orbits another body than the targets: a lander around the Mun meeting a relay around Kerbin, say. It uses the same rule as a mission leg from another body (§6.5). The Δv map's steps go from the craft's body's low orbit to the targets' body's low orbit, and the rendezvous starts on that low orbit, anywhere on it. The map's steps keep their map values (the craft's real orbit may differ from the map's low orbit).

To date the rendezvous burns, the plan estimates how long the steps take. Each step between a moon and the planet it orbits counts as a Hohmann half-orbit (§2.1) around the planet, between the moon's orbit and the planet's low orbit:

```
a = (r_moon + R_planet + h_low) / 2
t = π·√(a³ / μ_planet)                for each moon ↔ planet step; time inside the moon's SOI is left out
rendezvous start = start time + Σ t   (rounded up to the minute)
```

A step between planets waits for a launch window, which matters far more than the flight, so there is no estimate: pick the window on the Transfer Planner (§3.4) and set the start time to the arrival. On the Flight tab the steps are shown first; once the craft is around the targets' body, Re-plan (§8.4) plans the rendezvous from its real orbit.

Worked example: from the Mun to Kerbin's 80 km orbit (μ = 3.5316×10¹², Mun orbit r = 12,000,000 m, low orbit 680,000 m):

1. Map steps: Mun low orbit → Mun intercept (310) → Kerbin low orbit (860), **1,170 m/s** (or 310 when aerobraking into Kerbin's orbit).
2. a = (12,000,000 + 680,000) / 2 = 6,340,000 m, so t = π·√(6,340,000³ / 3.5316×10¹²) = π × 8,494.7 = **26,686.9 s**, about 7 h 25 m (the map says about 8 hours).
3. Planning from Year 1, Day 1, 0:00, the rendezvous starts Year 1, Day 2, 1:25 (26,700 s). To a relay at 2,868 km: a 668 m/s transfer burn and a 431 m/s match, **2,269 m/s** in all with the map's steps.

- Code: `LegTools.FindApproach`, `LegTools.TripTimeS`, `RendezvousViewModel.BuildRequest`, `FlightViewModel.RefreshDrift`; tested in `LegToolsTests`
- Source: Hohmann transfer (§2.1, Curtis 6.2), the Δv map (§6.1)

## 5. Deploying a satellite constellation

Dropping several satellites evenly around one orbit from a single carrier.

### 5.1 Resonant orbits for satellite constellations

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

### 5.2 Burn times and node values

Resonant deployment (§5.1) needs the carrier to pass the shared apsis once per satellite. The *shared apsis* is the point at the deployment radius r: the carrier's periapsis for a resonant orbit, its apoapsis for a dive orbit. The Flight tab times every burn from the carrier's real orbit:

```
1. Enter the carrier orbit: if the craft isn't on it yet (period and shared-apsis radius both within 0.5%), burn at the
   craft's apsis nearest r (any time, if its orbit is circular):
       a′ = (|r_burn| + r_far) / 2      r_far = 2·a_carrier − r, the carrier's other apsis
       v′ = √(μ·(2/|r_burn| − 1/a′))    horizontal, in the orbit's plane
       Δv = v′·ĥ − v                      split into node axes (§1.8)
2. Release k (k = 1…n) at the k-th pass of the shared apsis after that:
       t_k = t_first + (k − 1)·T_carrier
       v_circ = √(μ / |r(t_k)|)          horizontal
       Δv_k = v_circ·ĥ − v(t_k)          the satellite's node (it's flown by the satellite, not the carrier)
```

Here ĥ is the horizontal direction (the velocity with its radial part removed). At an apsis the velocity is already horizontal, so these Δv are pure prograde (positive) or retrograde (negative). If the craft's apsis misses r by more than 1 km, the plan warns you: the satellites will end up at that apsis instead, and their spacing will be off.

Worked example: 3 satellites at Kerbin's synchronous altitude, 2,863,334.1 m (r = 3,463,334.1 m, μ = 3.5317×10¹² m³/s²), from a circular orbit at that altitude:

1. Satellite period 21,549.1 s; carrier period × 4/3 = **28,732.1 s**, so a = 4,195,530.5 m and the far apsis is at **4,327.7 km**
2. Enter: v = √(μ/r) = 1,009.82 m/s → v′ = √(μ·(2/r − 1/a)) = 1,094.40 m/s, so **+84.57 m/s prograde**
3. Satellite k circularizes at the periapsis one, two and three carrier laps later (+28,732.1 s each): 1,009.82 − 1,094.40 = **−84.57 m/s (retrograde)**

- Code: `ResonantDeployment.Plan`; tested in `FlightTests`
- Source: vis-viva (§1.3); §5.1 for the carrier orbit

## 6. Budgeting a mission

How the Mission Builder adds up a mission's Δv: map values first, then leg tools that replace them with properly planned burns.

### 6.1 The Δv map and mission budgets

The Mission Builder adds up legs from the community Δv map. Each map edge is a published Δv; aerobraking replaces a burn where the map allows it; plane changes are optional worst-case extras. A phase's budget is:

```
phase Δv = Σ(legs in the phase) × (1 + margin%/100) + extra
```

**Launching straight to a higher orbit.** A stop in a planet's or moon's orbit above its low orbit (picked as "Orbit" with an altitude) hangs off the low orbit on the map. When the leg starts on that body's surface and the low orbit isn't a stop, the two steps are shown as one leg (and the same coming down):

```
surface → orbit Δv = (surface → low orbit, from the map) + Hohmann low orbit → orbit (§2.1)
```

Skipping the stop in low orbit doesn't skip a burn. Circularizing at 80 km and then burning Hohmann's Δv₁ are both prograde burns at the same point, so a direct ascent simply makes them one longer burn: it ends at about 80 km flying horizontally at the transfer orbit's periapsis speed, then circularizes at apoapsis.

Worked example — Kerbin surface → 150 km:

1. Low orbit at 80 km: circular speed √(μ/680,000) = 2,278.9 m/s; the transfer to 150 km needs 2,334.0 m/s there (vis-viva, §1.3), so Δv₁ = **55.1 m/s** whether it's a separate burn or the end of the ascent
2. Circularize at 150 km: Δv₂ = **53.8 m/s** (§2.1)
3. Leg: 3,400 + 108.9 ≈ **3,509 m/s**

Check that leaves out the 80 km orbit: an ideal launch (no atmosphere or rotation, instant burns, surface Hohmann to the orbit) costs 2,573.1 m/s to 80 km and 2,681.4 m/s to 150 km. That's 108.3 m/s more, against the 108.9 charged, so going straight up saves about 0.5 m/s. That's far inside the uncertainty of the 3,400 ascent figure (±100–200 m/s with the rocket and how it's flown). A real ascent ends near 70–80 km either way, because of the atmosphere.

- Code: `MissionPlanner.CombineClimbs` (joins the steps; only orbits the stop picker shows as "Orbit", and only where no leg tool supplies either step), `RouteLeg.Parts`

Known simplifications:

- The map is a tree, so moon-to-moon trips go through low Kerbin orbit and are overstated.
- Custom orbits around a planet hang off its low orbit, so arriving from another planet charges "capture to low orbit, then climb". Capturing straight into a high orbit would be cheaper.
- Custom orbit inclination is charged as a worst-case plane change at apoapsis (§2.2); the real cost depends on where your ascending node is.
- Atmospheric ascent values (3,400 for Kerbin) are typical, not exact — they depend on your rocket.
- Map surface ↔ low orbit values are optimistic; many players budget more (e.g. 700 instead of 580 for the Mun). Edit them in the Δv map tab.

- Code: `DeltaVMap.Route`, `MissionPlanner.Summarize`
- Source: [KSP Δv map 1.0.4 by /u/swashlebucky](https://www.reddit.com/r/KerbalSpaceProgram/) (based on Kowgan's map; CC BY-NC-SA 4.0)

### 6.2 Leg tools: replacing map values with a planned leg

A mission leg runs from one stop to the next and is made of Δv-map steps (§6.1). A leg tool works out part of the leg properly and puts its result in place of the map values for the steps it covers. The steps stay, so each one keeps its phase and aerobrake choice. A tool that no longer fits the leg (the stops changed) is ignored, and the map values come back.

### 6.3 Transfer legs: a chosen launch window

A leg is a transfer leg when one of its steps goes between two bodies that orbit the same parent (Kerbin → Duna, Duna → Kerbin). The window comes from the Transfer Planner (§3.4), set up from the leg:

```
origin, destination  = the two bodies of that step (for Duna → Eve via Kerbin: the first and last body)
parking orbit        = the origin's low orbit on the map, where the leg's climb out starts
arrival orbit        = the destination's low orbit on the map, if the leg goes down to it (else a flyby)

ejection steps  = from the parking orbit up to the crossing      first step = ejection Δv (§3.1), the rest 0
capture steps   = from the crossing down to the arrival orbit    first step = capture Δv (§3.1), the rest 0
```

The Lambert solution already points the ejection out of the plane where it needs to (§3.4), so these steps get no plane-change extra. Capture steps keep their aerobrake option: aerobraking replaces the capture burn just as it replaces the map's. Steps after the arrival orbit (for example, Duna's low orbit up to a 1,000 km relay orbit) keep their map values. The mission saves only the earliest date, the departure time and the flight time, and works out the window again (one Lambert solve) when the mission opens.

Worked example: Kerbin low orbit (80 km) to Duna low orbit (60 km), best window from Year 1, Day 1:

1. The planner picks departure Year 1, Day 231 (UT 4,987,091.8 s) and a 269-day flight. The Lambert solution gives v∞ = **854.38 m/s** leaving Kerbin and **890.99 m/s** arriving at Duna.
2. Ejection (§3.1, μ = 3.5316×10¹², r = 680,000 m): √(854.38² + 2μ/r) − √(μ/r) = 3,334.22 − 2,278.93 = **1,055.3 m/s**. This replaces the map's 950 + 130 = 1,080 for LKO → escape → Duna intercept.
3. Capture (μ = 3.0136×10¹¹, r = 380,000 m): √(890.99² + 2μ/r) − √(μ/r) = 1,542.72 − 890.54 = **652.2 m/s**. This replaces the map's 250 + 360 = 610 for intercept → elliptical → low orbit (or 0 when aerobraking).

- Code: `LegTools.FindTransfer`, `LegTools.ApplyTransfer`, `MissionBuilderViewModel.ApplyTools`; tested in `LegToolsTests`
- Source: §3.1 and §3.4 (Curtis 8.6–8.7, 5.3)

### 6.4 Deploy steps: a constellation released at a stop

A stop that is a circular orbit (a low orbit, the synchronous orbit or a circular custom location) can release *n* evenly spaced satellites from a resonant or dive carrier orbit (§5.1, §5.2). All of the burns happen at the shared apsis, where the circular speed and the carrier's speed differ by the same amount, so every burn is the same size:

```
burn  = | √(μ/r) − √(μ·(2/r − 1/a_carrier)) |
carrier Δv    = burn (into the carrier orbit) + burn (back to circular, only if more stops follow)
each satellite = burn (circularizes with its own engine; not counted in the rocket's phases)
```

The carrier Δv is added to the phase picked for the deployment in Legs & phases (by default, the phase of the leg arriving at the stop).

Worked example: 3 relays at Duna 1,000 km, the last stop of the mission (μ = 3.0136×10¹¹, r = 1,320,000 m):

1. Satellite period T = 2π·√(r³/μ) = **17,357.8 s**. The carrier period is T × 4/3 = **23,143.8 s**, so a = 1,599,066 m and its apoapsis is at **1,558.1 km**.
2. √(μ/r) = 477.81 m/s and √(μ·(2/r − 1/a)) = 517.83 m/s, so the burn is **40.0 m/s**.
3. Carrier: **40.0 m/s** (it doesn't return to circular, because this is the last stop). Each relay circularizes with **40.0 m/s** of its own.

- Code: `LegTools.CanDeploy`, `LegTools.EvaluateDeploy`, `MissionPlanner.Summarize` (its `steps`); tested in `LegToolsTests`
- Source: §5.1 (resonant carrier orbit), vis-viva (§1.3)

### 6.5 Rendezvous legs: meeting a placed craft

A leg is a rendezvous leg when it arrives at a *placed* craft: a custom location, an imported vessel, or a craft from the save that the mission keeps itself (added with *＋ Rendezvous*, so it isn't on the Locations tab). There is then a real craft whose position is known. Where the leg starts decides how much of it the rendezvous covers. The Rendezvous tab plans that part (§4.1): the launch time, a plane match, phasing, the Lambert intercept and the match-velocity burn.

```
from another placed craft around the same body   the whole leg is the rendezvous, starting from that craft's orbit
from Kerbin's surface                            surface → low orbit keeps the map's values (the climb); the rest is the rendezvous
from anywhere else                               the map's steps reach the target body's low orbit; the last step
  (that body's low orbit or another orbit,       (low orbit → craft) is the rendezvous, starting anywhere on the
   its surface, or another body)                  low orbit (the map's 80 km at Kerbin)
rendezvous steps                                 first step = Σ planned burns, the rest 0
```

Starting from a low orbit the mission arrives in, *where* on that orbit isn't known. The plan starts at longitude 0 when the leg begins. That changes how long you wait for the phasing window, but hardly the Δv: phasing costs almost nothing, and the transfer and match burns depend on the two orbits, not on where you are on yours.

The plan's burns already include any plane change, so these steps get no plane-change extra. Where craft are depends on the date and on the save, so the mission keeps the plan's result (its orbital Δv and arrival time) as well as the start time and picks (launch, phasing orbit, porkchop point). Opening the leg again plans from those, so it shows the same plan while the craft haven't moved. A new leg tool on the next leg starts when this one arrives.

**Back-to-back rendezvous.** The map reaches a craft only through its body's low orbit, so craft A → craft B is two map steps (A → low orbit → B). You never stop in that low orbit, so the Mission Builder shows the two as one leg, A → B, whose map value is the two steps added; the rendezvous then replaces it. The second leg starts from A's orbit when the first one arrives (you're alongside A then), which is what the Rendezvous tab does when one plan meets both craft: planned leg by leg or in one go, the legs come out the same. If the first leg is planned again and now arrives later than the second leg's saved start, the second plan is out of date (A and B have moved on since): the mission flags it, and opening the leg plans it again from the new arrival.

Worked example: launch to a placed station in a 150 km circular orbit inclined 5° (μ = 3.5316×10¹², LKO r₁ = 680,000 m, station r₂ = 750,000 m):

1. The map's step LKO → Station is a Hohmann transfer (§2.1): a = 715,000 m, so 2,334.04 − 2,278.93 = 55.11 m/s, then 2,169.98 − 2,116.20 = 53.78 m/s, **108.9 m/s** in all. Its worst-case plane change for 5° at the station is 2 × 2,169.98 × sin 2.5° = **189.3 m/s** (§2.3).
2. The Rendezvous tab lifts off as KSC passes under the station's orbit, so the rocket climbs straight into its plane. The plan needs only **109 m/s** of orbital burns and no plane change, and that replaces the step.
3. Going on from the station to a relay at 400 km in Kerbin's equatorial plane starts from the station's 5° orbit, so that plan has a 189 m/s plane match among its burns (479 m/s in total).
4. Coming home from the Mun to meet a relay at about 2,870 km: Mun surface → Mun low orbit (580) → Mun intercept (310) → Kerbin low orbit (860, or aerobrake) are the map's steps. The last step, Kerbin low orbit → relay, is the rendezvous. From 80 km, that is a small plane match (22 m/s for 0.56°), a 668 m/s transfer burn and a 433 m/s match, **1,122 m/s** in place of the map's Hohmann value.

- Code: `LegTools.FindRendezvous`, `LegTools.ApplyRendezvous`, `RendezvousLeg.StartsBefore`, `MissionPlanner.CombineCraftHops`, `MissionBuilderViewModel.RendezvousStartFor`, `RendezvousViewModel.EditLeg`; tested in `LegToolsTests`
- Source: §4.1 (Curtis 6.6–6.9, 5.3), Hohmann transfer (§2.1)

## 7. Your rocket: Δv and TWR

What a vessel or a saved design can actually do, and whether that's enough for the mission.

### 7.1 A vessel's remaining Δv (rocket equation)

The Vessels tab works out what a vessel in your save can still do from **its parts and what's left in its tanks**. The save gives each part's dry mass, the resources in it and each engine's thrust limiter; the part data read from your KSP folder (ModuleManager's cache, so mod patches are included) gives each engine's thrust, Isp and propellant mix, and each resource's density.

```
m_dry = Σ part masses                       m_wet = m_dry + Σ units × density
ṁ_i   = F_i / (Isp_i · g₀)                  (mass flow of engine i; F with its thrust limiter applied)
Isp   = Σ F_i / (g₀ · Σ ṁ_i)                (all engines firing together)
Δv    = Isp · g₀ · ln(m_wet / m_burnout)    (Tsiolkovsky rocket equation, vacuum Isp)
t     = m_burned / Σ ṁ_i                    (burn time)
TWR   = F / (m · g)
```

**Propellant mix.** An engine burns its propellants in a fixed volume ratio — liquid-fuel engines use LiquidFuel 0.9 : Oxidizer 1.1. Each engine's mass flow is split across its propellants by `ratio × density`, giving units per second of each; the burn ends when the **first** propellant runs out, so leftover oxidizer (or fuel) doesn't count. Electric charge never limits a burn (solar panels recharge it), and jet engines are left out (no air in a vacuum).

**Assumptions.** Every tank feeds every engine at once — no staging, no fuel lines — which matches most vessels already in orbit. The thrust limiter changes the burn time, not the Δv (unless engines with different Isp are mixed, where it changes the blend).

Worked example — the relay "KerbLink Mk2 A" (14 parts: HECS probe core, RA-2 relay antenna, 2 × Oscar-B tanks, 3 × Ant engines at 6 % thrust, …):

1. Dry mass = 0.1 + 0.15 + 0.01 + 2 × 0.025 + 3 × 0.02 + 3 × 0.0175 + 3 × 0.001 = **0.4255 t**
2. Left in the tanks: 6.4427 LiquidFuel + 7.8745 Oxidizer = 14.3172 units × 0.005 t = **0.07159 t** → wet mass **0.49709 t**
3. The Ants burn 0.9 : 1.1, and 6.4427 : 7.8745 is exactly that ratio, so both run out together
4. Δv = 315 × 9.80665 × ln(0.49709 / 0.4255) = 3,089.1 × 0.15550 = **480.3 m/s**
5. Thrust = 3 × 2 kN × 6 % = 0.36 kN → ṁ = 0.36 / (315 × 9.80665) = 0.0001165 t/s → burn time = 0.07159 / 0.0001165 = **614 s** (37 s at full thrust)

- Code: `VesselAnalysis.Analyze`, `PartCatalogReader.Parse`; tested in `VesselAnalysisTests`
- Source: [Tsiolkovsky rocket equation (Wikipedia)](https://en.wikipedia.org/wiki/Tsiolkovsky_rocket_equation); part and resource values from the game's own configs (ModuleManager.ConfigCache)

### 7.2 A saved design's stage-by-stage Δv

Ships saved in the VAB or SPH (`saves/<save>/Ships/*.craft`) list every part with the stage that fires it (`istg`), the stage whose decoupler drops it (`sepI`), what it's attached to, its fuel, thrust limiters and fuel lines. The planner flies the staging the way KSP's stage Δv readout does:

```
for stage s = first … 0:
    attached  = parts not dropped yet          (sepI < s; launch clamps go at the first stage)
    engines   = attached engines fired in stage s or earlier
    burn in steps — each step lasts until the next tank runs dry:
        each engine draws from the tanks it can reach (below), soonest-dropped first
        Δv += (ΣF / Σṁ) · ln(m_start / m_end)        (§7.1: ΣF / Σṁ = Isp_eff · g₀)
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
- Source: KSP 1.12 craft format and fuel-flow rules (resource flow modes, `fuelCrossFeed`, `CModuleFuelLine`); MechJeb's fuel-flow simulation for the staging approach; Tsiolkovsky rocket equation (§7.1)

### 7.3 Does a design have enough Δv for the mission?

In the Mission Builder's Rocket pane you can pick a saved design. Its stages (§7.2, vacuum Δv, in firing order) are poured into the mission's phases (§6.1, in burn order): the first stage fills the first phase; whatever it has left carries into the next phase; when a stage runs out part-way through a phase, the next stage takes over — as in flight.

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

### 7.4 TWR at every landing and lift-off

Enough Δv isn't enough if the stage firing at a landing or lift-off can't hold the rocket up against that body's gravity. For every leg of the route that touches down on or leaves a surface (and costs Δv — parachute and aerobrake landings are skipped), the planner finds where it falls in the stages (the same pour as §7.3, a phase's margin at its end), which stage is firing then, and how heavy the rocket is:

```
mass x m/s into a stage = m_start · (m_end / m_start)^(x / Δv_stage)     (constant Isp: mass falls exponentially with Δv)
thrust at pressure p    = F_vac + (F_ASL − F_vac) · p                     (p in atm; 0 for airless bodies; never below 0)
TWR                     = thrust / (mass · g_surface),   g_surface = μ / R²
```

| TWR | Rating |
|---|---|
| below 1 | ✗ can't lift off / can't slow the descent |
| 1 – 1.3 | ✓ amber — works, but slowly and with little room to correct |
| 1.3 and up | ✓ |

A TWR below 1 anywhere turns the design verdict red; a low one turns it amber.

Worked example (the numbers in `DesignCheckTests`): a lower stage giving 1,000 m/s (10 → 6 t, 200 kN vacuum / 150 kN at sea level) under a lander stage giving 2,000 m/s (4 → 2 t, 30 kN vacuum / 10 kN at sea level). Phases: Launch 1,000, Lander 1,500 m/s.

1. Lift-off from Kerbin (g = 9.81, 1 atm): first stage, full, sea-level thrust → 150 / (10 × 9.81) = **1.53**
2. Landing on the Mun (g = 1.63, airless) at the start of the Lander phase: the lower stage is just used up, so the lander fires at 4 t → 30 / (4 × 1.63) = **4.60**
3. Lift-off from the Mun 580 m/s into the lander: mass = 4 × (2 / 4)^(580 / 2000) = **3.272 t** → 30 / (3.272 × 1.63) = **5.63**
4. On Duna (0.067 atm) the lander would make 30 + (10 − 30) × 0.067 = **28.66 kN**; on Eve (5 atm) it would make none

- Code: `DesignCheck.Check` (surface events), `DesignCheck.ThrustAt`, `SurfaceTwr.Rating`; tested in `DesignCheckTests`
- Source: KSP engine thrust vs. pressure (thrust ∝ Isp at fixed fuel flow, Isp interpolated along `atmosphereCurve`); a linear curve between 0 and 1 atm is a simplification

## 8. Working with your save and the live game

Reading orbits from a KSP save or from the running game, and tracking how far a flight has drifted from its plan.

### 8.1 Importing vessels from a KSP save

KSP stores every vessel's orbit in the save (`saves/<name>/persistent.sfs`) as an `ORBIT` block with the same Keplerian elements a placed custom location uses — so importing is a unit conversion, not an orbit fit:

```
SMA  semi-major axis a (m, from the body's centre)     ECC  eccentricity e
INC  inclination (°)    LAN  Ω (°)    LPE  argument of periapsis ω (°)
MNA  mean anomaly M at the epoch (radians)    EPH  epoch (UT seconds)    IDENT / REF  the body orbited

periapsis altitude = a(1 − e) − R        apoapsis altitude = a(1 + e) − R        (R = body's equatorial radius)
mean anomaly (°)   = MNA × 180/π,  wrapped into 0–360°  (Ω and ω are wrapped the same way)
date of position   = EPH
```

From there the location moves exactly like the vessel: Kepler's equation (§1.4) from M at EPH. Only `ORBITING` vessels with e < 1 import; landed, sub-orbital and escaping vessels don't have an orbit to place. When KSP writes the save again, a linked location's orbit and position are replaced by the new values (name, description and images are kept).

Worked example — the relay "KerbLink Mk2 A" around Kerbin (R = 600,000 m):

1. SMA = 3,470,794.97 m, ECC = 0.0027130 → a·e = **9,416.3 m**
2. Periapsis = 3,470,794.97 − 9,416.3 − 600,000 = **2,861,378.7 m** (2,861.4 km); apoapsis = 3,470,794.97 + 9,416.3 − 600,000 = **2,880,211.2 m** (2,880.2 km)
3. MNA = 4.0125185 rad × 57.29578 = **229.900°** at EPH = 816,532.3 s (Year 1, Day 38)
4. INC 0.560°, LAN 281.711°, LPE 160.828° are copied as they are

- Code: `KspSaveReader.Parse`, `SaveImport.Convert`, `SaveImport.Refresh`; tested in `KspSaveTests`
- Source: KSP 1.12 save format (`ORBIT` node written by `OrbitSnapshot`); Curtis 4.4 (orbital elements)

### 8.2 Following the live game (kRPC)

While connected to the game (*Tools › Connect to KSP*), the planner reads the game time every 2 s and, in flight, every vessel's orbit every 10 s. kRPC reports the same elements as the save, in radians: a, e, i, Ω, ω, M at epoch and the epoch. So a live orbit is the save's `ORBIT` block (§8.1) with i, Ω and ω × 180/π.

**Which save vessel is it?** kRPC has no vessel ids, so live vessels are paired with save vessels by name. A different type adds 100 to the score. Among same-named vessels, the closest orbit wins:

```
score = |ln(a₁/a₂)| + |e₁ − e₂| + |i₁ − i₂|/180°        (+10 if they orbit different bodies)
```

**Has the orbit really changed?** KSP restates a coasting craft's orbit at a new epoch, so the numbers change while the path doesn't. Two statements are the same path when a, e and i agree, and the craft is in the same place at the same time *t*. The angles that are undefined for circular or equatorial orbits are compared through the combinations that stay defined:

```
n = √(μ / a³)                                  mean motion (rad/s)
ϖ = Ω + ω                                      longitude of periapsis (Ω skipped if i ≈ 0, ϖ skipped if e ≈ 0)
L(t) = ϖ + M₀ + n·(t − epoch)                  mean longitude: where the craft is along the orbit at t
same path ⇔ |Δa|/a, |Δe|, |Δi|, |ΔΩ|, |Δϖ|, |ΔL(t)| all ≤ 10⁻⁵   (angles in radians, wrapped to ±π)
```

10⁻⁵ rad is about 10 m per 1,000 km of orbit radius. That's more than a coasting craft's jitter under physics, and far less than any burn. Only a real change re-plans the Rendezvous tab and moves linked custom locations. Otherwise only the game time moves the plan's start, once per 60 s of game time.

Worked example: "KerbLink Mk2 A" (a = 3,470,794.97 m, M₀ = 4.0125185 rad at epoch 816,532.3 s), restated by KSP 1,000 s later:

1. n = √(3.5316×10¹² / 3,470,794.97³) = **0.000290631 rad/s** (period 21,619 s)
2. The restated orbit has M₀′ = 4.0125185 + 0.000290631 × 1,000 = **4.3031497 rad** at epoch 817,532.3 s
3. At any t, L(t) − L′(t) = (4.0125185 + n·(t − 816,532.3)) − (4.3031497 + n·(t − 817,532.3)) = 0, so it's the same path: nothing re-plans
4. Had the craft been 0.01 rad (35 km) further along, |ΔL| = 0.01 > 10⁻⁵, so it counts as changed

- Code: `LiveMerge.Apply`, `LiveMerge.SamePath` (`Core/Ksp/LiveGame.cs`), read by `KspLink.ReadVessels`, laid over the save by `SaveSync.ApplyLive`; tested in `LiveGameTests`
- Source: kRPC SpaceCenter API (`Orbit` class); mean longitude as in Murray & Dermott, *Solar System Dynamics* 2.9; Curtis 3.4 (mean motion)

### 8.3 Flying a plan: how far off it you are

The Flight tab keeps the orbit a plan starts from and its burns. Where the plan has the craft at time *t* is that start orbit with each of the craft's own burns before *t* applied in turn (§1.8, the same step the planner uses). Satellites' burns are left out, because they don't move the carrier. The drift is the straight-line distance between where the plan has the craft and where its real orbit (live from the game, §8.2) has it:

```
planned(t) = start orbit, then each own burn with time ≤ t applied (Rendezvous.OrbitAfter)
drift(t)   = | r_real(t) − r_planned(t) |          (positions from Kepler's equation, §1.4)
Re-plan lights up when drift > 1 km
```

A burn flown a little early, late, long or short shows up as a drift that grows with time. Being late along the same orbit shows up straight away, as distance along the track.

Worked example: a craft on a 100 km circular Kerbin orbit (r = 700,000 m) that is 1 s behind where the plan has it:

1. v = √(3.5317×10¹² / 700,000) = **2,246.2 m/s**, so n = v / r = 0.0032088 rad/s
2. drift = 2r·sin(n·1 s / 2) = 2 × 700,000 × sin(0.0016044) = **2,246.2 m**: over 1 km, so Re-plan lights up

- Code: `Flight.PlannedOrbitAt`, `Flight.DriftM`, `FlightViewModel.DriftThresholdM`; tested in `FlightTests`

### 8.4 Re-planning a rendezvous

Re-plan runs the whole Rendezvous planner (§4.1) again with the craft's real orbit as the start, from a few minutes after now (the lead time). It plans for the targets whose arrival is still to come, on their current orbits. The planner picks the phasing and intercept again (the cheapest), so a choice made on the Rendezvous tab can change. That's intended: the old choice was for an orbit you're no longer on.

- Code: `FlightViewModel.ReplanRendezvousAsync`

## Sources

- Howard D. Curtis, *Orbital Mechanics for Engineering Students* (Elsevier) — the reference for most formulas here
- Bate, Mueller & White, *Fundamentals of Astrodynamics* (Dover) — alternative derivations
- [KSP wiki](https://wiki.kerbalspaceprogram.com) — body data (`<Body>/Param` pages), part data
- [Eric Meyer — Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) — body constants, resonant-orbit method (MIT License)
- KSP Δv map 1.0.4 by /u/swashlebucky, based on Kowgan's map (CC BY-NC-SA 4.0) — mission leg values
- [alexmoon — KSP Launch Window Planner](https://alexmoon.github.io/ksp/) — porkchop approach for KSP
- Wikipedia articles linked in each section
