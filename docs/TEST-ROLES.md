# Crew roles test: Survey Engineers, Science Officers, landmarks

Version 0.3.3 on branch `science`. What's new since 0.3.2: on-duty Survey Engineers and Science Officers in research labs, linked probes, a body being done and its report, landmarks found by the survey. The science pane items (map, upcoming passes, Scan now, sent home / recovered) are in the same build and haven't been tried yet either, so they're listed at the end.

Tick what works, and note anything odd: numbers, messages, screenshots. Rules: [KERBIN-RESOURCES.md "Survey and science roles"](KERBIN-RESOURCES.md#survey-and-science-roles), [SURVEY.md "When a body is done"](SURVEY.md#when-a-body-is-done-built-2026-11-15-not-tried-in-game).

## Settings

- [ ] *KSP Flight Planner › Crew roles* is in the difficulty settings, with all nine settings and their defaults
- [ ] Turning *Survey needs a Survey Engineer* off brings back the old rule (any engineer aboard a station with scanners surveys), and the survey pane's first check goes back to "Engineer aboard"

Notes:

## Job titles and lab seats

- [ ] Engineer on duty in a station's lab (MPL), station with a narrow-band or survey scanner: **Min Station Survey Engineer** (KR window and Station window)
- [ ] Engineer on duty in a lab, station without scanners but with a linked probe that has one: still Survey Engineer
- [ ] Engineer on duty in a lab, no scanners anywhere: Awaiting orders
- [ ] Scientist on duty in a lab: **Min Station Science Officer**
- [ ] Engineer or scientist on duty in a pod, hitchhiker can or docked ship: Awaiting orders, and the Station window hints to move them into a lab
- [ ] *Move…* into the lab gives the title within about 10 s
- [ ] Engineer on duty at KSC: **KSC Survey Engineer**. Scientist on duty at KSC: **KSC Science Officer**
- [ ] Pilots' titles (Traffic Controller, Shuttle pilot) still work alongside these
- [ ] KR data card shows "Traffic Controller then …" job history across a change of title

Notes:

## Survey Engineer at work

- [ ] Survey pane, first check: "Survey Engineer working its scanners: Bill (Min Station)" for the station's own craft
- [ ] Engineer off duty (or out of the lab): the check fails, says what to do, and coverage stops building
- [ ] Linked probe: a probe with a narrow-band in the same SOI and a CommNet path to the station. The pane, with the probe picked, names the station's engineer, and coverage builds along the probe's own orbit
- [ ] Not linked: probe in another SOI, or no CommNet path (behind the moon). No coverage from it while it's out of contact
- [ ] Two craft (station and probe, or two probes): the engineer works the one over new ground. Coverage grows along one strip at a time, not both together
- [ ] Reports name the engineer and the craft: "Bill … mapped Greater Flats with Probe S-1's narrow-band: ore ≈ 7%"
- [ ] KSC engineer works a probe around Kerbin that has a link home
- [ ] KR data card: "Surveyed: N biome reports …" builds up (only while the planner is connected)
- [ ] Coverage credited over time warp and with the probe in the background (save, warp, save)
- [ ] Quickload to before a find: the find and its report are taken back

Notes:

## A body done, and its report

Quickest on a small body (Minmus). The figure is *Body done at % mapped*, so turning it down to 50–60 makes this quicker to reach.

- [ ] At the figure with every biome reported: a "Minmus is mapped … start writing the survey report" report in the pane, and a screen message in game
- [ ] The engineer's title becomes **Writing the Minmus survey report**. The survey pane's banner shows the writing time so far ("1h 10m of 6h on duty")
- [ ] Writing pauses while the writer is off duty or out of the lab, and picks up again when they're back
- [ ] After 6 hours of duty (or the setting), "… filed the Minmus survey report": in game, in the pane's reports, and on the banner
- [ ] After filing, the engineer is **Awaiting orders**, and the station's scanners and linked probes there no longer make anyone a Survey Engineer
- [ ] Done while the planner wasn't connected: the game is told the next time it connects
- [ ] KR data card: "survey reports filed for Minmus"

Notes:

## Landmarks

- [ ] The game's anomaly list is fetched on its own once connected. Afterwards, Locations › Minmus shows only what's been found (or "None found yet")
- [ ] Launch sites always show (Kerbin)
- [ ] A narrow-band strip over an anomaly: report "… spotted something odd on Minmus at …". It shows as **Unknown object (lat, lon)** in Locations, in Land at a spot, and in the survey pane's *Places of interest*
- [ ] Natural features (the Mohole, a highest point) show by name once covered, with a "found …" report
- [ ] A craft within 3 km (landed or flying, flown or not) identifies it: screen message in game, "… identified …" report, real name in the lists
- [ ] Anomaly positions match the game (an Unknown object sent as a landing spot lands next to it)
- [ ] *Hide landmarks until surveyed* off: everything shows, named
- [ ] Anomalies from a mod (if any installed) show up too

Notes:

## Science Officer at work

- [ ] Linked probe you're **not** flying enters a biome where an experiment pays at least the threshold *sent*: the officer runs and sends it. "Min Station Science Officer (…): Probe S-1 sent Gravity Scan from Lowlands (space low): 32". R&D science goes up by that much
- [ ] Timing: sign in 15 s, run 10 s each, send = the antenna's transfer time (Communotron 16: 18 s for a gravity scan), sign out 15 s. The Station window shows the step and the seconds left
- [ ] Several experiments in one session, the one paying most first
- [ ] Only rerunnable experiments (no goo or materials bay), and only ones that pay when sent (recovery-only science is passed over)
- [ ] The same experiment doesn't run again while the probe stays in the same place. It runs again on entering a new biome or situation
- [ ] One probe at a time. A second probe that starts paying waits, then gets its turn if it still pays
- [ ] Two officers at one station work two probes at once
- [ ] Switch to the probe mid-session: the officer stops after the current step, and you get the usual Scan now alerts
- [ ] Link lost while sending (behind the moon): "lost the link sending …, nothing earned"
- [ ] Lab research slows or stops during a session: the only researcher stops it, two researchers halve it. Check the lab's progress over a session, loaded and unloaded
- [ ] KSC Science Officer works probes around Kerbin with a link home
- [ ] KR data card: "Science sent: N runs from …: X science"
- [ ] Nothing happens at the space centre scene (it carries on in flight or the tracking station)

Notes:

## Science pane (0.3.2 items, not tried yet)

- [ ] Science data fetched on its own at connect (including transmit shares; an older fetch is redone)
- [ ] Map: biomes coloured by what one run pays; hover shows biome and value; situation box; experiment box filters it
- [ ] Upcoming passes for a picked probe: times, biomes and values look right against the game
- [ ] *Count as*: an uncrewed vessel defaults to *Sent home*, a crewed one to *Both*. Table, map, best places and passes follow it
- [ ] Scan now alert in game: an uncrewed vessel shows "… sent", a crewed one "… (sent N)"

Notes:
