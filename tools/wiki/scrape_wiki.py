"""Pulls stock KSP 1 body data from the KSP wiki into tools/wiki/wiki-stock.json.

Usage:  python -I tools/wiki/scrape_wiki.py [--cache DIR]

Reads raw wikitext (`<Page>?action=raw`), which the wiki serves to plain HTTP clients. Only facts are kept
(numbers, names, coordinates); no wiki prose or images are stored. Credit: https://wiki.kerbalspaceprogram.com

What comes from where:
  <Body>/Param            orbit, radius, surface gravity, rotation, highest point, atmosphere basics, science multipliers
  <Body> page             biome list (KSP 1 section only), atmosphere pressure table, molar mass, adiabatic index,
                          equator/pole temperatures
  LANDMARKS below         hand-picked from "List of easter eggs" and the location boxes of the launch-site pages;
                          each coordinate is re-checked against the fetched page so stale entries are reported
"""
import argparse
import json
import os
import re
import sys
import urllib.parse
import urllib.request

WIKI = "https://wiki.kerbalspaceprogram.com/wiki/"
BODIES = ["Kerbol", "Moho", "Eve", "Gilly", "Kerbin", "Mun", "Minmus", "Duna", "Ike", "Dres",
          "Jool", "Laythe", "Vall", "Tylo", "Bop", "Pol", "Eeloo"]
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "wiki-stock.json")

# (body, name, kind, latitude, longitude, source page, note). Kinds: launch-site, anomaly, feature.
LANDMARKS = [
    ("Kerbin", "KSC Launch Pad", "launch-site", -0.102668048653556, -74.5753856554463, "Launch Pad/Box", None),
    ("Kerbin", "KSC Runway", "launch-site", -0.0405816849745605, -74.6913027972574, "Runway/Box", None),
    ("Kerbin", "Island Airfield", "launch-site", -1.5409, -71.9099, "Old Airfield/Box", None),
    ("Kerbin", "Inland KSC (Baikerbanur)", "launch-site", 20.6667, -146.4667, "Inland Kerbal Space Center/Box", None),
    ("Kerbin", "Woomerang Launch Site", "launch-site", 45.29, 136.11, "Woomerang Launch Site/Box", "Making History"),
    ("Kerbin", "Dessert Launch Site", "launch-site", -6.560277778, -143.95, "Dessert Launch Site/Box", "Making History"),
    ("Kerbin", "Dessert Airfield", "launch-site", -6.59972222, -144.0405555556, "Dessert Airfield/Box", "Making History"),
    ("Kerbin", "Monolith (Inland KSC)", "anomaly", 20.6709, -146.4968, "List of easter eggs", None),
    ("Kerbin", "Monolith", "anomaly", 35.5705, -74.9773, "List of easter eggs", None),
    ("Kerbin", "Monolith", "anomaly", -0.6403, -80.7669, "List of easter eggs", None),
    ("Kerbin", "Monolith", "anomaly", -28.8084, -13.4401, "List of easter eggs", None),
    ("Kerbin", "Desert temple", "anomaly", -6.4966, -141.6676, "List of easter eggs", None),
    ("Kerbin", "Crashed flying saucer", "anomaly", 81.9552, -128.5139, "List of easter eggs", None),
    ("Kerbin", "Seafloor smiley face", "anomaly", -30, -80, "List of easter eggs", "Approximate; on the ocean floor"),
    ("Kerbin", "Crashed Sputnik", "anomaly", -17.5344444444, 61.0602777778, "List of easter eggs", None),
    ("Kerbin", "Goddard rocket", "anomaly", -6.5179, -144.0160, "List of easter eggs", None),
    ("Mun", "Monolith", "anomaly", -9.8314, 25.9177, "List of easter eggs", None),
    ("Mun", "Monolith", "anomaly", 57.6602, 9.1423, "List of easter eggs", None),
    ("Mun", "Monolith", "anomaly", -82.2062, 102.9287, "List of easter eggs", None),
    ("Mun", "Kerbol arch (shattered)", "anomaly", 12.4508, 39.1959, "List of easter eggs", None),
    ("Mun", "Rock arch", "anomaly", -12.4717, -140.9612, "List of easter eggs", None),
    ("Mun", "Rock arch", "anomaly", 2.4633, 81.5310, "List of easter eggs", None),
    ("Mun", "Armstrong memorial", "anomaly", 0.7017, 22.7460, "List of easter eggs", None),
    ("Mun", "Crashed flying saucer", "anomaly", -70.9572, -68.1388, "List of easter eggs", None),
    ("Minmus", "Monolith", "anomaly", 23.777, 60.046, "List of easter eggs", None),
    ("Moho", "Mohole", "feature", 90, 0, "Mohole", "North pole; 4.6 km deep, 1.7 km across"),
    ("Duna", "Face", "anomaly", 17.05, -85.4667, "List of easter eggs", None),
    ("Duna", "Curiosity camera mast", "anomaly", -30.3419, -28.8103, "List of easter eggs",
     "The wiki says the 1.12 location needs updating"),
    ("Duna", "SSTV pyramid hill", "anomaly", -66.05, -160.8834, "List of easter eggs", None),
    ("Duna", "Stranded rover", "anomaly", -2.2450, -5.1922, "List of easter eggs", None),
    ("Dres", "Wrecked sports car", "anomaly", 1.955, 64.1427, "List of easter eggs", None),
    ("Laythe", "Whale skeleton", "anomaly", 0.07223, -162.525, "List of easter eggs", "Reported underground for some players"),
    ("Vall", "Vallhenge", "anomaly", -60.0862, 83.7801, "List of easter eggs", None),
    ("Tylo", "Cave", "anomaly", 40.1996, 174.0016, "List of easter eggs", None),
    ("Bop", "Dead Kraken", "anomaly", 68.43, 117.03, "List of easter eggs", None),
    ("Eeloo", "Snowkerbal", "anomaly", 25.9968, 110.5917, "List of easter eggs", None),
]

# Highest elevations that are only given in the page text, not on <Body>/Param (metres, as worded there).
HIGHEST_FROM_TEXT = {"Moho": (6817, "6817"), "Ike": (12750, "12.75"), "Dres": (5700, "5.7"), "Tylo": (11290, "11290")}

# Bodies whose page shows the KSP 1 biomes only as a map image.
BIOME_FALLBACK = {"Gilly": ["Lowlands", "Midlands", "Highlands"]}


def fetch(page, cache):
    path = os.path.join(cache, re.sub(r"[^A-Za-z0-9_.-]", "_", page) + ".txt") if cache else None
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return f.read()
    url = WIKI + urllib.parse.quote(page.replace(" ", "_")) + "?action=raw"
    req = urllib.request.Request(url, headers={"User-Agent": "curl/8 (KSP Flight Planner data import)"})
    with urllib.request.urlopen(req, timeout=60) as r:
        text = r.read().decode("utf-8")
    if path:
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return text


def number(s):
    s = s.replace("&nbsp;", "").replace(",", "").replace(" ", "").replace("−", "-").strip()
    try:
        return float(s)
    except ValueError:
        return None


def parse_param(text):
    fields = dict(re.findall(r"^\|([^=|#][^=]*)=(.*)$", text, re.M))
    f = lambda k: number(fields[k]) if k in fields else None
    out = {
        "class": fields.get("class"),
        "parent": fields.get("parent") or None,
        "radiusM": f("radius"),
        "surfaceGravityG": f("gravity gees"),
        "siderealRotationS": f("sidrot"),
    }
    if "semi-major axis" in fields:
        out["orbit"] = {
            "semiMajorAxisM": f("semi-major axis"), "eccentricity": f("eccentricity"),
            "inclinationDeg": f("orbinc"), "argumentOfPeriapsisDeg": f("pe arg"),
            "longitudeOfAscendingNodeDeg": f("an arg"), "meanAnomalyAtEpochRad": f("anomaly"),
        }
    if f("highest") is not None:
        out["highestPoint"] = {"elevationM": f("highest")}
        if f("highest latitude") is not None and f("highest longitude") is not None:
            out["highestPoint"].update(latitudeDeg=f("highest latitude"), longitudeDeg=f("highest longitude"))
    if "atmospheric height" in fields:
        out["atmosphere"] = {
            "heightM": f("atmospheric height"), "seaLevelPressureAtm": f("pressure"),
            "scaleHeightM": f("scale height"), "minTempC": f("min temp"), "maxTempC": f("max temp"),
            "hasOxygen": fields.get("oxygen", "").strip().lower() == "yes",
        }
    sci = {
        "landed": f("sci landed surf"), "splashed": f("sci splashed surf"),
        "lowerAtmosphere": f("sci lower atmo"), "upperAtmosphere": f("sci upper atmo"),
        "nearSpace": f("sci near space"), "outerSpace": f("sci outer space"), "recovery": f("sci recovery"),
        "flyingHighAltitudeM": f("sci border atmo"), "spaceHighAltitudeM": f("sci border space"),
    }
    out["science"] = {k: v for k, v in sci.items() if v is not None}
    return out


def section(text, title_re):
    """Text of the first level-2 section whose heading matches, up to the next level-2 heading."""
    m = re.search(r"^==\s*" + title_re + r"\s*==\s*$", text, re.M | re.I)
    if not m:
        return ""
    rest = text[m.end():]
    end = re.search(r"^==[^=].*==\s*$", rest, re.M)
    return rest[:end.start()] if end else rest


def clean(s):
    s = re.sub(r"<[^>]+>", "", s)
    s = re.sub(r"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", r"\1", s)
    s = re.sub(r"\s*\([^)]*\)", "", s)
    return s.replace("}}", "").strip()


