# Live game test checklist

Everything here was built and unit-tested without the game running. These checks confirm it against real KSP.
Tick items as you go and jot down anything odd (numbers, messages, screenshots). Then hand this file back to Claude.

**Rough priority:** sections 2 and 5 matter most. A wrong number there sends burns the wrong way.

---

## 0. Setup

- [ ] Start KSP, load the **Protogen** save
- [ ] kRPC window → **Start server** (auto-accept is already on)
- [ ] Start the planner (`publish\KspFlightPlanner.exe`)
- [ ] Have a craft **in orbit around Kerbin** you don't mind adding/removing maneuver nodes on (a KerbLink relay is fine)
- [ ] Optional for later sections: a second craft in Kerbin orbit to use as a **target**, and a carrier with a few
      decouplable probes for the **resonant deployment** test

## 1. Connecting

- [ ] **Tools › Connect to KSP**: status bar dot goes amber, then green: "KSP: Space Center · Year 1, Day …"
- [ ] Status bar date **ticks forward** every couple of seconds while time passes in game
- [ ] Switch scenes (Space Center → Tracking Station → flight): status bar scene name follows
- [ ] **Tools › Disconnect from KSP** → grey dot, "not connected". Connect again works.
- [ ] Quit KSP while connected → planner shows "Lost the connection to KSP" in red (it shouldn't freeze or crash)
- [ ] Optional: **Settings › Live connection**: enter a bad port (e.g. `abc`) → red message and OK greyed out;
      **Restore kRPC defaults** puts 127.0.0.1 / 50000 / 50001 back

Notes:

## 2. Live orbits match the game ⚠ important

In flight, with a craft in orbit:

- [ ] **Rendezvous tab**: the "Craft" dropdown shows your vessels with Pe/Ap. For the craft you're flying, compare with
      KSP's map view (Pe/Ap of the orbit). **Should match within ~1 km.**
- [ ] "Save time" line now reads **"Game time: … · live from KSP"**
- [ ] Status bar (left) says **"live from KSP"** instead of "read 1:57 PM"
- [ ] Burn a little prograde in game (change Ap by 50+ km) → within ~10 s the planner's Pe/Ap for that craft updates
- [ ] While just coasting, the Rendezvous plan should **not** keep recalculating every few seconds (progress bar
      flickering constantly = bad). It may refresh about once a game-minute.
- [ ] Launch something new (not in the save yet) and reach orbit → it appears in the Rendezvous craft list as
      "… · not saved yet"
- [ ] **Locations tab**: if you have a linked location for a vessel you're moving, its orbit follows

Notes (paste any mismatched numbers):

## 3. Active vessel and target (Rendezvous tab)

- [ ] Left panel shows **"In KSP: flying X · target Y"** (or "no target set")
- [ ] **Use the craft I'm flying** switches to "A craft already in orbit" with your craft selected
- [ ] Set another Kerbin-orbit craft as target in KSP → **Add KSP's target** adds it to the targets list
- [ ] Target a planet or moon instead → the line shows the body name; Add KSP's target stays disabled (correct)
- [ ] Two craft with the **same name** (e.g. two "KerbLink"s): check the right one is picked. This is the
      shakiest part, because kRPC gives no vessel IDs and the planner matches by name and closest orbit.

Notes:

## 4. Fly a rendezvous plan (Flight tab)

- [ ] With a plan on the Rendezvous tab, click **Fly this plan** → switches to **Fly** and opens the **Flight** document with the new flight
- [ ] Flight shows "Flown by …", the burns with **countdowns that tick down**, first one marked **Next**
- [ ] **Send next burn** → node appears in KSP; the burn's tag changes to **On the craft** (within ~2 s)
- [ ] **Send all burns** → asks before replacing existing nodes; all nodes appear in KSP in order
- [ ] Send while flying a **different** craft than the flight's → it asks "You're flying X, but this flight is flown by Y"
- [ ] Drift line reads **"On plan: … m"** while coasting before the first burn (should stay well under 1 km)
- [ ] Fly the first burn (roughly is fine) → drift grows; once over 1 km **Re-plan from here** turns accent-coloured
- [ ] **Re-plan from here** → new burns from your real orbit, "Re-planned from …" message
- [ ] Close and reopen the planner → the flight is still in the list
- [ ] **Delete flight** asks first and removes it (nodes already in KSP stay)

Notes:

## 5. Node direction check ⚠ most important

This is the one thing that can't be tested without the game: do the planner's prograde/normal/radial land the same
way in KSP?

- [ ] On the Rendezvous tab, switch to **Exact nodes**. Pick a burn with a noticeable **normal** or **radial** part
      (a "Match planes" burn is ideal). Note its **"Orbit after"** Pe/Ap.
- [ ] Send it (via Fly this plan → Send to KSP). In KSP's map view, check the node's **predicted orbit**:
  - [ ] Pe/Ap ≈ the planner's "Orbit after"
  - [ ] For a plane change: relative inclination to the target drops to ~0° (not doubles)
  - [ ] In the node editor, the prograde/normal/radial numbers match the planner's (signs too!)
- [ ] If a sign is flipped (inclination doubles, or radial goes the wrong way), **stop and tell Claude** which axis

Notes (node values in planner vs KSP):

## 6. Resonant deployment

- [ ] Resonant Orbits tab: Kerbin, 3 satellites, **Use synchronous** → **Fly this deployment**
- [ ] If you were flying a craft around Kerbin, the flight has burns straight away; otherwise it says to choose the
      carrier. Switch to the carrier → **Use the craft I'm flying** → burns appear
- [ ] Burns: **Enter the carrier orbit** (~+84.6 m/s if starting circular at 2,863 km), then **Circularize satellite
      1–3** (~−84.6 m/s), one carrier lap (~7h 58m) apart
- [ ] If your orbit doesn't reach 2,863 km, a warning says so
- [ ] Send the carrier burn, fly it, check KSP shows Ap ≈ 4,327.7 km / Pe ≈ 2,863 km
- [ ] Before satellite 1's time: decouple a probe, switch to it, **Send to KSP** on "Circularize satellite 1"
      (sending it while flying the carrier should ask first)
- [ ] After flying it, the satellite's orbit is **circular** at ~2,863 km
- [ ] After satellite 1, **Re-plan from here** (with the carrier selected) → the plan picks up at satellite 2

Notes:

## 7. Other bits

- [ ] **Transfer Planner › Today** sets Year/Day to the game date
- [ ] **Vessels tab** still works while connected (it rebuilds when orbits change; your selection should stay)
- [ ] **Connect to KSP when the planner starts** (Settings) → connects on launch if KSP is up; stays quiet if not
- [ ] Leave it connected for a while in flight with lots of vessels: does KSP or the planner stutter every ~10 s?
      (Every vessel's orbit is read every 10 s.)

Notes:

---

**Known gaps (not bugs):** the Transfer Planner can't send nodes yet (needs ejection-node math). The Mission Builder
has no flight view yet. kRPC can't see the VAB craft you're building.
