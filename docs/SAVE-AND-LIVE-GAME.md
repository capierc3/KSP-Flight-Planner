# KSP Flight Planner — Your Save and the Live Game

How the planner reads KSP's save files and the live game (kRPC): which save is being played, which vessel a logged flight is now, docking and undocking, and splitting a docked stack back into its craft. These are rules and file formats, not formulas, so they live here rather than in the Math guide (docs/MATH.md). "Math guide §N.M" points there; a bare §N points to this file.

## Contents

- 1. Which save the game is playing
- 2. Which vessel a logged flight is now
- 3. Docking and undocking
- 4. Telling docked craft apart

## 1. Which save the game is playing

kRPC can't name the save the game has loaded, and the connection stays up when the player loads another one. So the planner works it out, and while the game plays another save than the chosen one it records no flights, ends none, and doesn't lay the live game over the chosen save (Math guide §8.2). A popup offers to switch to the other save.

**A save KSP just wrote.** KSP writes the save it's playing a few seconds after loading it and at every scene change (and the one it leaves on going back to the main menu). So every save folder is watched while connected. A save written now is the one being played if:

```
the game mode is the same     kRPC's ScienceSandbox = the save's SCIENCE_SANDBOX (underscores and case ignored)
|t_file − t_game| ≤ 60 s      the file's game time is the game's time now
```

**Before any write** (on connecting, and on each scene change), the game's vessels are looked for in every save by name. Debris and asteroids are left out, since any save could have one of the same name. With *share* = the game's vessels with a namesake in the save ÷ the game's vessels:

```
chosen save fits          same mode and share ≥ 50 %       (a launch since KSP last wrote it isn't in the file yet)
another save is the one   the only other save with the same mode and share ≥ 80 %
                          (with no vessels to go on: the only other save with the same mode)
not the chosen save       a different mode, or share < 50 %, and no other save stands out
nothing to say            same mode, no vessels to compare
```

Until a check has run, flights aren't recorded and none are ended as reverted (connecting to another save with an earlier game time would otherwise "revert" every open flight of the chosen one).