def parse_biomes(text):
    sec = section(text, r"(KSP ?1 )?Biomes?")
    if not sec:
        return []
    m = re.search(r"\{\{Biome list(.*?)\}\}\s*$", sec, re.S | re.M)
    block = m.group(1) if m else sec
    # Only top-level bullets; stop at the first nested box (Kerbin's KSC mini biomes).
    block = block.split("{{FlipBox")[0]
    names = [clean(b) for b in re.findall(r"^\*(?!\*)\s*(.+)$", block, re.M)]
    return [n for n in names if n]


def parse_atmosphere(text):
    sec = section(text, r"Atmosphere")
    if not sec:
        return {}
    out = {}
    m = re.search(r"molecular[_ ]?(?:mass|weight)[^0-9]*?is ([0-9.]+)(?:&nbsp;| )?g/mol", sec, re.I)
    if m:
        out["molarMassGPerMol"] = float(m.group(1))
    m = re.search(r"adiabatic[_ ]index[^0-9]*?is ([0-9.]+)", sec, re.I)
    if m:
        out["adiabaticIndex"] = float(m.group(1).rstrip("."))
    deg = r"(-?[0-9.]+)(?:&nbsp;| )?°C"
    m = re.search(r"equator,[^.]*?low of " + deg + r" and a daytime high of " + deg, sec)
    if m:
        out["equatorTempC"] = {"night": float(m.group(1)), "day": float(m.group(2))}
    m = re.search(r"poles, the temperature varies between " + deg + r" and " + deg, sec)
    if m:
        out["poleTempC"] = {"low": float(m.group(1)), "high": float(m.group(2))}
    table = re.search(r"\{\|.*?\|\}", sec, re.S)
    if table:
        head = re.search(r"^!(.*)$", table.group(0), re.M).group(1)
        has_density = "Density" in head
        rows = []
        for line in table.group(0).splitlines():
            line = line.strip()
            if not line.startswith("|") or line.startswith(("|-", "|}")):
                continue
            cells = [number(c) for c in line[1:].split("||")]
            if len(cells) >= 2 and cells[0] is not None and cells[1] is not None:
                row = {"altitudeM": cells[0], "pressurePa": cells[1]}
                if has_density and len(cells) >= 4 and cells[3] is not None:
                    row["densityKgM3"] = cells[3]
                rows.append(row)
        if rows:
            out["pressureTable"] = rows
    return out


def coord_present(page_text, lat, lon):
    nums = [number(n) for n in re.findall(r"-?\d+(?:\.\d+)?", page_text)]
    near = lambda v: any(n is not None and abs(n - v) < 1e-4 for n in nums)
    return near(lat) and near(lon)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", help="folder to cache raw pages in (re-used on later runs)")
    args = ap.parse_args()
    if args.cache:
        os.makedirs(args.cache, exist_ok=True)

    bodies = []
    for name in BODIES:
        body = {"name": name, **parse_param(fetch(name + "/Param", args.cache))}
        page = fetch(name, args.cache)
        body["biomes"] = parse_biomes(page) or BIOME_FALLBACK.get(name, [])
        if name in HIGHEST_FROM_TEXT and "highestPoint" not in body:
            metres, wording = HIGHEST_FROM_TEXT[name]
            if wording not in page:
                print(f"WARNING: {name} page no longer says '{wording}' for its highest point", file=sys.stderr)
            body["highestPoint"] = {"elevationM": metres, "approximate": True}
        atmo = parse_atmosphere(page)
        if "atmosphere" in body:
            body["atmosphere"].update(atmo)
        body["landmarks"] = []
        bodies.append(body)
        print(f"{name}: {len(body['biomes'])} biomes"
              + (f", {len(atmo.get('pressureTable', []))} pressure rows" if "atmosphere" in body else ""))

    by_name = {b["name"]: b for b in bodies}
    stale = []
    for body, name, kind, lat, lon, source, note in LANDMARKS:
        if not coord_present(fetch(source, args.cache), lat, lon) and source != "Mohole":
            stale.append(f"{body} / {name} ({lat}, {lon}) no longer on '{source}'")
        mark = {"name": name, "kind": kind, "latitudeDeg": lat, "longitudeDeg": lon, "source": source}
        if note:
            mark["note"] = note
        by_name[body]["landmarks"].append(mark)
    for b in bodies:
        if "latitudeDeg" in b.get("highestPoint", {}):
            hp = b["highestPoint"]
            b["landmarks"].append({"name": "Highest point", "kind": "feature", "latitudeDeg": hp["latitudeDeg"],
                                   "longitudeDeg": hp["longitudeDeg"], "source": b["name"] + "/Param",
                                   "note": f"{hp['elevationM']:.0f} m"})

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"source": "https://wiki.kerbalspaceprogram.com (facts only; KSP 1.12)", "bodies": bodies},
                  f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"Wrote {OUT}")
    for s in stale:
        print("WARNING:", s, file=sys.stderr)


if __name__ == "__main__":
    main()
