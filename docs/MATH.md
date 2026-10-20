# KSP Flight Planner — Math Guide

Every formula the app uses, where it lives in the code, where it comes from, and a worked example you can do yourself with a calculator. Examples use stock Kerbin unless noted.

> Tip: work in metres, seconds and m/s. Altitudes in KSP are measured from the surface, but almost every formula wants the radius r measured from the body's centre: r = R + altitude.

Each section has the formulas, a worked example, the code that implements them and a source. References like §2.1 point to sections of this guide; book references are written without § (Curtis 6.2 is section 6.2 of Curtis).

## Contents

- **1. Foundations** — 1.1 Constants and body data; 1.2 Circular orbits: speed and period; 1.3 Vis-viva: speed anywhere on an orbit; 1.4 Where things are at a given time: Kepler's equation; 1.5 Orbital elements from position and velocity; 1.6 Sphere of influence; 1.7 Atmosphere; 1.8 Maneuver-node axes; 1.9 Surface positions: latitude, longitude and distance
- **2. Changing your orbit around one body** — 2.1 Hohmann transfer: changing circular orbit altitude; 2.2 Elliptical orbits; 2.3 Plane changes; 2.4 Synchronous (stationary) orbit; 2.5 Deorbiting; 2.6 Landing at a chosen spot; 2.7 Launching into an inclined orbit; 2.8 Quick burns from where you are; 2.9 Landing at a spot from where you are; 2.10 Changing the inclination from where you are
- **3. Going to another planet** — 3.1 Escaping a body and arriving at another (hyperbolic burns); 3.2 Interplanetary Hohmann: phase angle, timing, v∞; 3.3 Ejection angle: where in your parking orbit to burn; 3.4 Real launch windows: Lambert's problem and porkchop plots; 3.5 Orbits around the Sun (Kerbol); 3.6 Transfers to custom locations; 3.7 Between a planet and its moons: transfer, course correction and capture; 3.8 Trips anywhere: routes, launch windows and options; 3.9 Arriving in a chosen inclination (a polar orbit at the Mun)
- **4. Meeting a craft in orbit (rendezvous)** — 4.1 How a rendezvous is planned; 4.2 Matching planes; 4.3 Phase angle, waiting, and phasing orbits; 4.4 The intercept: Lambert's problem again; 4.5 When and where to launch; 4.6 Starting from a craft around another body
- **5. Deploying a satellite constellation** — 5.1 Resonant orbits for satellite constellations; 5.2 Burn times and node values
- **6. Budgeting a mission** — 6.1 The Δv map and mission budgets; 6.2 Leg tools: replacing map values with a planned leg; 6.3 Transfer legs: a chosen launch window; 6.4 Deploy steps: a constellation released at a stop; 6.5 Rendezvous legs: meeting a placed craft
- **7. Your rocket: Δv and TWR** — 7.1 A vessel's remaining Δv (rocket equation); 7.2 A saved design's stage-by-stage Δv; 7.3 Does a design have enough Δv for the mission?; 7.4 TWR at every landing and lift-off; 7.5 A design taken apart: mass, cost and what each stage drops; 7.6 Which engine for a stage? (engine chart); 7.7 Antenna range and signal (CommNet planner); 7.8 Power through the longest night (power budget)
- **8. Working with your save and the live game** — 8.1 Importing vessels from a KSP save; 8.2 Following the live game (kRPC); 8.3 Flying a plan: how far off it you are; 8.4 Re-planning a rendezvous

**Where to look for…**

- Raising, lowering or reshaping an orbit: §2.1, §2.2
- A single burn from where your craft is now (circularize, change Ap/Pe): §2.8
- A plane change or an inclined target: §2.3, §4.2
- A polar or other inclined orbit straight from the launch pad: §2.7
- A polar (or any inclined) orbit around another body, set on the way there: §3.9; turning the orbit you're in: §2.10
- A trip to another planet, and its launch window: §3.2, §3.4
- Burns from your orbit to a moon (Mun, Minmus) and into orbit there, or back to the planet: §3.7
- Burns from where you are to anywhere else (Duna, Mun → Minmus, Mun → Duna), with a choice of windows: §3.8
- Landing at a crater, flag or waypoint: §2.6, §1.9; flying it from your real orbit: §2.9
- A rendezvous or docking: §4.1
- A relay network / constellation: §5.1, §5.2
- Why a mission's legs show a given Δv: §6.1, §6.2
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

**Your game's values over the wiki's.** The values above ship with the app (the wiki is the baseline). Once a body has been through KSP › Bake Surface Data, the stock body's values are compared with what your game reported, using the relative difference

```
difference = |game − wiki| / |wiki|
```

and the game's value is used when the difference is within a threshold: **5%** for constants the game defines exactly (sidereal day, radius, μ) and **15%** for sampled values (highest point, pressure at 0 m), since a 1° grid alone can be that far off. Beyond the threshold, the wiki value is kept and the Locations pane flags the difference (⚠). Two refinements:
- **Highest point:** the grid steps over sharp summits, so it only ever reads low. Within the threshold the *higher* of the two is used, the safe side for parking orbits and terrain warnings.
- **Pressure:** the wiki's baseline is its pressure table where there is one. When the game's value is used, its table (a row every km) replaces the wiki's.

Worked examples (a real 1° bake): Tylo's grid peak is 12,695 m against the wiki's 11,290 m: |12,695 − 11,290| / 11,290 = **12.4%**, within 15%, so the higher 12,695 m is used. The Mun's 6,852 m vs 7,061 m is 3.0%, within, and the wiki's higher 7,061 m stays. Eeloo's 1,245 m vs 3,874 m is **67.9%**, beyond, so 3,874 m is kept and flagged. Jool's pressure at 0 m is 5,066.25 kPa in both the game and the wiki's table (0%), so the game's table is used.

- Code: `BakeMerge.Apply` (applied to the stock system in `BodyRepository`); tested in `SurfaceBakeTests`

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

**Hyperbolas.** A craft faster than escape speed (leaving a body, or flying past a moon it has just reached) is on a hyperbola: a < 0 and e > 1. The same steps work with the hyperbolic anomaly H, and M no longer wraps round:

```
n = √(μ / |a|³)
M = e·sinh H − H                    solve for H (Newton's method, starting at asinh(M/e))
ν = 2·atan( √((e+1)/(e−1)) · tanh(H/2) )
r = a·(1 − e·cosh H)                (positive, since a < 0)
```

M < 0 is on the way in, M = 0 is the periapsis, and there's only one periapsis: at t = epoch − M₀/n.

- Code: `Kepler.StateAt`, `Kepler.SolveEccentricAnomaly`, `Kepler.SolveHyperbolicAnomaly`, `Kepler.NextPeriapsisUt`
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
                                        (hyperbola: H = 2·atanh(√((e−1)/(e+1))·tan(ν/2)), M = e·sinh H − H)
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

KSP's pressure curves are custom: the scale height changes with altitude. For the stock bodies with air (Kerbin, Eve, Duna, Jool, Laythe), the app stores the KSP wiki's pressure-vs-altitude table and reads between its rows. Pressure falls roughly exponentially between two rows, so the app interpolates the *logarithm* of pressure. The last row (top of the atmosphere) is 0 Pa, where a logarithm doesn't work, so that last step is a straight line:

```
between rows (h₁, p₁) and (h₂, p₂):
f = (h − h₁) / (h₂ − h₁)
p(h) = p₁ · (p₂ / p₁)^f          when both pressures are above 0
p(h) = p₁ + f · (p₂ − p₁)        when one of them is 0 (the top of the atmosphere)
```

Without a table (non-stock systems), the classic exponential model with the published scale height H is used:

```
p(h) = p₀ · e^(−h / H)
```

Worked examples —
- Duna at 6,250 m, halfway between the 5,000 m row (3,774 Pa) and the 7,500 m row (2,663 Pa): f = 0.5, p = 3,774 × (2,663 / 3,774)^0.5 = √(3,774 × 2,663) = **3,170.2 Pa = 0.03129 atm**. The scale-height model gives 0.0667 × e^(−6,250 / 5,700) = 0.0223 atm, almost 30% low.
- Duna at 45 km, between 40 km (4.918 Pa) and 50 km (0 Pa): p = 4.918 + 0.5 × (0 − 4.918) = **2.459 Pa**.
- Kerbin at 10 km: the table row says **0.177 atm**; the scale-height model gives 1 × e^(−10,000 / 5,600) = 0.168 atm.

The tables also give each air's molar mass M and adiabatic index γ (Kerbin: 28.9644 g/mol, 1.40). These are kept for the future descent simulation: density ρ = p·M / (R·T) with R = 8.314 J/(mol·K), and the speed of sound a = √(γ·R·T / M).

