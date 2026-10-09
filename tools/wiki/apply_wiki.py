"""Merges tools/wiki/wiki-stock.json into the stock system of the seed file and writes docs/WIKI-COMPARISON.md.

Usage:  python -I tools/wiki/apply_wiki.py

Values the seed already has (orbits, radius, gravity, rotation, atmosphere basics) are compared, not overwritten:
differences are listed in the report for a person to decide on. Data the seed lacks is filled in: biomes,
landmarks, science multipliers, atmosphere pressure table / gas data / temperatures, and missing highest elevations.
"""
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SEED = os.path.join(ROOT, "src", "KspFlightPlanner.Data", "Seed", "bodies.json")
WIKI = os.path.join(ROOT, "tools", "wiki", "wiki-stock.json")
REPORT = os.path.join(ROOT, "docs", "WIKI-COMPARISON.md")
STOCK = "KSP Stock"
G0 = 9.80665  # KSP's standard gravity, used by the wiki's "gravity gees"
ATMO_EXTRAS = ["molarMassGPerMol", "adiabaticIndex", "equatorTempC", "poleTempC", "pressureTable"]


def close(a, b, tol=1e-6):
    if a is None or b is None:
        return a is b
    if isinstance(a, bool) or isinstance(b, bool):
        return a == b
    return a == b or abs(a - b) <= tol * max(abs(a), abs(b))


def fmt(v):
    if v is None:
        return "—"
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, float) and v.is_integer() and abs(v) < 1e15:
        return f"{int(v):,}"
    return f"{v:,.10g}" if isinstance(v, float) else str(v)


def main():
    with open(SEED, encoding="utf-8") as f:
        seed = json.load(f)
    with open(WIKI, encoding="utf-8") as f:
        wiki = {b["name"]: b for b in json.load(f)["bodies"]}
    stock = next(s for s in seed["systems"] if s["name"] == STOCK)

    checks, added, conflicts = [], [], []
    for body in stock["bodies"]:
        w = wiki.get(body["name"])
        if w is None:
            continue
        name = body["name"]

        def compare(label, app, wv):
            ok = close(app, wv)
            checks.append((name, label, app, wv, ok))
            return ok

        compare("Radius (m)", body["equatorialRadiusM"], w["radiusM"])
        compare("Surface gravity (g)", body["mu"] / body["equatorialRadiusM"] ** 2 / G0, w["surfaceGravityG"])
        compare("Sidereal rotation (s)", body["rotationPeriodS"], w["siderealRotationS"])
        if w.get("orbit"):
            labels = {"semiMajorAxisM": "Semi-major axis (m)", "eccentricity": "Eccentricity",
                      "inclinationDeg": "Inclination (°)", "argumentOfPeriapsisDeg": "Argument of periapsis (°)",
                      "longitudeOfAscendingNodeDeg": "Longitude of AN (°)", "meanAnomalyAtEpochRad": "Mean anomaly at epoch (rad)"}
            for k, label in labels.items():
                compare(label, body["orbit"][k], w["orbit"][k])

        hp = w.get("highestPoint")
        if hp:
            if body.get("highestElevationM") is None:
                body["highestElevationM"] = hp["elevationM"]
                added.append((name, "Highest elevation", f"{fmt(hp['elevationM'])} m"
                              + (" (approximate, from page text)" if hp.get("approximate") else "")))
            else:
                compare("Highest elevation (m)", body["highestElevationM"], hp["elevationM"])

        wa = w.get("atmosphere")
        if wa:
            a = body.get("atmosphere") or {}
            for k, label in [("heightM", "Atmosphere height (m)"), ("seaLevelPressureAtm", "Sea-level pressure (atm)"),
                             ("scaleHeightM", "Scale height (m)"), ("minTempC", "Min temperature (°C)"),
                             ("maxTempC", "Max temperature (°C)"), ("hasOxygen", "Oxygen")]:
                compare(label, a.get(k), wa.get(k))
            table = wa.get("pressureTable")
            if table and wa.get("seaLevelPressureAtm") is not None and table[0]["altitudeM"] == 0:
                atm = table[0]["pressurePa"] / 101325
                if not close(atm, wa["seaLevelPressureAtm"], 1e-3):
                    conflicts.append((name, f"Pressure table starts at {fmt(atm)} atm at 0 m, but /Param says "
                                            f"{fmt(wa['seaLevelPressureAtm'])} atm. Both kept; a kRPC reading should settle it."))
            for k in ATMO_EXTRAS:
                if k in wa:
                    a[k] = wa[k]
            got = [k for k in ATMO_EXTRAS if k in wa]
            if got:
                added.append((name, "Atmosphere", ", ".join(
                    f"pressure table ({len(wa['pressureTable'])} rows)" if k == "pressureTable" else k for k in got)))

        if w.get("science"):
            body["science"] = w["science"]
        if w.get("biomes"):
            body["biomes"] = w["biomes"]
            added.append((name, "Biomes", f"{len(w['biomes'])}"))
        if w.get("landmarks"):
            body["landmarks"] = w["landmarks"]
            added.append((name, "Landmarks", f"{len(w['landmarks'])}"))

    seed["sources"]["wikiExtras"] = ("https://wiki.kerbalspaceprogram.com body pages and List of easter eggs "
                                     "(biomes, landmarks, science multipliers, pressure tables); imported by tools/wiki")
    with open(SEED, "w", encoding="utf-8", newline="\n") as f:
        json.dump(seed, f, indent=2, ensure_ascii=False)
        f.write("\n")

    write_report(checks, added, conflicts, wiki)
    print(f"{sum(1 for c in checks if c[4])}/{len(checks)} values match; {len(added)} additions; "
          f"{len(conflicts)} conflict(s). Report: {REPORT}")