Worked example (the probe of 2026-10-20, Protogen chosen): loading SandBox, KSP wrote `SandBox/persistent.sfs` at t = 29,388,990 s, 6 s after the load. The game read mode Sandbox at t = 29,388,990, so |Δt| = 0 ≤ 60 and the mode is the same: **SandBox** is being played. Its 3 non-debris vessels (Kerbal X, two Untitled Space Craft) are all in SandBox (share 100 %) and none in Protogen (0 %, and Protogen's mode is SCIENCE_SANDBOX), so the vessel check says the same.

- Code: `LiveSaveMatch.Judge`, `LiveSaveMatch.WrittenByGame` (`Core/Ksp/LiveSaveMatch.cs`), run by `SaveSync` (`App/Services/SaveSync.LiveSave.cs`), read from the game by `KspLink.ReadIdentityAsync`; tested in `LiveSaveMatchTests`
- Source: KSP's save behaviour as seen by a probe on 2026-10-20 (writes on scene change and after loading); kRPC SpaceCenter API (`GameMode`, `UT`, `Vessels`)

## 2. Which vessel a logged flight is now

A flight is logged under the vessel's name at launch (the *rocket*). Vessels get renamed later — a relay is usually given its final name once it's in its orbit — so the Flight log and the Vessels pane find each flight's vessel in the save by **launch time** instead. KSP stores each vessel's launch time as `lct`; a vessel that splits off another (a relay released from its carrier) gets the moment it split. The log's launch time is the game time less MET at its first sample (Math guide §8.5), which is that same moment. With *launched* = the save's vessels whose launch time is within 2 s of the flight's:

```
tagged by hand              that vessel (or none, if tagged to none)
one launched then           that one, whatever it's called now
several launched then       the one with the flight's name, else the only one that isn't debris
otherwise                   no vessel (gone, or can't be told apart): tag it by hand (Edit log…)
```

A save without `lct` uses the file's time less the vessel's `met`. Edit log… lists the vessels launched with the flight first, then those with its name, then the rest by how near their launch was.

Worked example (the Minmus relays, 2026-10-20): the carrier *Link Shuttle mk2* launched on Day 62 and released three relays on Days 68, 71 and 73, each logged as *Link Shuttle mk2 Relay*. Renamed in orbit, *MinLink B* has `lct` = Day 71, 1:50:58; the Day 71 log launched within 2 s of that and no other vessel did, so that log is MinLink B's — its row reads "Now MinLink B", and MinLink B's page lists it. The Day 62 log stays with the carrier, whose `lct` is Day 62.

- Code: `FlightLogs.VesselOf`, `FlightLogs.LogsOf`, `FlightLogs.Candidates` (`Core/Ksp/FlightLog.cs`); the tag is `flight_logs.vessel_id`, set by `FlightLogRepository.Retag`; tested in `FlightLogTests`
- Source: KSP's save format (`VESSEL` node `lct`, `met`, `pid`)

## 3. Docking and undocking

When two vessels dock, KSP merges them into **one** of them; the other vessel is gone from the game. When they undock, KSP makes a **new** vessel of the part that comes off: it gets the name it had before docking (each docking port remembers it) and its MET — and `lct` — start again. So the flight log handles the two sides differently:

```
kept vessel (its kRPC id carries on)     its log carries on, recording the docked stack; notes "<other> docked"
vessel gone into it                      its log waits docked: event Docked (joined), with the name it docked as
vessel that comes off again              the waiting log carries on, with that vessel's launch time added
```

**Spotting a docking live.** Between two readings (≤ 3 s apart, same stage, off rails) the mass jumps by more than max(20 kg, 0.2 % of the mass): up for a docking, down for an undocking. Burning fuel or mining never moves the mass that fast; a staging changes the stage. The flown vessel vanishing into the one flown next (KSP kept the other) shows as a switch of vessel instead. Either way the next listing of the game's vessels says which: a vessel *gone* is one that was in the listing before, isn't now, and was alongside the kept vessel — same body, semi-major axis within 5 %, inclination within 2° (a rendezvous leaves a few m/s between them, a fraction of a percent in orbit size). The kept vessel must be in both listings, so a quickload (every vessel back with a new id) isn't taken for a docking. A vessel *appeared* alongside after a mass drop is one that came off.

**Finding the flight again.** A flight's launch times are its launch plus, for every undocking of its own vessel, game time − MET of the vessel it came off as. A vessel is the flight's if its game time − MET is within 2 s of any of them (Math guide §8.5, §2). A flight docked into another vessel (or undocked but not seen since) is *away*: it's only picked up by the name it docked as, by a vessel seen first in space or more than a minute after its MET started — a new launch of a rocket with the same name is never taken for it. Samples recorded after an undocking have MET = game time − the log's launch, so the log's clock runs on.

If KSP hands the kept vessel the docked ship's name on undocking (players report stations renamed after the ship that docked), the flown vessel *is* that ship now: the ship's waiting log carries on with it, and the flight that was being recorded goes with the vessel that came off under its old name.

**What the save says.** A docked port (`state = Docked (…)`) in a saved vessel keeps a `DOCKEDVESSEL { vesselName }` node: the vessel it came with. A docked flight stays open while some saved vessel's ports remember its name, and its vessel (§2) is that one. If none does, it ends the way the vessel it docked into ended (recovered with it), else as lost. This also catches dockings flown while the planner wasn't connected.

**After a quickload.** KSP doesn't write the save file when a quicksave is loaded, so for a while the file on disk describes play that was undone: dockings that never happened now, vessels that are back where they were. A save file more than 60 s ahead of the live game's time is such a leftover, and the log takes nothing from it (no dockings found, no flights ended). The 60 s allows for game time read a poll before the file was written. When the live game's time goes back by more than a second, every open log not being recorded drops the samples and events it noted after that time, with a *Quickload* note; the flight being flown does the same on its next reading.

Worked example (the shuttle's practice trip): the shuttle undocked from *Kerb Station* at UT 2,202,581, flew to the Mun and docked at *Mun Station* at UT 2,240,160, and KSP saved. The player then loaded a quicksave from UT 2,202,588. The save file, written at about UT 2,240,160, was 37,572 s ahead of the game, so its ports remembering *Kerb Shuttle mk1* on Mun Station are ignored. The shuttle's log no longer gets a wrong "Docked to Mun Station (found in the save)", and Mun Station's log drops its "Kerb Shuttle mk1 docked" (UT 2,240,160 > 2,202,588).

Worked example: the shuttle (launched Day 80) docks to *Kerb Station* on Day 81; the station is kept. The station's log notes "Shuttle docked"; the shuttle's log stops with "Docked to Kerb Station", and saves after that show the station's ports remembering *Kerb Station* and *Shuttle*, so it stays open. On Day 95 the shuttle undocks: KSP names it *Shuttle* again with MET 0, so its game time − MET is Day 95. Flown, it's found as the away flight named *Shuttle*; the log adds "Undocked from Kerb Station after 14d 0h 00m" with launch time Day 95, and the 14 docked days show as a break in its charts. Its MET reads 15 days, not 0.

- Code: `FlightDocking` (`Core/Ksp/FlightDocking.cs`), `FlightLogs.LaunchTimes`, `DockedIn`, `Away`, `EndedIn` (`Core/Ksp/FlightLog.cs`), `SavedVessel.DockedNames` (`KspSave.cs`), `FlightLogRecorder.RewindTo`, `FlightRecorder` (the checks, `EndFromSave`, `RewindOthers`); tested in `FlightDockingTests`, `FlightLogTests`
- Sources: KSP's save format (`ModuleDockingNode` `state`, `DOCKEDVESSEL`); KSP forum notes on undocking names and MET ([undocking rules](https://forum.kerbalspaceprogram.com/profile/100668-claw/content/page/156), [station renamed to the docking craft](https://forum.kerbalspaceprogram.com/topic/190345-space-station-being-renamed-to-the-docking-craft), [vessel naming, 1.4](https://forum.kerbalspaceprogram.com/developerarticles.html/whats-in-a-vessel-name-r206))

## 4. Telling docked craft apart

A docked stack is one vessel in KSP, so the save lists *Kerb Station* with the shuttle's parts, fuel and engines in it. The planner splits it back into the vessels that docked, so a shuttle can be planned from while it's still docked (**Plan for**, in the Fly pane) and is listed as a craft of its own (Vessels, Rendezvous).

**Cutting the stack.** Every saved part has a `uid` and a `parent` (an index into the vessel's parts; the root's is its own). A docked port (`state = Docked (docker)` or `Docked (dockee)`) has `dockUId`: the part it's docked to. Join each part to its parent, except where the two are a docked pair (a port on one is docked to the other): the groups left are the vessels that docked. The one holding the vessel's `root` part is the stack itself. Each other group is named by the `DOCKEDVESSEL { vesselName, vesselType, rootUId }` of a port in it whose `rootUId` is in the group (that port came with it). Another port in it may remember a stack it docked onto later, which is why the root decides. A port docked to another port of the same vessel (`Docked (same vessel)`) isn't the parent join, so it cuts nothing.

**Craft or station part.** A piece is its own craft when its type is Ship, Lander, Rover or Plane **and** it has a command part (`ModuleCommand`: a pod or probe core) **and** an engine. Everything else (Probe, Relay, Station, Base, Debris, or no command part or engine) is part of the stack. The type decides because a module or fuel tank brought up on its own probe core and engine looks just like a craft otherwise. The player can say otherwise on the piece's page (Vessels: **Counts as**); the say is kept by the piece's name, per save, so it holds when the same shuttle docks again.

A piece takes the stack's orbit and situation (live while connected, Math guide §8.2), with only its own parts: its Δv is what it has once it undocks. Its id is the stack's id and its root part's uid (`<pid>#<uid>`). A linked location whose vessel has gone (it docked, or undocked as a new vessel) follows the one craft that has its name. A plan made for a docked piece keeps following it after it undocks: KSP keeps the piece's root part as the new vessel's root, so the piece id finds the saved vessel whose root part has that uid (or, docked somewhere else, the piece with that uid). Before KSP saves again, the undocked craft is the live vessel of that name not yet in the save. Fly mode counts it as the flight's own craft when sending burns.

Worked example, the Protogen save as read on 2026-10-27: *Kerb Station* (Station) has four docked pieces. *Kerb Shuttle mk1* (Ship: pod, probe core, RK-7 engine, 42 parts) and *Homeward Bound 2* (Ship, 36 parts) are craft. *Kerb Station - Electrical* (Probe, no command part left and no engine, 34 parts) and *Kerbin Station - Fuel Tank* (Probe with a probe core and a Skipper, 61 parts) are station parts: the tank flew itself up, but it's a Probe. *Mun Station* has *Grand Lander* (Lander) docked, a craft.

- Code: `DockedCraft.Pieces`, `DockedPiece.IsCraftByDefault`, `WithDockedCraft`, `FindVessel` (`Core/Ksp/DockedCraft.cs`); `SavedPart.ParentIndex`/`DockingPorts` (`KspSave.cs`); `SaveSync.Craft`/`FlyAsId`/`SetDockedChoice`; `SettingsRepository.LoadDockedChoices`; tested in `DockedCraftTests`
- Source: KSP's save format (`PART` `uid`/`parent`, `VESSEL` `root`, `ModuleDockingNode` `state`/`dockUId`/`DOCKEDVESSEL`), read from a 1.12.5 save