- Code: `Atmosphere.PressureAtmAt`; tested in `DatabaseTests.Pressure_comes_from_the_wiki_table_when_present`
- Data: `tools/wiki/scrape_wiki.py` pulls the tables; `docs/WIKI-COMPARISON.md` lists them
- Source: KSP wiki body pages (pressure tables, molar mass, γ) and `<Body>/Param` (sea-level pressure, scale height); [Scale height (Wikipedia)](https://en.wikipedia.org/wiki/Scale_height)

> **Known conflict:** Jool's wiki table says 50 atm at 0 m, but its `/Param` page says 15 atm. Both are kept until a reading from the live game (kRPC) settles it.

### 1.8 Maneuver-node axes

KSP's maneuver node has three handles. For a craft at position r with velocity v:

```
prograde = v / |v|
normal   = (r × v) / |r × v|          "up" out of the orbit plane (purple triangle)
radial   = prograde × normal          in the plane, at right angles to prograde, pointing away from the body (cyan)
```

Each burn's Δv vector is split as (Δv·prograde, Δv·normal, Δv·radial). These are the numbers to type into the node editor in **Exact nodes** mode. On the Flight pane, **Send to KSP** puts the same three numbers and the node time on the craft you're flying (kRPC `Control.AddNode(ut, prograde, normal, radial)`, which uses the same axes). Later nodes chain off the earlier ones in KSP just as they do in the plan.

- Code: `NodeBurn.From`; tested in `RendezvousTests`

### 1.9 Surface positions: latitude, longitude and distance

KSP gives surface positions as latitude φ (north positive) and longitude λ (east positive), in degrees. Landmarks from the wiki, flags in your save and KerbNet waypoints all use this form. Kerbin's launch pad, for example, is at φ = −0.1027°, λ = −74.5754°.

As a direction from the body's centre, in the body's own (rotating) frame:

```
x = cos φ · cos λ
y = cos φ · sin λ
z = sin φ                (z = the north pole; multiply by R + altitude for the position)
```

Distance along the surface between two points (the great-circle or "haversine" formula), with Δφ = φ₂ − φ₁ and Δλ = λ₂ − λ₁:

```
h = sin²(Δφ / 2) + cos φ₁ · cos φ₂ · sin²(Δλ / 2)
d = 2R · asin(√h)
```

Worked example — on the Mun (R = 200,000 m), from the Armstrong memorial (0.7017° N, 22.7460° E) to the Kerbol arch (12.4508° N, 39.1959° E): Δφ = 11.7491°, Δλ = 16.4499°. h = sin²(5.8746°) + cos 0.7017° × cos 12.4508° × sin²(8.2250°) = 0.010476 + 0.019983 = 0.030459, so d = 400,000 × asin(0.174525) = **70.17 km**. The same formula measures how far a landing ended up from its target.

- Code: `Landing.SurfaceDistance`; tested in `LandingTests`
- Source: [Haversine formula (Wikipedia)](https://en.wikipedia.org/wiki/Haversine_formula)

**Heights from the surface bake.** KSP › Bake Surface Data asks the running game (kRPC `SurfaceHeight`, `BiomeAt`) for the terrain height and biome at every grid point: one row per step from 90° S to 90° N, columns from 180° W eastward, wrapping round at 180°. A spot between grid points takes its height from the four around it (bilinear interpolation). With the spot a fraction f_lon of the way east and f_lat of the way north inside its grid cell:

```
south = h_SW + f_lon · (h_SE − h_SW)
north = h_NW + f_lon · (h_NE − h_NW)
h     = south + f_lat · (north − south)
```

Worked example — grid points 1° apart: 1,000 m (SW, 10° N 20° E), 1,200 m (SE, 10° N 21° E), 1,100 m (NW, 11° N 20° E) and 1,500 m (NE). The spot 10.6° N 20.25° E has f_lon = 0.25 and f_lat = 0.6: south = 1,000 + 0.25 × 200 = 1,050, north = 1,100 + 0.25 × 400 = 1,200, h = 1,050 + 0.6 × 150 = **1,140 m**. A biome is taken from the nearest grid point instead, since biomes can't be averaged.

To walk along the ground from a spot, the great-circle destination formula gives the point a distance d away on bearing θ (from north):

```
φ₂ = asin(sin φ₁ · cos(d/R) + cos φ₁ · sin(d/R) · cos θ)
λ₂ = λ₁ + atan2(sin θ · sin(d/R) · cos φ₁, cos(d/R) − sin φ₁ · sin φ₂)
```

- Code: `SurfaceBake.HeightAt`, `SurfaceBake.BiomeAt`, `Landing.Destination`; tested in `SurfaceBakeTests`
- Source: [Bilinear interpolation (Wikipedia)](https://en.wikipedia.org/wiki/Bilinear_interpolation); destination point: [Movable Type, "Calculate distance, bearing and more between latitude/longitude points"](https://www.movable-type.co.uk/scripts/latlong.html)

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

The Δv map's 80 km → keostationary (2,863 km) edge is this Hohmann transfer, ≈ 1,099 m/s, rounded to 1,100 (§6.1).

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

**Inclination** is charged as a plane change (§2.3) at the target's apoapsis, where you are slowest. For the orbit above at 10°: 2 × 759.1 × sin 5° = **132 m/s** — doing the same 10° in the 80 km orbit would cost 397 m/s. A mission adds this when "Include worst-case plane changes" is on (Mission pane, Options).

**Validation** checks only the independent values: periapsis above the atmosphere (or the highest terrain on airless bodies), apoapsis inside the sphere of influence, apoapsis ≥ periapsis, inclination 0–180°.

- Code: `OrbitShape`, `OrbitMath.TransferToEllipse`, `OrbitMath.PlaneChangeDv`, `CustomLocations.Analyze`, `CustomLocations.Validate`
- Source: Curtis 6.2–6.4 (Hohmann and general coplanar transfers) and 6.10 (plane changes)

### 2.3 Plane changes

Changing inclination by Δi at speed v costs:

```
Δv = 2 · v · sin(Δi / 2)
```

It's cheapest where you're slowest (high up, or at apoapsis). Worked example — matching Minmus's 6° inclination while still in 80 km Kerbin orbit: 2 × 2,278.9 × sin 3° = **238.5 m/s**. The Δv map charges the worst case instead: the whole 6° at the fastest point of the transfer to Minmus, its 3,199.8 m/s periapsis speed, so 2 × 3,199.8 × sin 3° = 334.9, shown as **330 m/s** (§6.1). The Mission pane's "Include worst-case plane changes" adds those map values.

- Code: `OrbitMath.PlaneChangeDv`; map values from `DeltaVMapGenerator` (`DvMapNode.PlaneChangeDv`)
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

### 2.6 Landing at a chosen spot

On the Mission pane, a surface stop reached from an orbit offers **Land at a spot**: a wiki landmark, a flag or landed craft from your save, a KerbNet waypoint, or a typed latitude/longitude. This section is how that landing is worked out. It is exact enough for **airless bodies**, where its deorbit and braking burns replace the Δv map's landing value. On bodies with air, drag shortens the descent by an amount that depends on the craft, so the map's value is kept and the spot is marked as a **rough estimate** until the app models drag.

Inputs: the site's latitude φ, longitude λ and ground height h_s (§1.9); the craft's circular orbit radius r₀, inclination i and longitude of ascending node Ω; the body's μ, radius R, sidereal rotation period T_rot and its rotation angle θ₀ at UT 0. θ₀ is not on the wiki; it will come from the live game (kRPC `CelestialBody.InitialRotation`), so the app doesn't time the burn yet (step 3 below gives the method).

The app's choices, where the method leaves one open:
- The orbit's inclination is planned **1° above** the site's latitude, so the ground track crosses the latitude instead of just grazing it. Turning an equatorial orbit to it costs 2·v₀·sin(i/2) (§2.3). That goes in the leg's plane-change column, counted when plane changes are on.
- The descent's low point is **1,000 m** above the site's ground. The ground height comes from the spot itself when it has one (flags and landed craft record it as `alt` in the save), otherwise from the surface bake (§1.9). Without either, the datum (0 m) is assumed and the plan says so.
- **The approach.** With a surface bake, the app walks back from the site along the ground track over the braking distance d (step 5), every 500 m, and reads the terrain (§1.9). The northbound pass arrives on heading az = asin(cos H) east of north (cos H from step 5), the southbound one on 180° − az. The lower of the two is suggested, and the plan warns if that terrain reaches within 100 m of the descent's low point. Without a bake it can only compare the low point with the body's highest peak.
- The lander's thrust is entered as its **TWR at the body's surface** (default 2), so a_T = TWR × μ/R².
- r₀ is the low orbit the leg comes down from (the Δv map's, e.g. 10 km on the Mun).

**Step 1 — Can the orbit pass over the site?** A ground track never goes further north or south than the inclination, so the orbit must satisfy

```
|φ| ≤ i ≤ 180° − |φ|
```

If not, plane-change first (§2.3). An equatorial orbit only reaches sites near the equator. A site at 80° N needs a nearly polar orbit.

**Step 2 — Where on the orbit is the site's latitude?** Measured from the ascending node (the "argument of latitude" u):

```
sin u = sin φ / sin i          u on the northbound pass, 180° − u on the southbound pass
Δα = atan2(cos i · sin u, cos u)       how far east of the ascending node that point lies, in space
```

**Step 3 — When is the site under that point?** The body turns at ω = 360° / T_rot, so a surface longitude λ sits under the space direction α at time t when

```
λ = α − (θ₀ + ω · t)      (mod 360°), with α = Ω + Δα
```

The craft is at u once per orbit, every period P. Between passes the body turns ω·P, so **the ground track moves west by 360° · P / T_rot each orbit**. Pick the pass where the site is closest. It is never more than half a shift off, and a small normal burn on the way down removes the rest.

**Step 4 — The deorbit burn.** Burn retrograde half an orbit before the site, so the new periapsis r_p = R + h_s + clearance is reached right over it (Hohmann, §2.1):

```
a      = (r₀ + r_p) / 2
Δv₁    = √(μ / r₀) − √(μ · (2/r₀ − 1/a))
t_desc = π · √(a³ / μ)                     time from the burn to periapsis
```

The site keeps moving east during the descent, by ω · t_desc. So aim at where the site will be when you arrive, not where it is when you burn. Keep the periapsis above the highest terrain along the last part of the path; the Mun's highest point is 7,061 m.

**Step 5 — The braking burn.** At periapsis the craft moves at v_p = √(μ · (2/r_p − 1/a)). The ground under it moves east at v_s = ω · r_p · cos φ. The craft's velocity points cos H = cos i / cos φ of the way east, so the speed relative to the ground is

```
v_rel = √(v_p² − 2 · v_p · v_s · cos H + v_s²)
```

To brake without falling, part of the engine's acceleration a_T has to hold the craft up against gravity g = μ / r_p². That leaves a_h = √(a_T² − g²) to slow it down. (This is conservative: while still fast, the craft is partly "in orbit", so it needs less than g.) Braking takes

```
t_b = v_rel / a_h
d   = v_rel² / (2 · a_h)        ground distance covered while braking: start this far before the site
Δv₂ = a_T · t_b                 includes the gravity loss
```

Worked example — the Mun, from a 30 km circular orbit inclined 15°, down to the Kerbol arch (12.4508° N, 39.1959° E), with periapsis 3 km above the datum and a lander accelerating at a_T = 5 m/s²:
- Mun: μ = 6.5138 × 10¹⁰ m³/s², R = 200,000 m, T_rot = 138,984.4 s.
- Step 1: 12.45° ≤ 15° ≤ 167.55°. The orbit reaches the site.
- Step 2: sin u = sin 12.4508° / sin 15° = 0.8330, so u = **56.41°** (northbound) or 123.59° (southbound). Δα = atan2(cos 15° × sin 56.41°, cos 56.41°) = **55.49°** east of the ascending node.
- Step 3: P = 2π √(230,000³ / μ) = 2,715.5 s. The track moves west **7.03° per orbit**, which is 24.6 km at the equator.
- Step 4: r₀ = 230,000 m, r_p = 203,000 m, a = 216,500 m. Δv₁ = 532.18 − 515.32 = **16.86 m/s**. t_desc = **1,240 s**, during which the Mun turns 3.21°: about 10.9 km at this latitude.
- Step 5: v_p = 583.86 m/s, v_s = 8.96 m/s, cos H = cos 15° / cos 12.4508° = 0.9892, so v_rel = **574.99 m/s**. g = 1.581 m/s², a_h = √(5² − 1.581²) = 4.744 m/s². Braking takes t_b = **121.2 s** over d = **34.8 km**, costing Δv₂ = **606.1 m/s**. That is 31.1 m/s (5.4%) more than v_rel, because of the gravity loss.
- Total from orbit: 16.9 + 606.1 = **623 m/s**. Compare the Δv map's 580 m/s from its 10 km low orbit, which assumes no particular spot.

Fly mode times the deorbit burn from the craft's real orbit (step 3): see §2.9. Still to come: a drag model for bodies with air.

- Code: `Landing.Plan` (steps 1–5, plus the approach with `Landing.ApproachPeak`), `Landing.Targets` (landmarks, flags, landed craft, waypoints), `LegTools.ApplyLanding` (replaces the map value); tested in `LandingTests` (this worked example is `Mun_landing_matches_the_worked_example`)
- Source: argument of latitude and ground tracks: Curtis chapter 4 (orbits in three dimensions); Hohmann and vis-viva: §2.1, §1.3; braking with gravity loss: the constant-altitude braking approximation

### 2.7 Launching into an inclined orbit

On the Mission pane, a stop that a launch from KSC arrives at (Kerbin's low orbit, or an orbit above it reached straight from the pad) has an **Inclination** box under its altitude: type an inclination, or pick Equatorial (0°), Polar (90°) or Retrograde (180°) from the list next to it (it shows Custom for any other value). Equatorial is the default and keeps the Δv map's launch value as it is. The Δv map's launch value assumes you fly **due east**, so Kerbin's spin gives you a free 175 m/s. Any other heading gets less of that help, and this section works out how much more the launch costs. It also gives the heading to fly. Choosing the plane at launch is far cheaper than turning in orbit (§2.3): a polar orbit costs **+182 m/s** at launch, against **3,223 m/s** to turn an equatorial 80 km orbit to polar.

**Inclination vs. heading.** The inclination i (0° to 180°) is the angle between the orbit's plane and the equator, with the direction of travel included. Below 90° the orbit goes east like Kerbin turns (prograde). 90° goes over the poles. Above 90° it goes west (retrograde), and 180° is the equator flown westward. The navball **heading** is a compass direction from the pad: 0° (= 360°) is north, 90° east, 180° south, 270° west. A polar orbit is flown at a heading near **0° or 180°**: heading north and heading south give the same orbit plane, just crossing the pad's latitude the other way. The two numbers are different scales: inclination 90° is heading 0° or 180°, and heading 90° (east) is inclination 0°.

Inputs: the launch site's latitude φ (the wiki's **KSC Launch Pad** landmark, −0.1027°); Kerbin's μ, radius R and sidereal rotation period T_rot; the circular orbit radius r = R + altitude the launch goes into (the map's low orbit, 80 km); and the inclination i.

**Step 1 — Can a launch reach it?** A launch from latitude φ goes straight into any inclination with

```
|φ| ≤ i ≤ 180° − |φ|
```

Lower or higher, launch as close as you can (due east or due west) and turn the rest in orbit at the orbit's speed: 2·v·sin(Δi/2) (§2.3). The app adds that turn to the launch too. From the KSC this only matters within 0.1027° of the equator and costs about 4 m/s.

**Step 2 — Which way does the orbit point at the pad?** The launch azimuth az (from north, toward east) comes from spherical trigonometry:

```
sin(az) = cos i / cos φ        az on the northbound pass, 180° − az on the southbound pass
```

The orbit's velocity makes the angle β = 90° − az with east, so cos β = cos i / cos φ. For a retrograde orbit (i > 90°) cos i is negative, so az is negative: the orbit heads west of north (or west of south).

**Step 3 — How much speed the rocket has to add.** The orbit needs speed v = √(μ / r) in the fixed (inertial) frame. Sitting on the pad, the rocket already moves east at the ground's speed v_rot = 2π·R·cos φ / T_rot. What the engines have to supply is the difference of the two velocities (law of cosines):

```
v_rel = √(v² + v_rot² − 2 · v · v_rot · cos β)
```

Due east (β = 0) this is v − v_rot, which is what the Δv map assumes. So the launch costs

```
extra Δv = v_rel − (v − v_rot)
```

on top of the map's value. Gravity and drag losses don't depend much on the heading, so they stay in the map's number.

**Step 4 — The heading to fly.** Point the rocket along that difference, not along az. Split the orbit's velocity into east and north parts and take the spin off the east part:

```
heading = atan2(v · sin(az) − v_rot, v · cos(az))        (navball degrees, 0°–360°)
```

The spin pulls the heading a little west of the azimuth. That's why the polar heading is 355.6°, not 0°. At the lowest inclination the site reaches (|φ|, due east) and the highest (180° − |φ|, due west), the orbit only touches the pad's latitude, so northbound and southbound are the same launch: one heading, 90° or 270°.

Worked example — a polar 80 km Kerbin orbit from the KSC Launch Pad:
- Kerbin: μ = 3.5316 × 10¹² m³/s², R = 600,000 m, T_rot = 21,549.425 s; φ = −0.1027°.
- Step 1: 0.1027° ≤ 90° ≤ 179.8973°. The launch reaches it directly.
- Step 2: sin(az) = cos 90° / cos 0.1027° = 0, so az = **0°** (north) or 180° (south), and cos β = 0.
- Step 3: v = √(3.5316 × 10¹² / 680,000) = **2,278.93 m/s**. v_rot = 2π × 600,000 × cos 0.1027° / 21,549.425 = **174.94 m/s**. v_rel = √(2,278.93² + 174.94²) = **2,285.64 m/s**. Due east needs 2,278.93 − 174.94 = 2,103.99 m/s, so the extra is 2,285.64 − 2,103.99 = **+181.6 m/s**.
- Step 4: heading = atan2(−174.94, 2,278.93) = −4.39°, so fly **355.6°** going north, or **184.4°** going south.

The same 80 km orbit at other inclinations, against turning an equatorial orbit in place (§2.3):

| Inclination | Launch extra | Heading north / south | Turning in orbit instead |
|---|---|---|---|
| 28.5° | +22.8 m/s | 59.3° / 120.7° | 1,121.9 m/s |
| 45° | +54.8 m/s | 41.7° / 138.3° | 1,744.2 m/s |
| 90° (polar) | +181.6 m/s | 355.6° / 184.4° | 3,222.9 m/s |
| 135° | +301.8 m/s | 312.1° / 227.9° | 4,210.9 m/s |
| 180° (retrograde) | +349.9 m/s + 4.1 m/s turn | 270° (due west, one heading) | 4,557.9 m/s |

**Later orbits around Kerbin.** A custom location's plane change (§2.3) is the worst case from the equator, 2·v_a·sin(i/2) at its apoapsis speed v_a. After an inclined launch the craft is already in an inclined plane, so the app measures each later custom orbit's plane change **from the orbit the craft is in**: 2·v_a·sin(|i_location − i_now|/2). A launch to 90° and then a stop at a 90° station charges no plane change. A relay at 100° next is a 10° turn. Arriving at a location puts the craft in its plane for the next one. This stops when the mission lands or leaves Kerbin. It assumes the planes also line up around the planet (same longitude of ascending node), which you get by timing the launch (§4.5). A rendezvous leg (§6.5) plans its own launch into the target's plane, so it doesn't use this tool.

- Code: `LaunchInclination.Plan` (steps 1–4), `LegTools.FindLaunch`, `LegTools.ApplyLaunch` (adds the extra to the launch step), `LegTools.MeasurePlaneChanges` (later custom orbits); tested in `LaunchInclinationTests` (this worked example is `Polar_launch_matches_the_worked_example`, the table `Launching_into_the_plane_is_far_cheaper_than_turning_in_orbit`)
- Source: launch azimuth sin(az) = cos i / cos φ and the rotating-launch-site velocity ([Braeunig, Orbital Mechanics](http://www.braeunig.us/space/orbmech.htm)); Curtis chapter 4 (orbits in three dimensions); KSC Launch Pad coordinates from the KSP wiki

### 2.8 Quick burns from where you are

The **From here** pane in Fly mode plans one burn from the live orbit of the craft you're flying, with no mission. Each burn is at an apsis, where the velocity is horizontal, so it's purely prograde or retrograde. The new speed comes straight from vis-viva (§1.3):

```
circularize at r:                v_new = √(μ / r)
move the far apsis to r₁,
burning at the near one r₀:      v_new = √(2μ·r₁ / (r₀·(r₀ + r₁)))        (r₀ stays put)
Δv (prograde) = v_new − v_now
```

To change the apoapsis it burns at the periapsis, and to change the periapsis it burns at the apoapsis. Each uses the **next** apsis, however soon it is, and the pane warns when that's under two minutes away. Skipping a close apsis would put the burn a whole orbit later, which a craft just off the pad never reaches (its periapsis is underground) and which puts a released relay in the wrong slot (§5.2). An orbit with e < 0.002 has no apsis worth waiting for, so the burn is two minutes from now.

If the periapsis is in the atmosphere (or below the ground on an airless body), the craft can't wait for a periapsis:

- *Circularize at periapsis* and *Change the apoapsis* (which burns there) are refused.
- If the next periapsis comes before the apoapsis you asked for, the craft is falling: it's just past the apoapsis after a launch. *Circularize* then burns 30 s from now (`FallingLeadS`) and turns the velocity horizontal at the circular speed for that height: Δv = √(μ/r)·ĥ − v, where ĥ is the horizontal direction in the orbit's plane. The radial part cancels the fall. *Change the periapsis* is refused, with a note to circularize instead.

Worked example: the craft is 40 s past an 80 km apoapsis with a periapsis 300 km underground. The burn 30 s later is at 77.6 km: **+484.6 m/s prograde and +150.9 m/s radial** (507.5 m/s in all), which leaves a 77.6 km circular orbit (`Just_past_the_apoapsis_and_falling_it_circularizes_now`). An apoapsis below the periapsis, or the reverse, is refused with a note to set the other one. *Change the inclination* is §2.10, *Match planes* is §4.2, *Rendezvous* is the whole §4.1 plan against the craft you've targeted in KSP, and *Deorbit* is §2.5.

**Capturing at a moon.** Once a craft enters a moon's sphere of influence (§1.6), its path around the moon is a hyperbola (§1.4). *Circularize at periapsis* is then the capture burn, at the pass's only periapsis: v_new = √(μ/r_p) as above, with v_now from vis-viva, where a < 0. *Change the apoapsis* burns there too and captures into an ellipse, which is cheaper. A path with no apoapsis can't do the rest (circularize at apoapsis, change the periapsis, match planes, rendezvous or deorbit), and the pane says to capture first. Once the periapsis has passed, the craft is on its way out and there's nothing to burn at.

Worked example — Kerbin (μ = 3.5316×10¹² m³/s², R = 600 km), from an 80 km circular orbit:

1. Raise the apoapsis to 250 km: r₀ = 680,000, r₁ = 850,000. v_now = √(μ/r₀) = 2,278.93 m/s and v_new = 2,402.20 m/s, so the burn is **+123.27 m/s** prograde. That's the first Hohmann burn of §2.1, as it should be.
2. Circularize at the new 250 km apoapsis: v_a = √(2μ·680,000 / (850,000 × 1,530,000)) = 1,921.76 m/s and √(μ/850,000) = 2,038.34 m/s, so **+116.57 m/s**.

- Code: `QuickBurns.Circularize`, `QuickBurns.SetApsis`, `QuickBurns.ApsisUt`, `QuickBurns.ClearOfAtmosphere`; the pane is `FromHereViewModel`; tested in `QuickBurnsTests` (step 1 is `Raising_the_apoapsis_from_low_orbit_is_the_first_hohmann_burn`) and, for capture, `HopsTests.Inside_the_moons_sphere_circularize_at_periapsis_captures`
- Source: vis-viva (§1.3); Curtis 6.2–6.3 (coplanar maneuvers at an apsis)

### 2.9 Landing at a spot from where you are

§2.6 prices a landing from an idealised circular orbit. Fly mode flies it from the craft's **real** orbit at a real time: **Land at a spot** in the From here pane, a kept "Land at …" flight in the Flight pane, or **Fly this landing** on a mission's landing spot. It is §2.6 with step 3 (timing) done, plus a small burn on the way down to put the track exactly over the spot.

Inputs: the craft's orbit (any closed orbit clear of the ground and air), the spot and lander as in §2.6, and the body's rotation angle at UT 0, θ₀. θ₀ comes from the surface bake when the body has one (§1.9), else from the live game (kRPC `CelestialBody.InitialRotation`), else, for Kerbin only, the stock 90°. Without any of these the landing can't be timed.

**Where the spot is, in the frame orbits are measured in.** The body turns at ω = 2π / T_rot, so at time t the spot's direction is

```
λ_I(t) = λ + θ₀ + ω·t                      (the same convention as the launch site in §4.5)
Ŝ(t)   = (cos φ · cos λ_I, cos φ · sin λ_I, sin φ)
```

**Step 1 — Reach the latitude.** The track reaches latitude i (or 180° − i for a retrograde orbit). If that's less than |φ| + 0.5°, the first burn turns the orbit to i' = |φ| + 1° (§2.6's margin) at the next equator crossing, where turning the velocity about the radius changes the inclination and nothing else: Δv = 2·v·sin(|i' − i| / 2) (§2.3).

**Step 2 — One landing per pass.** For each orbit k over one turn of the body (so the spot passes under both sides of the orbit), the landing time T is found by repeating, until it moves less than half a second:

```
u_S    = angle of Ŝ(T) in the orbit's plane, from the ascending node
u_low  = u_S − d / (R + h_s)              the low point, a braking distance d short of the spot
burn at the craft's next pass of u_low − 180°, at radius r_b:
  v_new = √(μ · (2/r_b − 1/a)) horizontal,  a = (r_b + r_p) / 2      (§2.6 step 4, for any orbit)
t_low  = t_burn + π · √(a³ / μ)
v_rel  = |v_low − ω ẑ × r_low|            speed over the ground at the low point
t_b, d, Δv₂ from v_rel as in §2.6 step 5
T      = t_low + t_b
```

Passes whose deorbit burn would come before the lead time are dropped.

**Step 3 — How far to the side.** The spot is off the orbit's plane (normal n̂) by the angle χ = asin(n̂ · Ŝ(T)), a sideways miss of |χ|·(R + h_s). Successive passes move it by the track shift of §2.6 step 3, so somewhere in a turn of the body there is a pass with almost none.

**Step 4 — Swing the track over the spot.** A plane change moves the points 90° away from it the most and the point 180° away not at all, so the fix is a quarter orbit before the spot, at u_S − 90° on the descent orbit. There the new plane must hold both the craft and the spot: n̂' = (r_c × Ŝ(T)) / |r_c × Ŝ(T)|, on the same side as n̂. The craft keeps its radial speed v_r and its horizontal speed v_h, turned into the new plane:

```
v' = v_r · r̂_c + v_h · (n̂' × r̂_c)          Δv₃ = |v' − v_c|  (normal, ≈ v_h · |χ|)
```

It's left out when the miss is under 200 m.

**Step 5 — Brake.** As §2.6 step 5, from the low point. The node is placed half the burn time after the low point, so a pilot starting half the burn early (KSP's usual habit) starts at the low point. Its Δv is Δv₂, gravity loss included. Hold surface retrograde and keep the vertical speed near zero. On a body with air there's no braking burn (§2.6: the air and parachutes do it, and the spot is a rough estimate).

**Choosing a pass.** *Least Δv* is the pass with the smallest Δv₁ + Δv₃ + Δv₂. *Sooner* is the earliest pass costing no more than max(50 m/s, 10%) on top of it. When that costs under 5 m/s more, it's simply the one offered.

Worked example — the Mun from a 30 km circular orbit inclined 15° (Ω = 20°), to the Kerbol arch (12.4508° N, 39.1959° E, ground 2,000 m), lander a_T = 5 m/s², θ₀ = 37°, planned at UT 5,000,000 s with a 120 s lead (`LandingFlightsTests`):
- No tilt: 15° reaches 12.45° + 0.5°.
- The first pass comes in northbound and lands 2,628 s after planning. Deorbit at +1,266 s: **16.86 m/s** retrograde, as §2.6's step 4 (the same orbit and periapsis).
- The track passes **714 m** to the side, so at +2,003 s a **1.96 m/s** normal burn swings it over (≈ v_h·|χ|).
- Brake node at +2,567 s: **606.08 m/s**, as §2.6's step 5. In all **624.9 m/s**. A later pass with a 661 m miss costs 0.1 m/s less but lands 8 hours later, so only this one is offered.
- From an *equatorial* 30 km orbit instead: **Tilt the orbit to 13.5°** first (2 × 532.18 × sin 6.73° = **124.65 m/s**). *Least Δv* (748.1 m/s) then waits for a pass 228 m off (a 0.63 m/s fix) and lands about 19 hours later; *Sooner* lands 3.8 hours earlier with an 18.9 km miss and a **51.89 m/s** fix (799.4 m/s).

- Code: `LandingFlights.Options` (steps 1–5), `LandingFlights.Plan` (a Landing flight), `LandingFlights.SiteDirection`; the panes are `FromHereViewModel` (Land at a spot), `FlightViewModel` (re-plan) and `MissionBuilderViewModel.FlyLanding`; θ₀ from `BodyRotation`. Tested in `LandingFlightsTests` (`Mun_landing_comes_down_on_the_spot` checks that the descent's plane holds the spot and its low point is a braking distance short of it)
- Source: §2.6 and its sources; plane change at a point 90° away: Curtis 6.10 (plane change maneuvers)

### 2.10 Changing the inclination from where you are

*Change the inclination* in the **From here** pane turns the orbit you're in to a new inclination to the body's equator, keeping its shape and its ascending node. It's §4.2's plane match with the new plane as the "target": one burn where the orbit crosses the equator (the line of nodes), turning the velocity into the new plane while keeping its speed and climb angle,

```
Δv = 2 · v_h · sin(Δi / 2)          v_h = the horizontal part of the speed there
```

Of the two crossings it takes the cheaper, which on an eccentric orbit is the one farther out (slower). An orbit lying in the equator crosses it everywhere, so the burn goes at the next apoapsis (two minutes from now on a round orbit), and that point becomes the ascending node.

Turning a low orbit a long way is expensive (§2.3), so a turn of more than 30° carries a note: raising the apoapsis first, turning there and lowering it again can cost less, and on the way to another body the inclination is better set in *Go to* (§3.9). A path that's passing the body (a hyperbola) is refused for the same reason.

Worked examples — Kerbin (μ = 3.5316×10¹² m³/s², R = 600 km):

1. A round 100 km equatorial orbit to polar: v = √(μ / 700,000) = 2,246.1 m/s, so Δv = 2 × 2,246.1 × sin 45° = **3,176.5 m/s**, two minutes from now (`Turning_a_round_equatorial_orbit_polar_costs_two_v_sin_45`). That's more than it took to get into orbit, which is why polar orbits are launched into (§2.7) or arrived in (§3.9).
2. A 100 × 2,000 km orbit inclined 30°, to 10°: the burn is at the crossing beyond the semi-major axis, where it's slower, and leaves the same periapsis, apoapsis and ascending node (`Changing_an_inclined_orbit_burns_at_the_higher_equator_crossing_and_keeps_its_shape`).

- Code: `QuickBurns.ChangeInclination` (using `Rendezvous.MatchPlane`); the pane is `FromHereViewModel`; tested in `QuickBurnsTests`
- Source: Curtis 6.10; [Orbital inclination change (Wikipedia)](https://en.wikipedia.org/wiki/Orbital_inclination_change)

## 3. Going to another planet

Leaving one body's sphere of influence and arriving at another: the burns, when to make them, and where to point.

### 3.1 Escaping a body and arriving at another (hyperbolic burns)

Far from a planet you have a leftover speed v∞ (hyperbolic excess speed). Burning at periapsis of a low parking orbit (radius r) to reach it costs:

```
Δv = √( v∞² + 2μ/r ) − √( μ/r )
```

The same formula gives the capture burn at the destination (circularizing at radius r). Escape speed is the special case v∞ = 0 measured from rest: v_esc = √(2μ/r).

Worked example — leaving 80 km Kerbin orbit for Duna (v∞ = 918.3 m/s, see §3.2):
Δv = √(918.3² + 2 × 3.5316e12 / 680,000) − 2,278.9 = **1,072 m/s** (the Δv map says 930 + 140 = 1,070: the same burn, split at the SOI edge and rounded; §6.1).

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

An orbit around the Sun is an interplanetary destination, so the planner attaches it where every other interplanetary trip starts: Kerbin's escape node (next to the Duna, Eve and Moho intercepts), not Kerbol's 610 km low orbit. A solar orbit only "belongs" to Duna if it is inside Duna's sphere of influence — then it's a Duna orbit (§2.1).

Steps — combine §2.1 (around the Sun), §3.1 (leaving Kerbin) and the map's 930 m/s low-Kerbin-orbit → escape edge:

```
r₁ = Kerbin's orbit radius,  r₂ = R_Kerbol + altitude,  a = (r₁ + r₂)/2      (around Kerbol, μ_sun)
v∞          = | √(μ_sun·(2/r₁ − 1/a)) − √(μ_sun/r₁) |       departure excess speed
ejection    = √(v∞² + 2μ_Kerbin/r_park) − √(μ_Kerbin/r_park)   from 80 km Kerbin orbit
circularize = | √(μ_sun/r₂) − √(μ_sun·(2/r₂ − 1/a)) |        no planet there, so no Oberth bonus
edge from Kerbin escape = (ejection − 930) + circularize
```

Worked example — "Eye of Kerbol", 10,000,000 km above Kerbol: r₁ = 13,599,840,256 m, r₂ = 261,600,000 + 10,000,000,000 = 10,261,600,000 m.

1. a = 11,930,720,128 m; Kerbin's speed √(μ_sun/r₁) = 9,284.5 m/s; transfer speed at r₁ = 8,610.6 m/s → v∞ = **673.9 m/s** (an inward transfer: you slow down relative to Kerbin)
2. Ejection from 80 km = √(673.9² + 2 × 3.5316e12/680,000) − 2,278.9 = **1,013.7 m/s** → beyond the map's 930: **84 m/s**
3. Circularize at r₂: √(μ_sun/r₂) − speed on the transfer at r₂ = **723.2 m/s**
4. Edge = 84 + 723 = **807 m/s**; flight time π√(a³/μ_sun) ≈ **175 Kerbin days**. Total from Kerbin's surface: 3,400 + 930 + 807 = 5,137 m/s.

Check: put the orbit at Duna's distance and step 2 gives 142 m/s — the map's own Duna intercept edge is 140 m/s.

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

### 3.7 Between a planet and its moons: transfer, course correction and capture

The **From here** pane's *Go to a moon or planet* plans the burns from the craft's live orbit to a round orbit at a height you choose. That's around one of the moons of the body you're orbiting, or, from a moon, back around its planet. It uses patched conics, as KSP flies it: only the planet pulls on the craft until it enters the moon's sphere of influence (§1.6), and only the moon pulls on it after that.

**Going out to a moon:**

1. **First guess: the cheapest intercept of the moon's centre.** As in a rendezvous (§4.4), Lambert's problem is solved over a grid of departure times (one synodic period of your orbit against the moon's) and flight times (0.6–1.4 × the Hohmann time), and the cheapest transfer plus arrival speed wins. This ignores the moon's gravity, so it would hit the moon dead centre.
2. **Fly it into the sphere of influence.** The path after the burn is followed until its distance to the moon drops below r_SOI. Each step is half the time the gap would take to close at the current closing speed, so the crossing can't be skipped, then it's halved back to within 0.01 s. At the crossing, the craft's position and velocity *relative to the moon* give its path there (§1.5): a hyperbola with periapsis r_p = a·(1 − e).
3. **Aim for the height.** The prograde part of the burn is tuned until the pass is at the asked height *and goes round the way the moon spins*. Seen from the north, the pass is prograde when (r × v)_z > 0. The quantity solved is the signed periapsis ±r_p − r_target. The sign flips with the direction of the pass, so the function runs smoothly through zero as the aim point sweeps across the moon. The search steps 0.25 m/s outward from the Lambert burn until the sign changes, then halves the step.
4. **Capture.** At the pass's periapsis, burn retrograde to the round orbit's speed (§2.8): Δv = √(μ_moon / r_p) − √(μ_moon·(2/r_p − 1/a)).

**A moon on a tilted orbit** (Minmus, 6°) is cheapest to reach when it's near the plane of your orbit. Then the transfer needs no plane change. When the two planes are more than 0.5° apart, twelve more departure windows are searched across half the moon's orbit (each half orbit has one crossing of the planes), and the cheapest wins. The pane says how long that waits and what leaving at the first chance would cost. *Leave at the first chance* skips the wait.

**Already on the way.** If the craft's path already reaches the moon's sphere, the first burn is a **course correction** two minutes from now. It's tried two ways, and the cheaper wins: tuning a plain prograde/retrograde burn (for a pass that's too close or too far), and tuning a re-aim at the moon's centre (for a miss out of the plane). A pass within 2 km of the asked height needs no correction, so only the capture is shown.

The capture burn is planned before the craft gets there. Once it's inside the moon's sphere, *Circularize at periapsis* plans it again from the real path. The burn's note says so.

Worked example — Mun (R = 200 km, μ = 6.5138×10¹⁰), from a 100 km circular equatorial orbit around Kerbin, asking for 30 km:

1. Hohmann check: r₁ = 700,000 m, r₂ = 12,000,000 m (the Mun's distance). √(μ/r₁) = 2,246.1 m/s and √(μ·(2/r₁ − 2/(r₁+r₂))) = 3,087.7 m/s, so about **842 m/s**.
2. The planner's transfer is **+837.7 m/s** prograde, with radial −0.1. It's a little less than the Hohmann figure because it only has to reach the moon's sphere. The pass is at **30.000 km**, prograde and equatorial.
3. Capture at 30 km: **−260.5 m/s** retrograde leaves a round 30 km orbit.
4. Burn the transfer 3 m/s too hard, and the pass would hit the Mun (periapsis −193 km). Planned again an hour later, it's a **10.4 m/s retrograde** correction, and the pass is back at 30 km.

Minmus (6° tilt) from 80 km at 20 km: the first chance costs **1,049.9 m/s**, with 440 m/s of it normal. Waiting **6 days 1 hour** (Kerbin days) for Minmus to come near Kerbin's equatorial plane costs **921.0 m/s**, all prograde, which is the Hohmann figure from 80 km. That saves 129 m/s.

**Coming back to the planet.** There's nothing to aim at, so there's no Lambert first guess. The best burn from a round orbit is prograde: it's where the speed you add counts most. Where you burn decides which way you leave the moon, and how hard decides how fast. So:

1. **Try 36 places round the orbit.** At each, search the prograde Δv upward from just under escape speed, √(2μ/r) − v, in 2 m/s steps. Follow the path out to the edge of the moon's sphere. There, add the moon's own position and velocity around the planet to get the path around the planet (§1.5). Then halve the step down to the Δv whose planet periapsis is at the asked height, going round the way the planet spins (the same signed periapsis as above). A badly placed burn throws you forward along the moon's orbit and never comes down, so that place is skipped.
2. **Narrow down** around the place that needed the least Δv: 9 places across a quarter of the step each side, three times.
3. **Capture** at the planet's periapsis as above. A height inside the planet's atmosphere plans no capture burn: the air does the braking (a reentry, or an aerobraking pass).
4. **Already leaving** the moon's sphere? The first burn is a course correction, either along the path or sideways to it (sideways swings where you leave), whichever is cheaper.

Worked example — from a 30 km round equatorial orbit around the Mun, back to 80 km over Kerbin:

1. Estimate with an infinitely large sphere: to fall from the Mun's distance (12,000 km) to a 680 km periapsis, the speed there must be √(2μ·r_p / (r_a·(r_a + r_p))) = 177.7 m/s, against the Mun's 542.5 m/s. So v∞ = 364.8 m/s. At 230 km from the Mun's centre, √(v_esc² + v∞²) − v_circ = √(752.6² + 364.8²) − 532.2 = **304.2 m/s**. (From the map's 10 km low orbit this gives 311.1 m/s, its Mun → Kerbin edge of 310.)
2. The planner's burn is **+260.8 m/s** prograde. It's less because the real sphere is only 2,430 km across: the burn is placed so the craft leaves it about 970 km on Kerbin's side of the Mun, already falling inward at 135 m/s, where 193 m/s of sideways speed is all the periapsis allows.
3. Capture at 80 km: **−851.9 m/s**. Asking for 35 km instead (inside Kerbin's 70 km atmosphere) needs a 263.5 m/s burn, with no capture.
4. Burn 5 m/s too hard, and the periapsis is at the surface. Half an hour later, an **8.6 m/s retrograde** correction puts it back at 80 km.

From Minmus (20 km) to 80 km over Kerbin: **159.4 m/s**, which matches the Δv map's Minmus → Kerbin edge of 160 m/s.

Moon to moon (Mun → Minmus) and planet to planet are hops *across*: §3.8.

**Already on the way, and missing.** A path that crosses the moon's orbit and passes within five of its spheres (or a tenth of its distance), without entering, also counts as on the way: a correction is planned rather than a new transfer. The correction is tried now, a quarter of the way to the meeting and halfway (a plane fix is cheaper later), and the cheapest wins.

- Code: `Hops.Down`, `Hops.Up`, `Hops.EnterSoi`, `Hops.LeaveSoi`; uses `Rendezvous.CheapestIntercept` and `Rendezvous.Intercept` (§4.4); the pane is `FromHereViewModel`; tested in `HopsTests`
- Source: Curtis 8.9–8.10 (patched conics and the sphere of influence); Curtis 5.3 (Lambert's problem); vis-viva (§1.3)

### 3.8 Trips anywhere: routes, launch windows and options

*Go to…* plans a trip from the craft's orbit to any body in the system. Every trip is a chain of three kinds of hop through the tree of bodies (star → planets → moons):

| Hop | Example | How it's planned |
|---|---|---|
| **Down** | Kerbin → Mun, the Sun → Duna | §3.7: the cheapest intercept of the body's centre, tuned to the height |
| **Up** | Mun → Kerbin | §3.7: a prograde escape burn, placed where the least Δv reaches the height |
| **Across** | Kerbin → Duna, Mun → Minmus | a launch window from the Transfer Planner (§3.4), flown from the real orbit (below) |

**The route.** List each body's chain of parents and find the first they share. Then go up from where you are to just below it, across to the destination's branch, and down to the destination. Mun → Duna is *up* (Mun → Kerbin), then *across* (Kerbin → Duna). Mun → Ike adds *down* (Duna → Ike). Each hop is planned from where the one before leaves the craft. A stop on the way is a round orbit at that body's usual parking height. Only the first hop is planned from where the craft really is, so each later burn says to plan again from there.

**A hop across** is flown in three burns:

1. **Ejection.** The window gives the departure velocity relative to the origin body, v∞ (Lambert's problem, §3.4). With patched conics the origin stops pulling at the edge of its sphere, so that's where the speed must be |v∞|. Vis-viva gives the burn from a round orbit at r:
   ```
   v_burn = √(|v∞|² + 2μ/r − 2μ/r_SOI)          Δv = v_burn − v
   ```
   (§3.1's √(v∞² + 2μ/r) is the same with an infinitely large sphere. For Kerbin's 84,159 km sphere the difference is about 45 m/s at the edge, which would make you arrive at Duna ten days early.) The burn is prograde, placed where the path's direction *at the sphere's edge* is nearest v∞'s. From r = p / (1 + e·cos ν) at r_SOI, that direction is (−sin ν, e + cos ν) in the orbit's plane. 72 places round the orbit are tried, then it's narrowed down by golden-section search.
2. **Tuned through both spheres.** The burn is then tuned like §3.7's, with the path followed out of the origin's sphere and into the destination's. First a normal (out-of-plane) part takes out the miss across the path's plane where it comes closest, using a few secant steps since the miss is close to linear in it. Then the prograde part is tuned for the height. Near a 180° transfer Lambert's plane is ill-defined, and v∞ can carry a tilt that isn't really needed. So the burn is also tried with v∞ laid flat in the origin's own orbital plane. Of the four ways (as given or flat, with or without the plane fix), the one costing least in all, ejection plus correction, wins.
3. **Course correction**, a tenth of the way along the coast (§3.7's correction), for whatever the tuning couldn't reach. Then the **capture** at the destination's periapsis.

**Options.** The windows come from the porkchop plot (80 × 60, searched from when the trip can leave):

- the **cheapest** window;
- the **fastest** flight among cells costing at most 1.25 × the cheapest;
- the cheapest flight **leaving at the first chance**. It's left out when the cheapest window is already near (within 5 % of a synodic period).

Each is planned in full. Then each is named for what it's best at, by its planned burns: *cheapest*, *arrives soonest*, *shortest flight* (time in space) or *leaves soonest*. One that's best at nothing is dropped. A trip down to a tilted moon (§3.7) offers its cheapest window and leaving at once in the same way.

Worked example — Kerbin (100 km round equatorial orbit) to a 100 km orbit around Duna, from UT 1,000,120 s:

| | Leaves in | Flight | Ejection | Correction | Capture | In all |
|---|---|---|---|---|---|---|
| Cheapest | 46 days | 66.1 days | 1,030.5 m/s | 16.7 m/s | 643.4 m/s | **1,691 m/s** |
| Shortest flight | 54 days | 45.3 days | 1,153.2 m/s | — | 919.7 m/s | **2,073 m/s** |
| Leaves soonest, arrives soonest | now | 81.1 days | 1,843.8 m/s | — | 984.8 m/s | **2,829 m/s** |

(Days of 24 h here, as the tests print them.) The cheapest matches the Δv map: 930 + 140 m/s to leave from low Kerbin orbit, and 250 + 360 to get into low Duna orbit. The other two tune straight onto Duna, so they need no correction.

From the Mun (30 km) it's the Mun → Kerbin hop first: 260.8 m/s, and 851.9 m/s into an 80 km orbit. Then the same Kerbin → Duna choices: 2,815 m/s in all for the cheapest. Mun → Minmus (30 km to 20 km) costs **450 m/s**.

**Flying a Transfer Planner window.** *Fly this window* hands the window you picked (the best one, or a point clicked on the porkchop plot) to the Flight pane as a transfer flight. If you're flying a craft around the origin, it's planned from that craft's orbit. Otherwise it's planned from a round equatorial orbit at the planner's parking height, to plan again from the real orbit once you're up ("Use the craft I'm flying"). *Re-plan* works from wherever the craft is: around the origin it's this hop across in that window, and around the parent it's a course correction (§3.7). A window whose departure is more than 2 % of its flight time in the past is gone. The return leg's window has its own *Fly this window*: the same, from the destination back. The arrival mode carries over too. *Flyby* drops the capture burn. *Aerobrake* drops it and, when the arrival height is above the atmosphere, aims the periapsis 0.43 of the way up it (as a deorbit, §2.5): **30.1 km** at Kerbin, 21.5 km at Duna. Worked example (`A_return_window_flies_home_and_aerobraking_aims_into_the_atmosphere`): from a 60 km Duna orbit, the cheapest return leaves with **643.9 m/s** and aerobrakes at Kerbin, with no correction needed.

**Planned again on the way.** Once out of Kerbin's sphere the craft is around the Sun, and Go to Duna is a hop *down* (§3.7). It's on the way, so it's a correction. In the test, a 2 m/s overburn at Kerbin is fixed with **28.7 m/s**, a quarter of the way to Duna.

- Code: `TripPlanner.Route`, `TripPlanner.Options`, `TripPlanner.Windows`, `Hops.Across` (with `Eject`, `Level`, `DirectionAt`), `TransferFlights.Plan`; the panes are `FromHereViewModel`, `TransferPlannerViewModel` (Fly this window) and `FlightViewModel` (Re-plan); tested in `TripPlannerTests`
- Source: Curtis 8.2–8.10 (patched conics, the sphere of influence, departure and arrival hyperbolas); Lambert's problem (§3.4)

### 3.9 Arriving in a chosen inclination (a polar orbit at the Mun)

A trip from an equatorial orbit arrives in about the destination's equator, because the path comes in roughly in the plane the bodies orbit in. A polar orbit there could be had by turning the orbit after the capture (§2.10), but that's dear: a 30 km Mun orbit costs 2 × 532.2 × sin 45° = **752.6 m/s** to turn polar.

It's far cheaper to turn the approach on the way. Seen from the destination, the craft comes in along a line (its velocity far away, v∞) and passes at a distance b from a parallel line through the body's centre: the aim point. Which side the aim point is on decides the plane of the pass. To the side in the equator, the pass is equatorial; swung a quarter-turn round the line of approach (above or below the body), it's polar. Gravity bends the path in, so the aim point for a periapsis r_p is wider than r_p:

```
b = r_p · √(1 + 2μ / (r_p · v∞²))
```

A small sideways burn a long way out moves the aim point a long way. So *Go to* with an inclination adds a burn on the way: **Aim for a 90° orbit**.

**How it's planned (From here, Fly mode).** After the transfer (§3.7, §3.8), a burn is tried 5 %, 15 %, 30 % and 50 % of the way along the coast, and the least Δv wins. Each is solved for two things at once: the pass at the asked height, and the arrival path's inclination to the destination's equator (stock KSP bodies have no axial tilt, so that's the inclination in the planner's frame). There are two things to hit and three parts to the burn (prograde, normal, radial), so Newton's method takes the least-Δv step each time:

```
miss = (height miss in km, inclination miss in degrees)
J    = how the miss changes per m/s along each axis (nudged 0.02 m/s)
Δv  ← Δv − Jᵀ (J Jᵀ)⁻¹ · miss          halved until the miss shrinks
```

It's done when the height is within 0.5 km and the inclination within 0.05°. The inclination is walked there 10° at a time from where the path arrives now, so each solve starts close. Then the capture is as before: retrograde at the periapsis, which keeps the plane. Above 90° the orbit is retrograde.

- *Already on the way* (§3.7), the course correction does the tilt as well, starting from the plain correction.
- *Across* (Kerbin → Duna, §3.8), the course correction a tenth of the way along does it. The coast is so long that it costs almost nothing.
- *Up* (Mun → Kerbin), the burn comes once the craft is out of the moon's sphere, falling toward the planet.
- *Keep as a flight* keeps the inclination, so *Re-plan* on the way aims for it too.

Worked example — Kerbin (100 km round equatorial orbit) to a 30 km orbit at the Mun (`Kerbin_to_the_mun_arrives_in_the_asked_inclination_for_a_small_burn_on_the_way`):

| Inclination at the Mun | Transfer | Aim on the way | Capture | In all | Extra |
|---|---|---|---|---|---|
| however it comes (0°) | 837.7 | — | 260.5 | **1,098 m/s** | |
| 45° | 837.7 | 22.7, 1.7 h after the transfer | 260.9 | **1,121 m/s** | +23 |
| 90° (polar) | 837.7 | 35.3 (16.6 prograde, 31.0 normal, 3.1 radial), 1.7 h after | 262.2 | **1,135 m/s** | +37 |
| 135° | 837.7 | 30.4, 0.9 h after | 264.6 | **1,133 m/s** | +34 |

So polar costs **37 m/s** on the way against 753 m/s turning in Mun orbit. Flying the burns exactly arrives at 90.0° and 30 km. Already coasting (planned again 10 minutes after the transfer), the course correction does it for **36.4 m/s**.

To Duna (100 km, the cheapest window), the course correction for 90° is **16.2 m/s**, against 16.7 m/s for the plain one: the polar orbit is free. From the Mun (30 km) to a polar 100 km Kerbin orbit, the aim burn is **280.6 m/s** (turning a round 100 km Kerbin orbit polar would be 3,176.5 m/s, §2.10).

**In Plan mode** a stop in orbit of another body (the leg arrives from its planet, its moon or another planet) shows an **Inclination** box, as a launch stop does (§2.7). The extra goes on the leg that crosses into the body's sphere, on top of its map value (an arrival tool, §6.2). There are no real orbits or dates there, so:

- **Down or Up** (planet ↔ moon): both trips are planned as above, from a round equatorial orbit at the map's low orbit, with and without the inclination; the extra is the difference. Kerbin's 80 km low orbit → Mun's 10 km low orbit at 90°: **+35 m/s**. Mun → Kerbin at 90°: **+276 m/s**.
- **Across** (planet → planet): a rule of thumb. A sideways burn a time t before arriving moves the aim point about Δv · t, and swinging it round the line of approach by Δi moves it 2·b·sin(Δi/2):
  ```
  Δv ≈ 2 · b · sin(Δi / 2) / t
  ```
  with v∞ from the Hohmann transfer (§3.2) and t the coast after the course correction, 0.9 of the transfer. Kerbin → Duna's 60 km low orbit at 90°: v∞ = 826.1 m/s, r_p = 380 km and μ = 3.0136×10¹¹ give b = 692.9 km; t = 5.87×10⁶ s, so Δv ≈ 2 × 692,857 × sin 45° / 5,871,602 = **0.17 m/s**. It ignores the Sun's pull on the way, so it's shown as an estimate.

- Code: `Hops.TiltOnTheWay`, `Hops.CorrectTilted`, `Hops.Tilt` (the `inclinationDeg` of `Hops.Down`, `Hops.Up`, `Hops.Across` and `TripPlanner.Options`); Plan mode: `ArrivalTilt.Plan`, `ArrivalTilt.RuleOfThumb`, `LegTools.FindArrival`, `LegTools.EvaluateArrival`, `LegTools.ApplyArrival`; tested in `TripPlannerTests` and `LegToolsTests`
- Source: Curtis 8.9–8.10 (the hyperbolic approach, its aiming radius and the B-plane idea of where it passes); Newton's method for an underdetermined system (the minimum-norm step, Jᵀ(JJᵀ)⁻¹)

## 4. Meeting a craft in orbit (rendezvous)

Getting from a launch, or from a craft already in orbit, to a standstill next to another craft. Used by the Rendezvous pane and by rendezvous legs (§6.5).

### 4.1 How a rendezvous is planned

The Rendezvous pane takes you from a launch (or a craft already in orbit) to a standstill next to each target in turn: a relay to repair, debris to deorbit. Every leg is the same four steps, and every burn is given both ways: **by feel** (which way to point, what to look for) and as **exact maneuver-node values** (time, prograde / normal / radial).

1. **Match planes** at the cheaper of the next two crossings of the orbits' planes (§4.2)
2. **Get into position**: wait for the window, or fly a phasing orbit to bring it sooner (§4.3)
3. **Transfer**: a Lambert intercept, searched around the Hohmann window (§4.4)
4. **Match velocity** at the meeting point

Each burn is planned at least **5 minutes** (`Rendezvous.BurnGapS`, node time to node time) after the one before, and after reaching orbit. That's time to finish one burn, turn to the next node and set it up. Node times are burn midpoints, so a 2-minute burn uses up 1 minute on each side and leaves 3 minutes to turn.

The positions come straight from the save: KSP stores each vessel's orbit as Keplerian elements (SMA, ECC, INC, LAN, LPE, MNA at epoch EPH), and §1.4 turns them into positions at any time. That is why the page asks you to plan from the save's current time — quicksave right before you plan.

- Code: `Rendezvous.Plan`, `Rendezvous.PlanLeg`; tested in `RendezvousTests`

### 4.2 Matching planes

Two orbits' planes cross along a line through the body's centre; you cross it twice per orbit, at the ascending node (AN, going "up" through the target's plane) and the descending node (DN). At the crossing your position lies in both planes, so you keep your speed and climb angle and just turn the horizontal part of your velocity into the target's plane:

```
v_new = v_r·r̂ + |v_h|·(n̂_target × r̂)        v_r = radial speed, v_h = horizontal velocity
Δv    = |v_new − v| = 2·|v_h|·sin(Δi/2)       Δi = angle between the planes (between the two h vectors)
```

That's §2.3 again, using the horizontal speed. Burn **anti-normal at the AN, normal at the DN**. The planner takes the cheaper of the next two crossings (the higher, slower one if your orbit is eccentric).

A plane change under **5 m/s** (`Rendezvous.SmallPlaneChangeDv`) isn't flown on its own. The transfer is a Lambert intercept between the real 3D orbits (§4.4), so its normal part and the match-velocity burn take up the small tilt for about the same Δv, with one burn fewer. At 100 km around Kerbin (v ≈ 2,246 m/s) that's tilts under 2·asin(5 / (2 × 2,246)) ≈ **0.13°**.

**When the lead time hides a crossing.** The plan starts the lead time after now (§8.4). If the next crossing comes before then, the planner waits for a later one, which can be half an orbit or a whole orbit later. The Rendezvous pane and Re-plan then say so: `NextPlaneCrossingUt` finds the first crossing after now, and when it is before the start and earlier than the planned burn, the note gives its countdown.

Worked example: a 31m 27s orbit, the crossing in 4m 36s, a 5-minute lead. The planner skips that crossing. The other node, ~15m 43s later, costs a little more, so it plans the same node one lap on, 31m 27s after the skipped one. A 4-minute lead would catch it.

Worked example — circular 200 km orbit, target tilted 10°: v = √(3.5316×10¹² / 800,000) = 2,101.07 m/s → Δv = 2 × 2,101.07 × sin 5° = **366.24 m/s**.

- Code: `Rendezvous.MatchPlane`, `Rendezvous.NextPlaneCrossingUt`, `Rendezvous.RelativeInclinationDeg`, `RendezvousViewModel.SkippedCrossingNote`; tested in `RendezvousTests`
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

Kerbin turns under the fixed (inertial) frame the orbits are measured in. KSP stores each body's starting rotation; Kerbin starts at **90°**. That's the value the app assumes; once Kerbin has been through KSP › Bake Surface Data it uses the game's own (kRPC `CelestialBody.InitialRotation`). The launch site's longitude in the inertial frame, comparable with a target's LAN (Ω), is:

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

The Rendezvous pane can start from a craft (or the craft you're flying in KSP) that orbits another body than the targets: a lander around the Mun meeting a relay around Kerbin, say. It uses the same rule as a mission leg from another body (§6.5). The Δv map's steps go from the craft's body's low orbit to the targets' body's low orbit, and the rendezvous starts on that low orbit, anywhere on it. The map's steps keep their map values (the craft's real orbit may differ from the map's low orbit).

To date the rendezvous burns, the plan estimates how long the steps take. Each step between a moon and the planet it orbits counts as a Hohmann half-orbit (§2.1) around the planet, between the moon's orbit and the planet's low orbit:

```
a = (r_moon + R_planet + h_low) / 2
t = π·√(a³ / μ_planet)                for each moon ↔ planet step; time inside the moon's SOI is left out
rendezvous start = start time + Σ t   (rounded up to the minute)
```

A step between planets waits for a launch window, which matters far more than the flight, so there is no estimate: pick the window on the Transfer Planner (§3.4) and set the start time to the arrival. On the Flight pane the steps are shown first; once the craft is around the targets' body, Re-plan (§8.4) plans the rendezvous from its real orbit.

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

Resonant deployment (§5.1) needs the carrier to pass the shared apsis once per satellite. The *shared apsis* is the point at the deployment radius r: the carrier's periapsis for a resonant orbit, its apoapsis for a dive orbit. The Flight pane times every burn from the carrier's real orbit:

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

Here ĥ is the horizontal direction (the velocity with its radial part removed). At an apsis the velocity is already horizontal, so these Δv are pure prograde (positive) or retrograde (negative).

If that apsis misses r by more than 1 km (say the craft is still in low orbit), the plan starts with one more burn that brings it to r, half a Hohmann transfer (§2.1). It burns at the *other* apsis (any time, if the orbit is circular), so the far side of the orbit lands on r:

```
0. Bring the apsis to r:   a₀ = (|r_burn| + r) / 2,   v₀ = √(μ·(2/|r_burn| − 1/a₀)) horizontal,   Δv₀ = v₀·ĥ − v
   then step 1 happens half an orbit later, at the new apsis (|r_burn| = r)
```

Re-planning after this burn (the Flight pane does it on its own once a burn is flown) drops it, and a small miss just gives a small fix-up burn.

Re-planning partway through (at least one satellite already out) keeps the very next pass of the shared apsis, even if it's inside the flight's lead time. Waiting a lap would put the next satellite 2/n of an orbit behind the one before instead of 1/n, so the spacing would be lost (`Partway_through_the_next_release_is_the_next_pass_however_soon`). The same goes for a released satellite circularized from the From here pane (§2.8).

Worked example: 3 satellites at Kerbin's synchronous altitude, 2,863,334.1 m (r = 3,463,334.1 m, μ = 3.5317×10¹² m³/s²), from a circular orbit at that altitude:

1. Satellite period 21,549.1 s; carrier period × 4/3 = **28,732.1 s**, so a = 4,195,530.5 m and the far apsis is at **4,327.7 km**
2. Enter: v = √(μ/r) = 1,009.82 m/s → v′ = √(μ·(2/r − 1/a)) = 1,094.40 m/s, so **+84.57 m/s prograde**
3. Satellite k circularizes at the periapsis one, two and three carrier laps later (+28,732.1 s each): 1,009.82 − 1,094.40 = **−84.57 m/s (retrograde)**

The same deployment from an 80 km circular orbit (r₀ = 680,000 m) adds step 0 and moves step 1:

0. v = √(μ/r₀) = 2,278.93 m/s; a₀ = (680,000 + 3,463,334.1) / 2 = 2,071,667 m, v₀ = 2,946.58 m/s, so **+667.65 m/s prograde**, raising the apoapsis to 2,863.3 km
1. Half that orbit later (**4,984.8 s**), at the apoapsis: v = √(μ·(2/r − 1/a₀)) = 578.54 m/s → 1,094.38 m/s, so **+515.84 m/s prograde**

- Code: `ResonantDeployment.Plan`; tested in `FlightTests`
- Source: vis-viva (§1.3); §5.1 for the carrier orbit

## 6. Budgeting a mission

How the planner adds up a mission's Δv: map values first, then leg tools that replace them with properly planned burns.

### 6.1 The Δv map and mission budgets

A mission adds up its legs from the Δv map. The app works the map out from the bodies' data (below), and you can edit any value in the Δv map pane. Aerobraking replaces a burn where the map allows it, and plane changes are optional worst-case extras. A phase's budget is:

```
phase Δv = Σ(legs in the phase) × (1 + margin%/100) + extra
```

**How the map is built.** The map is a tree rooted at Kerbin's surface: Kerbin's low orbit, keostationary orbit and moons hang off its low orbit. The Sun and the other planets hang off an "escape" node at the edge of Kerbin's SOI, and each planet's moons hang off that planet. Every edge is worked out with the formulas earlier in this guide, treating every orbit as circular at its semi-major axis, and rounded to 10 m/s:

```
low orbit altitude          10 km above the atmosphere, or 2 km above the highest peak (at least 10 km), rounded up to a km
surface ↔ low orbit         ideal climb + loss allowance (below)
low orbit → keostationary   Hohmann (§2.1)
low orbit → moon intercept  Hohmann Δv₁ out to the moon's orbit
intercept → low orbit       capture from v∞ (§3.1); v∞ = moon's orbital speed − transfer's apoapsis speed
low orbit → escape          ellipse from low orbit to the SOI edge: v_p − v_circ
escape → planet intercept   ejection for the Hohmann v∞ (§3.1, §3.2), less the escape edge already paid
intercept → elliptical      planets with moons: capture into an ellipse from low orbit to the SOI edge
elliptical → low orbit      v_p − v_circ at the ellipse's periapsis
elliptical → moon intercept at that periapsis, lower the apoapsis to the moon's orbit
escape → Sun intercept      ejection that drops the solar periapsis to the Sun's low orbit
plane change (worst case)   the whole inclination difference at the transfer's faster end: 2·v·sin(Δi/2) (§2.3)
flight time                 half the transfer orbit's period
aerobraking                 toward Kerbin wherever Kerbin's air can do the braking; away from Kerbin into any body with air
```

The climb from a surface is the only part that isn't pure orbital mechanics. The ideal climb is a Hohmann transfer from a point resting on the equator up to low orbit, minus the equator's own speed (launching east). On a body with air, a **loss allowance** for gravity and drag is added:

```
ideal = v_p − v_rot + (v_circ − v_a)      climb ellipse from r = R to r = R + low orbit altitude
loss  = 1,000 m/s × (g / 9.80665) × max(1, 0.4 + 0.6 · p)      g = surface gravity, p = sea-level pressure (atm); 0 if airless
```

The loss allowance is a rule of thumb, not a simulation. The 1,000 m/s per g is the usual figure for Kerbin. The pressure factor grows it for air thicker than Kerbin's, where drag dominates. It comes within about 10% of the figures players usually quote, and the real number depends on your rocket and how you fly it.

Worked example — Kerbin surface → 80 km:

1. Climb ellipse a = (600,000 + 680,000) / 2 = 640,000 m: v_p = 2,500.8, v_a = 2,206.6, v_circ = 2,278.9. From rest: 2,500.8 + (2,278.9 − 2,206.6) = 2,573.1 m/s
2. Kerbin's equator moves at 2π × 600,000 / 21,549.4 = 174.9 m/s, so ideal = **2,398.2 m/s**
3. Loss: g = 1.000, p = 1 → **1,000.3 m/s**. Total 3,398.5, shown as **3,400 m/s**

The same rule gives Duna 1,320, Laythe 2,830, Eve 9,360 and Jool 13,450. Airless bodies get the ideal climb only, e.g. the Mun from its 10 km low orbit: 584.4 − 9.0 = **575 m/s**, shown as 580.

Worked example — low Kerbin orbit → the Mun:

1. Transfer from 680 km to 12,000 km: a = 6,340,000 m, v_p = 3,135.3, so the intercept edge is 3,135.3 − 2,278.9 = **856.4 → 860 m/s**; flight time 7.4 h
2. Arriving: v_a = 177.7 against the Mun's 542.5, so v∞ = 364.8 m/s. Capture at 10 km (r = 210,000 m): √(364.8² + 2μ/r) − √(μ/r) = **311.1 → 310 m/s**

And to Duna: Kerbin's escape edge is 3,209.9 − 2,278.9 = **931 → 930 m/s**. The Hohmann v∞ of 918.3 m/s (§3.2) needs a 1,072.2 m/s ejection (§3.1), so the Duna intercept edge is 1,072.2 − 931.0 = **141 → 140 m/s**. Arriving with v∞ = 826.1 m/s, capture into a 60 km × SOI ellipse costs **250** and lowering the apoapsis to low orbit **360**.

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
- Planet and moon orbits are taken as circular at their semi-major axis, so trips to eccentric orbits (Moho, Eeloo, Dres) vary from the map with the date. A transfer leg (§6.3) works out the real window.
- Atmospheric ascent values (3,400 for Kerbin) come from the loss allowance above. They're typical, not exact: they depend on your rocket.
- Airless surface ↔ low orbit values are ideal (instant burns); many players budget more (e.g. 700 instead of 580 for the Mun). Edit them in the Δv map pane.

- Code: `DeltaVMapGenerator` (builds the map; stored at first start by `DvMapSeeder`), `CelestialBody.SurfaceToLowOrbitDv`, `DeltaVMap.Route`, `MissionPlanner.Summarize`; tested in `DeltaVMapGeneratorTests`
- Source: Curtis 6.2 (Hohmann), 8.6–8.7 (escape and capture with patched conics)

### 6.2 Leg tools: replacing map values with a planned leg

A mission leg runs from one stop to the next and is made of Δv-map steps (§6.1). A leg tool works out part of the leg properly and puts its result in place of the map values for the steps it covers. The steps stay, so each one keeps its phase and aerobrake choice. A tool that no longer fits the leg (the stops changed) is ignored, and the map values come back.

The tools are transfer windows (§6.3), constellation deployments (§6.4), rendezvous (§6.5), landing at a chosen spot (§2.6), launching into an inclined orbit (§2.7) and arriving at another body in an inclined orbit (§3.9). A landing replaces the last step, from a low orbit down to the surface, with its deorbit and braking burns on airless bodies. An inclined launch adds its extra over a due-east launch to the launch step, on top of the map's value, and measures later plane changes around the body from its inclination. An inclined arrival adds the burn on the way that tilts the approach to the step that crosses into the body's sphere.

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
2. Ejection (§3.1, μ = 3.5316×10¹², r = 680,000 m): √(854.38² + 2μ/r) − √(μ/r) = 3,334.22 − 2,278.93 = **1,055.3 m/s**. This replaces the map's 930 + 140 = 1,070 for LKO → escape → Duna intercept.
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

A leg is a rendezvous leg when it arrives at a *placed* craft: a custom location, an imported vessel, or a craft from the save that the mission keeps itself (added with *＋ Rendezvous*, so it isn't in the Locations pane). There is then a real craft whose position is known. Where the leg starts decides how much of it the rendezvous covers. The Rendezvous pane plans that part (§4.1): the launch time, a plane match, phasing, the Lambert intercept and the match-velocity burn.

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

**Back-to-back rendezvous.** The map reaches a craft only through its body's low orbit, so craft A → craft B is two map steps (A → low orbit → B). You never stop in that low orbit, so the mission shows the two as one leg, A → B, whose map value is the two steps added; the rendezvous then replaces it. The second leg starts from A's orbit when the first one arrives (you're alongside A then), which is what the Rendezvous pane does when one plan meets both craft: planned leg by leg or in one go, the legs come out the same. If the first leg is planned again and now arrives later than the second leg's saved start, the second plan is out of date (A and B have moved on since): the mission flags it, and opening the leg plans it again from the new arrival.

Worked example: launch to a placed station in a 150 km circular orbit inclined 5° (μ = 3.5316×10¹², LKO r₁ = 680,000 m, station r₂ = 750,000 m):

1. The map's step LKO → Station is a Hohmann transfer (§2.1): a = 715,000 m, so 2,334.04 − 2,278.93 = 55.11 m/s, then 2,169.98 − 2,116.20 = 53.78 m/s, **108.9 m/s** in all. Its worst-case plane change for 5° at the station is 2 × 2,169.98 × sin 2.5° = **189.3 m/s** (§2.3).
2. The Rendezvous pane lifts off as KSC passes under the station's orbit, so the rocket climbs straight into its plane. The plan needs only **109 m/s** of orbital burns and no plane change, and that replaces the step.
3. Going on from the station to a relay at 400 km in Kerbin's equatorial plane starts from the station's 5° orbit, so that plan has a 189 m/s plane match among its burns (479 m/s in total).
4. Coming home from the Mun to meet a relay at about 2,870 km: Mun surface → Mun low orbit (580) → Mun intercept (310) → Kerbin low orbit (860, or aerobrake) are the map's steps. The last step, Kerbin low orbit → relay, is the rendezvous. From 80 km, that is a small plane match (22 m/s for 0.56°), a 668 m/s transfer burn and a 433 m/s match, **1,122 m/s** in place of the map's Hohmann value.

- Code: `LegTools.FindRendezvous`, `LegTools.ApplyRendezvous`, `RendezvousLeg.StartsBefore`, `MissionPlanner.CombineCraftHops`, `MissionBuilderViewModel.RendezvousStartFor`, `RendezvousViewModel.EditLeg`; tested in `LegToolsTests`
- Source: §4.1 (Curtis 6.6–6.9, 5.3), Hohmann transfer (§2.1)

## 7. Your rocket: Δv and TWR

What a vessel or a saved design can actually do, and whether that's enough for the mission.

### 7.1 A vessel's remaining Δv (rocket equation)

The Vessels pane works out what a vessel in your save can still do from **its parts and what's left in its tanks**. The save gives each part's dry mass, the resources in it and each engine's thrust limiter; the part data read from your KSP folder (ModuleManager's cache, so mod patches are included) gives each engine's thrust, Isp and propellant mix, and each resource's density.

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

**How long one burn takes.** The **From here** pane shows each planned burn's length for the craft you're flying (as the save last recorded it). A burn of Δv uses up `m₀·(1 − e^(−Δv / (Isp·g₀)))` of propellant, from the rocket equation turned round, at the mass flow `F / (Isp·g₀)`. The next burn starts that much lighter. A burn the fuel doesn't stretch to gets no time. Example: 10 t, 100 kN, Isp 300 s (g₀·Isp = 2,942 m/s), 1,000 m/s → 10 × (1 − e^(−0.3399)) = 2.881 t at 0.03399 t/s = **84.8 s**. A second 1,000 m/s burn then starts at 7.119 t: **60.4 s**. Start a long burn about half its length before the node.

- Code: `VesselAnalysis.Analyze`, `VesselAnalysis.BurnTimes`, `PartCatalogReader.Parse`; tested in `VesselAnalysisTests`
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

In the Rocket pane you can pick a saved design. Its stages (§7.2, vacuum Δv, in firing order) are poured into the mission's phases (§6.1, in burn order): the first stage fills the first phase; whatever it has left carries into the next phase; when a stage runs out part-way through a phase, the next stage takes over — as in flight.

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

The Engineering document's stage table uses the same TWR for any body you pick, with each stage's tanks full: `TWR = thrust at the body's surface pressure / (m_start · g_surface)`. Picking a stage lists it for every body with a surface in the Rocket pane. Code: `CraftBreakdown.TwrAt`.

### 7.5 A design taken apart: mass, cost and what each stage drops

The **Engineering** document reads a saved design (.craft) and adds up what it's made of. Each part's mass is its dry mass plus what its tanks hold as loaded. Its cost is KSP's: a part's listed cost is with its tanks full, so the full contents come off and what's actually loaded goes back on.

```
part mass = dry mass + Σ amount · density
part cost = listed cost − Σ max amount · unit cost + Σ amount · unit cost
```

Each part in a .craft carries `sepI`, the stage whose firing drops it (−1 if it stays to the end). Parts are listed by that stage, in firing order. A stage's own decouplers fire as it starts, so in the stage table "Leaves behind" is what the *next* firing drops, the parts that stage used up. "Left at the end" is the mass once the last stage has burned (§7.2).

It also flags:
- **An engine that never fires with fuel**: the staging (§7.2) never burns it, because no stage lights it while a tank it can draw from still has propellant.
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

### 7.6 Which engine for a stage? (engine chart)

The **Engine chart** sizes one stage around each engine and picks the one that comes out lightest (or cheapest). The stage pushes a payload P (everything above it) through a vacuum Δv. A liquid-fuel stage gets exactly the propellant and tankage that takes. Tank dry mass is a fixed share k of the propellant, taken from the lightest tank that holds the engine's exact mix (stock liquid fuel + oxidizer tanks: k = 1/8).

```
r   = e^(Δv / (Isp_vac · g₀)) − 1                         (propellant ÷ mass at burnout)
m_p = r · (P + n·m_engine) / (1 − r·k)                    (propellant for n engines)
m₀  = P + n·m_engine + k·m_p + m_p                        (the stage with payload, full)
TWR = n · F(p) / (m₀ · g)                                 (F at the air pressure p where it fires, §7.4)
```

From m_p = r · (P + n·m_e + k·m_p): the burnout mass is everything but the propellant, and the rocket equation (§7.1) needs m₀ / m_burnout = r + 1.

- Liquid engines can't give more than **Isp · g₀ · ln(1 + 1/k)**, however big the tanks: at k = 1/8 that's ln 9. Past it, each tank adds more dry mass than the Δv it brings (r·k ≥ 1).
- An engine with its own tank (the Twin-Boar) uses that fuel first and only adds tanks for the rest.
- **Solid boosters** carry their own fuel and burn all of it, so only their number n can change: Δv = Isp · g₀ · ln((P + n·m_full) / (P + n·m_dry)).
- Each engine is tried at 1 up to the most engines allowed. A count only counts if its TWR (full tanks, at the place picked) is at least the least TWR asked for. Each engine shows at its best count, and the best engine wins the chart's cell.
- Cost is the engines plus full tanks, at the tank's cost per tonne of propellant. "Lightest" compares m₀ and "cheapest" compares cost.
- Δv uses vacuum Isp, as the Δv map's figures already include losses. On a body with air, only the thrust (and so the TWR) drops, which rules out vacuum engines like the Terrier for lift-off.

Worked example (the numbers in `EngineChartTests`): a Terrier (0.5 t, 60 kN, Isp 345 s vacuum / 85 s sea level, √390) under a 2 t payload, 1,000 m/s in space, with FL-T400 tanks (0.25 t dry for 2 t of propellant, √500 full).

1. k = 0.25 / 2 = **0.125**, at √500 / 2 = **√250 per tonne** of propellant
2. r = e^(1,000 / (345 × 9.80665)) − 1 = **0.34389**
3. m_p = 0.34389 × (2 + 0.5) / (1 − 0.34389 / 8) = **0.8983 t**, in **0.1123 t** of tanks
4. m₀ = 2 + 0.5 + 0.1123 + 0.8983 = **3.5106 t**. Check: 345 × 9.80665 × ln(3.5106 / 2.6123) = 1,000 m/s
5. TWR in space = 60 / (3.5106 × 9.81) = **1.74**. Cost √390 + 0.8983 × √250 = **√614.59**
6. The most the Terrier can give: 345 × 9.80665 × ln 9 = **7,434 m/s**

Off the pad it's another story. With 10 t on Kerbin (1 atm) and a least TWR of 1.3, one Reliant (240 kN vacuum, Isp 310 / 265 s, so 205.2 kN at sea level) gives 1.27 at 16.43 t. That's too low, so the stage takes **2 Reliants**: 18.26 t, TWR 2.29. A Hammer booster (0.75 t + 2.8125 t of solid fuel, Isp 195 s) under 1 t gives 195 × 9.80665 × ln(4.5625 / 1.75) = **1,832 m/s**.

- Code: `EngineChart` (`Options`, `Best`, `TankFor`, `ThrustAt`); tested in `EngineChartTests`
- Source: Tsiolkovsky rocket equation solved for propellant with a fixed tank fraction (the method behind community "best engine" charts such as Cuky's); KSP 1.12 stock tank masses (dry = 1/8 of the propellant)

### 7.7 Antenna range and signal (CommNet planner)

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

### 7.8 Power through the longest night (power budget)

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

While connected to the game (*KSP › Connect to KSP*), the planner reads the game time every 2 s and, in flight, every vessel's orbit every 10 s (both set in *Settings › Live connection*). kRPC answers one call per game frame or so, so the game-time read (scene, time, the flown craft's nodes: about 5 + 4 per node calls) takes a few frames, while the vessel read (about 14 calls per vessel) grows with the vessel and debris count. Hover the KSP status in the main window to see how long the last of each took. kRPC reports the same elements as the save, in radians: a, e, i, Ω, ω, M at epoch and the epoch. So a live orbit is the save's `ORBIT` block (§8.1) with i, Ω and ω × 180/π.

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

10⁻⁵ rad is about 10 m per 1,000 km of orbit radius. That's more than a coasting craft's jitter under physics, and far less than any burn. Only a real change re-plans the Rendezvous pane and moves linked custom locations. Otherwise only the game time moves the plan's start, once per 60 s of game time.

Worked example: "KerbLink Mk2 A" (a = 3,470,794.97 m, M₀ = 4.0125185 rad at epoch 816,532.3 s), restated by KSP 1,000 s later:

1. n = √(3.5316×10¹² / 3,470,794.97³) = **0.000290631 rad/s** (period 21,619 s)
2. The restated orbit has M₀′ = 4.0125185 + 0.000290631 × 1,000 = **4.3031497 rad** at epoch 817,532.3 s
3. At any t, L(t) − L′(t) = (4.0125185 + n·(t − 816,532.3)) − (4.3031497 + n·(t − 817,532.3)) = 0, so it's the same path: nothing re-plans
4. Had the craft been 0.01 rad (35 km) further along, |ΔL| = 0.01 > 10⁻⁵, so it counts as changed

- Code: `LiveMerge.Apply`, `LiveMerge.SamePath` (`Core/Ksp/LiveGame.cs`), read by `KspLink.ReadVessels`, laid over the save by `SaveSync.ApplyLive`; tested in `LiveGameTests`
- Source: kRPC SpaceCenter API (`Orbit` class); mean longitude as in Murray & Dermott, *Solar System Dynamics* 2.9; Curtis 3.4 (mean motion)

### 8.3 Flying a plan: how far off it you are

The Flight pane keeps the orbit a plan starts from and its burns. Where the plan has the craft at time *t* is that start orbit with each of the craft's own burns before *t* applied in turn (§1.8, the same step the planner uses). Satellites' burns are left out, because they don't move the carrier. The drift is the straight-line distance between where the plan has the craft and where its real orbit (live from the game, §8.2) has it:

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

**Partway through a leg** (at least one of its burns flown, and the plane match behind you), Re-plan keeps the leg's steps and fixes only the rest. It doesn't start the leg over, because a fresh plan would throw away the phasing you've already flown and cost far more. The same re-plan runs **by itself after each burn**: 90 s after the burn's node time (about half a long burn comes after the node), once per burn, while connected. It only updates the flight. Nothing goes to KSP until you click send, and the status line says when the craft's old nodes need replacing (Send all burns).

1. Burns already flown stay. Burns still to come before the last one (a phasing orbit before the transfer) stay as planned, applied to the craft's **real** orbit.
2. **Before the transfer**, the transfer burn and the match-velocity burn are solved again (Lambert, §4.4) from where those burns leave the craft.
3. **On the coast after the transfer**, the miss is measured first: the craft's real path and the target's at the meeting time. Under 500 m, no correction is planned and only the match-velocity burn is updated for the real path (Δv = target's velocity − yours there). Otherwise a **course correction** is solved from the coast to the target, searched over the first half of what's left, at least two minutes from now. A speed error is cheapest to fix early, and a tilt late.
4. The burn time and the meeting may each move by up to 15% of the time left. **Pinning them would be a mistake.** A transfer is close to half an orbit, and at 180° Lambert's transfer plane is undefined: a 0.07° tilt left by a plane match turned the re-solved transfer into 608 m/s instead of 55 m/s. Searching the window (the porkchop refinement of §3.4) finds a slightly shorter or longer transfer that costs about what the original did. The meeting can't move past the next leg's first burn.
5. Within 2 minutes of the meeting there's nothing left to re-plan: fly the last part by eye (the Rendezvous pane's "Closing the last few kilometres").

A tilt alone barely matters on the coast. A sideways error at a half-orbit transfer moves the meeting by metres, because the two paths cross again exactly half an orbit on. That's why a pure normal error needs no correction.

Worked example — Kerbin, from an 80 km orbit to a craft at 150 km 60° ahead (transfer 55.1 m/s, match velocity 53.8 m/s):

| Flown badly | Without a fix | Re-plan |
|---|---|---|
| Transfer +1.5 prograde, +0.5 normal | passes 5 km from it | correction **3.0 m/s**, match 54.3 m/s, meeting 34 s sooner |
| Transfer +1.5 prograde, +4 normal, +0.5 radial | passes 5.5 km from it | correction **2.8 m/s**, match 54.3 m/s |
| Transfer flown exactly | passes 9 m from it | none, match 53.8 m/s |
| 3° plane match off by 3 m/s normal, 1 m/s radial | — | transfer **57.8 m/s** (was 55.1), match 56.2 m/s, meeting 27 s sooner |

- Code: `FlightReplan.RendezvousRest`, `Rendezvous.CheapestIntercept`, `FlightViewModel.ReplanAfterBurn`/`ReplanRest`; tested in `FlightReplanTests` (the table, row by row)
- Source: Lambert's problem (§4.4); midcourse corrections as a re-solved Lambert arc, Curtis 5.3

**When the leg hasn't started** (nothing flown yet, or the plane match still to come), Re-plan runs the whole Rendezvous planner (§4.1) again with the craft's real orbit as the start, from a few minutes after now (the lead time). If a plane crossing falls inside the lead, the flight's note says so (§4.2). On the Rendezvous pane, a plan that starts from the game's time counts its "in …" times from the game's time, not from the start after the lead. It plans for the targets whose arrival is still to come, on their current orbits. The planner picks the phasing and intercept again (the cheapest), so a choice made in the Rendezvous pane can change. That's intended: the old choice was for an orbit you're no longer on.

- Code: `FlightViewModel.ReplanRendezvousAsync`

### 8.5 The flight log: Δv a flight used

While connected and flying, the flight log reads the flown vessel twice a second over kRPC streams. The game pushes the values as they change, so reading them costs no calls. Each sample holds game time, MET, situation, stage, altitude, speeds, Ap/Pe, position, attitude, throttle, mass, thrust, the active engines' combined Isp, dynamic pressure, g-force and the warp rate. A flight is told apart from others of the same rocket by its launch time, game time − MET (two within 2 s are the same launch).

**Δv between two samples a and b** (masses in kg, thrust in N, Isp in s, g₀ = 9.80665 m/s²):

```
Engines off in both                         Δv = 0
Same stage, no mass gone                    Δv = 0                                  (engines only just lit or cut)
Same stage, mass drop ≤ 1.5 × burnable      Δv = g₀ · Isp · ln(m_a / m_b)          Isp = (Isp_a + Isp_b) / 2
Otherwise (staged, parts dropped)           Δv = (F_a/m_a + F_b/m_b) / 2 · (t_b − t_a)
burnable = (F_a + F_b) / 2 / (g₀ · Isp) · (t_b − t_a)       what the engines could have burned in the time
```

The rocket equation on the mass drop is exact however far apart the samples are, because fuel burned is the mass gone. That holds through physics warp and through a coast that recording thinned out. When parts come off, the mass drop isn't fuel, so the thrust ÷ mass average over the gap is used instead. That's rough over a long gap, but staging gaps are half a second.

**Thinning.** Burning, in the air (q > 1 Pa; standing still on the ground still reads a trace, about 10⁻⁷ Pa), or a change of stage, situation, body or mass: every sample is kept, and so is the one before. Coasting outside the air: one every 10 s of game time. On-rails warp (faster than ×4) skips physics and moves the craft along its orbit. kRPC doesn't refresh every reading in the same frame there, so a sample's time, MET and orbit can be from moments far apart (at ×1,000, one frame is 20 s). Samples on rails aren't kept: the last one before the warp is, and the stretch is worked out from the orbit (§8.7). A warp of a minute or more is noted with its length. The same skew is why the recorder tells the flown vessel by kRPC's object id, not by game time − MET, and only matches a flight by its launch time out of warp. A disconnect, a vessel switch or leaving the flight is noted with the time not recorded once the flight is picked up again. The flight is picked up by launch time, not name, since the player may have renamed the vessel meanwhile; of flights launched together, the one last recorded with that kRPC vessel wins, then the one of that name. The first two readings after a switch are skipped: the game is still putting the vessel together, and its mass can read wrong. Readings whose game time hasn't moved on (the game paused) are dropped.

**Separations.** KSP starts a vessel's MET again when it splits off another, so a satellite released in orbit has MET ≈ 0 and its own launch time. A log whose first sample has MET under 2 s is a launch only if it isn't in space (orbiting, sub-orbital or escaping). In space and under 60 s, it's noted as separated from another vessel.

**How a flight ends.** It ends when the save KSP last wrote, if written after the log's last sample, has no vessel launched when the flight was (the save's `lct`, within 2 s). One launched then counts if it has the log's name, or isn't debris (renamed), or is the only one launched then. The save's `met` can't be used for this: KSP only moves it on while the vessel is loaded, so for a vessel left behind while the player flew another and warped, Ut − met is days off. A gone flight is *Recovered* if it was last seen landed or splashed, and *Lost* otherwise. The flight being flown is never ended this way (recovering or losing it ends the recording first). If the game's time goes back before the launch, it's *Reverted*. If it goes back within the flight (a quickload), the samples after that point are dropped.

Worked example: a 10 t craft burns to 8 t on a 300 s engine making 200 kN:

1. Mass flow = 200,000 / (9.80665 × 300) = **67.98 kg/s**, so 2 t takes **29.4 s**
2. Δv = 9.80665 × 300 × ln(10 / 8) = **656.48 m/s**, the same from two samples 29.4 s apart as from sixty
3. Staging in the next half second (10 t → 4 t, thrust 200 → 50 kN): the mass drop would claim 2,696 m/s. Thrust ÷ mass gives (20 + 12.5) / 2 × 0.5 = **8.1 m/s**

- Code: `FlightLogMath.DeltaVBetween`, `FlightLogRecorder` and `FlightLogs.EndedIn` (`Core/Ksp/FlightLog.cs`), streamed by `LiveTelemetry`, run by `FlightRecorder`; tested in `FlightLogTests`
- Source: Tsiolkovsky rocket equation (§7.1); kRPC SpaceCenter API (`Vessel.Mass`, `Thrust`, `SpecificImpulse`, streams)

### 8.6 The flight report: ascent losses, burns and touchdowns

The Flight report works through a log's samples (§8.5). Each step's Δv is added to running totals: by burn, by stage (the stage firing at the step's start), and by the body the craft was around.

**Burns.** A burn is a run of samples with thrust. Two runs less than 3 s apart, around the same body, are one burn (a staging pause or a throttle blip). It counts the step into its first sample (throttling up) and the step out of its last (the engines seen off).

**Ascent losses.** The ascent runs from the launch to the first sample in orbit around the launch body. That's KSP's ORBITING, with periapsis above the air, so the circularization burn is included. Along the path, speed changes by thrust less drag less gravity's pull along it:

```
dv/dt = (F/m)·cos α − D/m − g·sin γ          α: thrust off the path, γ: climb angle
sin γ = vertical speed / orbital speed        g = μ / (R + h)²
gravity loss        = ∫ g·sin γ dt            (trapezoids between samples)
drag & steering     = Δv spent − (v_end − v_start) − gravity loss
```

Speeds are orbital, so the planet's spin that the pad already gives counts as speed you had, not speed gained. Drag isn't read from the game, so drag and steering (cos α < 1) come out together as what's left over. Max Q is the highest dynamic pressure on the way up.

**Touchdowns.** A touchdown is the first sample landed or splashed after flying. It shows the vertical and surface speeds from the sample before (the moment before contact). Its descent Δv is spent from the last sample in orbit around that body, or entering its sphere of influence, to touchdown.

**Ascent profile.** Altitude against distance along the ground from the pad, from latitude and longitude (haversine):

```
a = sin²(Δφ/2) + cos φ₁ · cos φ₂ · sin²(Δλ/2)
downrange = 2R · asin √a
```

Worked example: straight up from Kerbin's pad at 100 m/s for 2 s, then reaching 110 m/s:

1. g = 3.5316×10¹² / 600,000² = **9.81 m/s²**, sin γ = 1 throughout, so gravity loss = 9.81 × 2 = **19.62 m/s**
2. 10 t → 9.864 t on a 300 s engine: spent = 9.80665 × 300 × ln(10 / 9.864) = **40.29 m/s**
3. Gained 110 − 100 = 10 m/s, so drag & steering = 40.29 − 10 − 19.62 = **10.67 m/s**
4. 10° of longitude along Kerbin's equator: 2 × 600 km × asin(sin 5°) = **104.7 km** downrange

- Code: `FlightAnalysis.Analyze`, `FlightAnalysis.GravityAlongPath`, `FlightAnalysis.Downrange` (`Core/Ksp/FlightAnalysis.cs`), shown by `FlightReportViewModel`; tested in `FlightAnalysisTests`
- Source: equations of motion along the flight path, Curtis 11.2 (gravity and drag losses); haversine formula

### 8.7 Filling a warp from the orbit

On-rails warp moves the craft along its orbit with Kepler's equation. Nothing else acts on it: no thrust, no drag (KSP won't rails-warp in the air). So where the log has a gap, the path can be worked out exactly from the orbit recorded with the sample before it. Each sample keeps the orbit's semi-major axis a, eccentricity e and the mean anomaly M₀ at that moment. A gap over 30 s is filled with up to 200 points:

```
n = √(μ / |a|³)                       mean motion
M(t) = M₀ + n·(t − t₀)
ellipse:   M = E − e·sin E,    r = a·(1 − e·cos E)
hyperbola: M = e·sinh H − H,   r = a·(1 − e·cosh H)     (a < 0)
v = √(μ·(2/r − 1/a))                  vis-viva
altitude = r − R
```

A gap that crosses into another body's sphere of influence is filled forward from the sample before it, until r passes the old body's sphere. It's also filled back from the sample after it, on its orbit around the new body, until r passes that sphere. A gap where the mass changed (a burn while not recorded), or that starts or ends in the air or on the ground, is left empty. The charts draw the worked-out path dashed. A change of sphere of influence isn't a gap in the log: the recorder keeps the same flight and reads against the new body.

Worked example: a 100 km circular Kerbin orbit (a = 700,000 m, e = 0) warped through for 1,000 s is filled every 10 s at 100 km and √(3.5316×10¹² / 700,000) = **2,246.1 m/s**. On an ellipse with a = 1,000 km and e = 0.5, half an orbit (π·√(a³/μ) = **1,671.7 s**) after periapsis it's at a·(1 + e) = 1,500 km from the centre (900 km up), going √(μ·(2/1.5×10⁶ − 1/10⁶)) = **1,085.0 m/s**.

- Code: `FlightAnalysis.FillGaps`, `FlightAnalysis.Along` (`Core/Ksp/FlightAnalysis.cs`), `Kepler.SolveEccentricAnomaly`/`SolveHyperbolicAnomaly`; orbit read by `LiveTelemetry`; tested in `FlightAnalysisTests`
- Source: Kepler's equation and vis-viva, Curtis 3.4 and 3.5 (hyperbolic); KSP patched conics (on-rails orbits)

### 8.8 The flight map

Each sample also keeps which way its orbit faces (inclination i, ascending node Ω, argument of periapsis ω, as kRPC gives them, in the same frame as the save and the bodies' orbits). With a, e and the mean anomaly (§8.7), that's the whole orbit with the sample's time as its epoch. So the craft's position is Kepler's position on it (§1.4), relative to the body it's around. The map looks down on a body's equator: it draws x and y and drops z.

```
craft around B, map of B:      p = StateAt(sample's orbit, μ_B, t)
craft around moon M of B:      p = StateAt(M's orbit, μ_B, t) + StateAt(sample's orbit, μ_M, t)
```

A planet's map shows its moons' orbits, and each moon where it was when the craft entered and left its sphere of influence (with that sphere drawn round it). Stretches filled from the orbit (§8.7) are placed the same way and drawn dashed. Burning is orange, coasting blue. A moon's own map shows only the part of the flight around it.

Worked example: a sample on a circular equatorial 100 km Kerbin orbit (a = 700,000 m, e = 0, i = Ω = ω = 0) with mean anomaly 0 at t₀ is at (700 km, 0). 20 s later it has gone n·20 = √(3.5316×10¹² / 700,000³)·20 = **0.0642 rad** round, to (698.6 km, 44.9 km), still 700 km from the centre.

- Code: `FlightMap.Build`, `FlightMap.Views`, `FlightAnalysis.PositionAt` (`Core/Ksp/`), drawn by `FlightMapCanvas`; tested in `FlightMapTests` (a simulated Mun flyby with patched conics)
- Source: Curtis 4.6 (orbital elements to position); patched conics, Curtis 8.6

### 8.9 Comparing a rocket's flights

A rocket is the vessel's name at launch. The Compare flights document puts its flights side by side. Each row shows Δv used, Δv to orbit, time to orbit, ascent losses (gravity, and drag & steering, §8.6), max Q, max g and touchdown speed (vertical, the moment before contact, of the last touchdown). Lower is better for every one of these, so the lowest in each column is picked out.

**Versions of the design.** The same name can fly a changed rocket. On the pad, the recorder counts the vessel's parts (with its launch clamps). With the mass at the first sample after lift-off, that's the design's fingerprint. Flights are lettered A, B… in the order first flown. A flight joins a version when it has the same part count (or either count is unknown) and a launch mass within 1 %:

```
same version ⇔ (parts₁ = parts₂ or either unknown) and |m₁ − m₂| ≤ 0.01 · m₁
```

1 % lets a little more or less fuel, or a kerbal, count as the same rocket, while a new tank or engine doesn't. A flight joined under way has no launch to go by and gets "—".

**Overlaid charts.** For the ascent and the whole flight, MET lines the flights up at launch. For a landing, the time since its descent began (the last sample in orbit, §8.6) lines them up, and Δv used counts from that moment.

Worked example: three flights of Kerbal X at 30.0 t (42 parts), 30.2 t (42 parts) and 33.0 t (42 parts). 30.2 is within 1 % of 30.0 (0.3 t), so it's version **A**. 33.0 isn't (3 t over), so it's **B**. Their Δv to orbit of 3,400, 3,350 and 3,520 m/s have the 3,350 picked out, but the summary warns that two versions are being compared.

- Code: `FlightComparison.Designs`, `FlightComparison.Figures`, `FlightComparison.Best` (`Core/Ksp/FlightComparison.cs`), shown by `FlightCompareViewModel`; the Rocket pane's line comes from the same analysis; tested in `FlightComparisonTests`

### 8.10 Ascent coach: what the climb did

The Flight report reads the climb to orbit (§8.6) sample by sample. Pitch is read against the local horizon (kRPC's surface frame). Logs from before 2026-10-15 read it against the planet's axes, which isn't a pitch, so for those the pitch figures are left out.

```
climb angle   γ = asin(vertical speed / surface speed)        (prograde, over the ground; from 50 m/s up)
off prograde  δ = pitch − γ                                   (+ above prograde: turning slower than the path)
steering loss = ∫ (F/m)·(1 − cos δ) dt                         (thrust not along the path)
drag loss     = drag & steering (§8.6) − steering loss         (never below 0)
launch TWR    = F / (m · g₀),  g₀ = μ / R²                     (first sample with thrust)
```

Rules of thumb for Kerbin-like climbs, with the figure each one is judged on:

| Advice | Judged on | Good | Called out |
|---|---|---|---|
| Launch TWR | F/(m·g₀) on the pad | 1.25–2.2 | below 1.25 (gravity loss), above 2.2 (drag) |
| Turn start | surface speed when pitch first < 85° | 20–120 m/s | over 120 m/s late, under 20 m/s early |
| 45° | altitude when pitch first ≤ 45° | 5–15 km | over 15 km late, under 5 km early |
| Prograde | mean δ while q ≥ 1 kPa | within ±3° | above (+) or below (−) prograde |
| Max Q | highest q on the way up | ≤ 25 kPa | over 25 kPa: too fast low down |

Worked example: 10° off prograde at a thrust acceleration of 20 m/s² for 10 s costs 20 × (1 − cos 10°) × 10 = **3.04 m/s**.

- Code: `AscentCoach.Diagnose`, `AscentCoach.ProgradePitch` (`Core/Ksp/AscentCoach.cs`); tested in `AscentCoachTests`

### 8.11 Ascent coach: the best climb for this rocket

The climb is flown again in a model fitted to the flight, with other steering. The model is 2D, over the equator, eastward, on a turning planet, in the frame that doesn't turn, stepped every 0.1 s:

```
a = −μ/r² r̂  +  (F/m) n̂  −  (q · A(v_air) / m) v̂_air          q = ½ ρ v_air²,  v_air = v − ω × r
```

- **Fuel.** The engines burn fuel at the rate they did (KSP engines burn at a fixed rate; only thrust changes with Isp). So the model follows the flight's mass and fuel flow against **burn time**, staging where the flight did: ṁ = F/(Isp·g₀) from each sample.
- **Thrust** = ṁ · g₀ · Isp(p). Isp against pressure is a straight line fitted per stage to what the flight saw (constant if a stage only flew in vacuum).
- **Air density** per atm of pressure, from the flight: ρ = 2q/v², in 5 km bands.
- **Drag area** A, from the flight, in the frame that doesn't turn, where only thrust, drag and gravity act. Along the orbital velocity:
  ```
  dv/dt = (F cos τ − D cos β)/m − g · v_vertical/v      ⇒   D = (F cos τ − m (dv/dt + g · v_vertical/v)) / cos β
  A = Σ D / Σ q   per 100 m/s band of air speed (q ≥ 1 kPa; bands with too little air take the nearest)
  ```
  β is between the airflow and the orbital velocity (they differ by the planet's spin, eastward). τ is between the nose and the orbital velocity. Without a usable pitch the nose is taken as along the path, so steering is counted with drag. Fast bands are flown high, where q is small, so their fit is mostly noise. KSP's drag falls past Mach 1 but not to nothing, so no band is taken below 40 % of the largest subsonic one (100–400 m/s).
- **Fuel past the recording.** The recording only shows fuel burned up to orbit. A climb flown differently may need a little more, so past it the last stage keeps burning at its last rate, for up to 50 % more burn time.
- **Pitch programs.** Straight up to a speed v₀, then pitch = 90° · (1 − f^k), with f the share of the climb from there to an altitude H, level after it. In thick air (q ≥ 1 kPa) the nose is kept within 5° of prograde, since a real rocket flips further off. Each program burns until the apoapsis is the flown one, coasts to it, then burns level until the periapsis is the flown one. A grid of v₀ (30–140 m/s), H (20–80 km) and k (0.3–1.2) is tried, then a finer one round the best.
- **Calibration.** The flown pitch, against burn time, is flown through the same model too. Gain = replay − best, so the model's own error (drag, Isp) mostly cancels. Best for this rocket ≈ flown − gain. The replay's difference from the real flight is shown as the model check. Without a usable pitch there's no replay, and the gain is the real flight − best, marked rough.

Checked against a rocket whose drag area (1.5 m²) and engines are known: the fit finds 1.50 m², and the replay comes within 0.8 % of the flown Δv (3,310 against 3,286 m/s).

- Code: `AscentSimulator.For`, `AscentSimulator.Fly`, `AscentSimulator.Optimize` (`Core/Ksp/AscentSimulator.cs`), shown by `FlightReportViewModel`; tested in `AscentCoachTests` (a known rocket flown with plain physics)
- Source: equations of motion along the flight path, Curtis 11.2; KSP engines (fixed fuel flow, Isp against pressure), KSP wiki *Specific impulse*

## Sources

- Howard D. Curtis, *Orbital Mechanics for Engineering Students* (Elsevier) — the reference for most formulas here
- Bate, Mueller & White, *Fundamentals of Astrodynamics* (Dover) — alternative derivations
- [KSP wiki](https://wiki.kerbalspaceprogram.com) — body data (`<Body>/Param` pages), pressure tables, biomes and landmark coordinates (body pages, *List of easter eggs*), part data
- [Eric Meyer — Resonant Orbit Calculator](https://meyerweb.com/eric/ksp/resonant-orbits/) — body constants, resonant-orbit method (MIT License)
- [alexmoon — KSP Launch Window Planner](https://alexmoon.github.io/ksp/) — porkchop approach for KSP
- Wikipedia articles linked in each section