def write_report(checks, added, conflicts, wiki):
    lines = [
        "# App data vs. KSP wiki (stock bodies)",
        "",
        "Generated by `tools/wiki/apply_wiki.py` from `tools/wiki/wiki-stock.json` (scraped by `scrape_wiki.py`).",
        "Values the app already had are **compared, not changed**. Data the app lacked was added to",
        "`src/KspFlightPlanner.Data/Seed/bodies.json` (stock system only). Re-run both scripts to refresh.",
        "",
        "Source: [KSP wiki](https://wiki.kerbalspaceprogram.com), `<Body>/Param` pages, body pages, *List of easter eggs*,",
        "and the launch-site location boxes. Facts only; no wiki text or images are copied.",
        "",
        "## Summary",
        "",
        f"- **{sum(1 for c in checks if c[4])} of {len(checks)}** values the app already had match the wiki.",
    ]
    mismatches = [c for c in checks if not c[4]]
    lines.append(f"- **{len(mismatches)}** differ." if mismatches else "- **None differ.**")
    lines.append(f"- **{len(conflicts)}** conflict(s) inside the wiki itself (see below).")
    lines += ["", "## Differences", ""]
    if mismatches:
        lines += ["| Body | Value | App | Wiki |", "|---|---|---|---|"]
        lines += [f"| {b} | {l} | {fmt(a)} | {fmt(w)} |" for b, l, a, w, _ in mismatches]
    else:
        lines.append("None. Orbits, radius, surface gravity, rotation and atmosphere basics agree to 6 significant digits.")
    lines += ["", "## Conflicts inside the wiki", ""]
    lines += [f"- **{b}:** {t}" for b, t in conflicts] or ["None found."]
    lines += ["", "## Added to the app", "", "| Body | What | Detail |", "|---|---|---|"]
    lines += [f"| {b} | {w} | {d} |" for b, w, d in added]
    lines += ["", "## Landmarks", "",
              "Coordinates in degrees (north and east positive). These become landing-target choices.", "",
              "| Body | Name | Kind | Latitude | Longitude | Note |", "|---|---|---|---|---|---|"]
    for name, b in wiki.items():
        for m in b.get("landmarks", []):
            lines.append(f"| {name} | {m['name']} | {m['kind']} | {m['latitudeDeg']:.4f} | {m['longitudeDeg']:.4f} | {m.get('note', '')} |")
    lines += ["", "## Every value checked", "", "| Body | Value | App | Wiki | Match |", "|---|---|---|---|---|"]
    lines += [f"| {b} | {l} | {fmt(a)} | {fmt(w)} | {'✓' if ok else '✗'} |" for b, l, a, w, ok in checks]
    lines += ["", "## Not on the wiki (still missing)", "",
              "- Highest elevation for Laythe (no figure on its page), Kerbol and Jool (no surface).",
              "- Highest-point coordinates for Moho, Eve, Gilly, Ike, Dres, Laythe, Vall, Tylo, Bop.",
              "- Each body's rotation angle at UT 0 (needed to place a lat/lon in space). Comes from kRPC.",
              "- Terrain height at any lat/lon (craters, slopes). Comes from the planned kRPC map bake.",
              "- Temperature profiles above the surface. Only sea-level equator/pole ranges are given.", ""]
    with open(REPORT, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


if __name__ == "__main__":
    main()
