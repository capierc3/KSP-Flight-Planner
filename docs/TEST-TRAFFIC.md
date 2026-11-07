# Station traffic test: three shuttles

Everything on the `haulers` branch since the two-ship test: engines and RCS on at undock, warp held for burns and docking, the pilots' log, the pilot's safety stop, the station traffic controller and lanes, port booking and the 3 km wait, and the port settings.
Tick items as you go and jot down anything odd (numbers, messages, screenshots). Then hand this file back to Claude.
Easiest way: open `docs/checklist.html` in Edge or Chrome and pick this file. Ticks and notes save straight back into it.

| Shuttle | Name in the game | Starts docked at | Crew |
|---|---|---|---|
| A | Kerb Shuttle mk2 | Mun Station | Jeb (pilot) |
| B | Kerb Shuttle mk2 B | Kerb Station | none |
| C | Kerb Shuttle mk2 C | Min Station | none |

Mun Station and Min Station have a pilot aboard, so they have **traffic control**. Kerb Station has only scientists, so it has **none**. All tanks are full.

The route log (planner, or the in-game haulers panel) says what each ship did. Physics flying only happens near the ship you're flying, so to watch an arrival or a departure, be at that station.

---

## 0. Setup

- [x] KSP restarted (the new add-on and `HaulerPorts.cfg` load at start; ModuleManager rebuilds its cache, so it's slower once)
- [x] Planner started from PowerShell with error capture (for the import crash):
      `Start-Process "D:\Brick Road Software\KSP Flight Planner\.claude\worktrees\haulers\publish\haulers\KspFlightPlanner.exe" -RedirectStandardError "$env:USERPROFILE\planner-err.txt"`
- [x] "Protogen Hauler Test" loaded, kRPC server started, planner connected
- [x] Right-click a docking port: it has a **Haulers** setting (Any hauler). Right-click a command pod or probe core: it has **Hauler role** (Crew & supplies)
- [x] Quicksave made (the quicksave in the folder is the old Protogen one: don't quickload before this)

Notes:

## 1. Shuttle A: Mun Station → Kerb Station

Leaves a controlled station.

- [x] A undocks; message says engines lit, RCS on
- [x] A backs straight out of its port
- [x] A then climbs to about 200 m **above** Mun Station ("climbing out the departure lane"), stops there, and later burns
- [ ] The route log says "Backed off … out the departure lane"
- [ ] A long time warp gets stopped before A's burn while A is near you (and stays stopped till the burn is done)

Notes:

## 2. Shuttle C: Min Station → Mun Station

- [ ] C undocks with engines lit and RCS on, and climbs out above Min Station (it has a pilot too)
- [ ] An hour before arriving, C's log says "Booked a port at Mun Station"

Notes:

## 3. A arrives at Kerb Station while B leaves (no traffic control)

Be at Kerb Station. When A is in physics and coming in, send B to Mun Station.

- [ ] A's message: "No traffic control at Kerb Station (no pilot aboard Kerb Station): coming in on its own"
- [ ] A comes in from the side it arrives from (no lane, no 200 m wait)
- [ ] B undocks and backs out at the same time; no queue between them
- [ ] If they get within about 30 m: one shows "Safety stop: … is N m away", holds still or backs away, then "Clear of …: going on"
- [ ] They never stop for each other for good (the one leaving goes on, or the lower id does)
- [ ] A docks. Its log has the pilots' line: "Jebediah Kerman: Mun Station → Kerb Station flown 1×"
- [ ] B pushes clear of Kerb Station and flies on (it doesn't climb above: no traffic control here)

Notes:

## 4. At Mun Station: C and B arrive, one leaves (traffic control)

Be at Mun Station. Whichever of C or B docks first, wait for the other to come in, then send the docked one to Kerb Station.

- [ ] The first one comes in **below** the station ("waiting in the arrival lane" if anything else is busy), then docks
- [ ] The ship that's arriving holds about 200 m below Mun Station while the other is docking or leaving
- [ ] An arrival from above swings round the station at about 200 m, not straight through it
- [ ] The one leaving backs out, then climbs to about 200 m above the station before its burn
- [ ] Only once the leaver is clear does the arrival move in to about 55 m below and dock
- [ ] Time warp is stopped while a ship near you is docking
- [ ] No ship comes close to another

Notes:

## 5. Kerb Station full: the third ship waits, then two leave together

Before the third shuttle gets within an hour of Kerb Station, close ports so it finds none: set the fuel-tank port to **Tankers only**, and others to **No haulers**, so that once A and the second ship have docked no free port is open to shuttles. Be at Kerb Station.

- [ ] The second ship docks at a port still open
- [ ] It never picks a port set to Tankers only or No haulers
- [ ] The third ship's log: no free port open to crew & supplies haulers; it parks 3 km behind Kerb Station
- [ ] The planner's trip list: "waiting 3 km behind it for a free port, since …"
- [ ] It doesn't come into view at 3 km (it stays on rails)
- [ ] Send the two docked ships off at the same time: they leave with no queue, kept apart by their safety stops
- [ ] Once a leaver has finished backing out, the waiting ship books its port within a few seconds and is lined up 2.4 km out, coming in
- [ ] It docks, with the other two still nearby, and nothing collides
- [ ] Set the closed ports back to Any hauler afterwards (or leave the tank port on Tankers only)

Notes:

## 6. After the test

- [ ] If the planner crashed on importing a vessel: `C:\Users\Chase\planner-err.txt` handed to Claude
- [ ] KSP.log kept (or the test left as it is) so Claude can read it

Notes:
