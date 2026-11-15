# Kerbin Resources

Kerbin Resources is the kerbals' HR department, one of the planner's in-game add-ons (`mod/KspFlightPlanner.AddOns/KerbinResources`). It keeps a personnel file on every kerbal, a log of every crewed flight, the duty roster and the controller log. Traffic Controller's crew rules read from it.

**Status (2026-11-08; duty roster and controller log 2026-11-09):** built and installed, not tried in game yet, not committed. The basics are done: the personnel data is for show only. Ages, retirement and the rest don't affect anything until the management update (shifts, fatigue, recruiting).

## The window

The Kerbin Resources window has its own toolbar button in flight, the map, the tracking station and the space centre. It also opens by itself on the right of the Astronaut Complex. KSP's own Astronaut Complex screen isn't changed.

- **The roster:** every kerbal, with their trait, age, where they are (KSC, or the craft they're aboard) and their logged flights. There are tabs by trait, and a tick box for the dead and missing.
- **The data card:** pick a kerbal to see:

| Line | What it shows |
|---|---|
| Name | Full name, pilot's call sign, trait, founding member, where they are |
| Age | Age in years, life stage, years until retirement |
| Born | Weekday and date on the Kerbin calendar |
| Birthplace | City and nation |
| Hired | Date joined and years with the company (or "applicant") |
| Family | Kerbals sharing their last name |
| Nature | KSP's own courage and stupidity, and whether they're a badass |
| Been to | KSP's own career log: the furthest they've got at each body (landed, orbit, flyby) |
| Duty | On duty where (KSC, or a craft and its body) and for how long, or off duty; a button to put them on or take them off |
| Time on duty | Total over all their shifts |
| Controlled | Burns and hand-offs overseen as a controller, and the bodies they covered |
| Cleared for, Training | Traffic Controller's lines: the routes a pilot may fly ([TRAFFIC-CONTROLLER.md](TRAFFIC-CONTROLLER.md#route-qualification)) |
| Flights | Logged flights, total time flying, longest flight; "Flying now" if partway through a trip |

Below the card are the last burns and hand-offs they controlled, their last shifts, their trips (from → to, how often, how many by hand, when last) and every flight with who else was aboard. The roster marks who's on duty.

## The flight log

Every crewed flight is logged with who was aboard. The player's own flights count as well as Traffic Controller's routes. A crewed craft undocking (or launching) opens a trip, and docking somewhere else logs it. The rules are in [TRAFFIC-CONTROLLER.md “Crew rules and the flight log”](TRAFFIC-CONTROLLER.md#crew-rules-and-the-flight-log).

Later: decide which half of an undocking is travelling by which one thrusts first (engines or RCS), with mass as the fallback.

## Duty roster

Who's on duty, and where. A kerbal goes on duty where they are: **at KSC** (Kerbin's SOI, standing in for the tracking station) if they're in the Astronaut Complex, or **aboard their craft** (its SOI) if they're on one. Only hired crew can go on duty. The player puts kerbals on and off duty from the data card (or the planner, through kRPC).

- A kerbal comes off duty by themselves when they leave their post: a KSC kerbal put on a ship, a station kerbal who leaves the station, or one who dies or goes missing. Checked every 10 seconds.
- **The post is the station, and the seat is tracked separately.** A kerbal aboard a docked stack is on duty at the stack's main craft, the station KSP named the stack after. KR also tracks which craft their seat belongs to: the station's own parts, or a ship docked to it. Each docked port remembers the craft it came from, so the parts below it belong to that craft. A pilot in a shuttle docked at Min Station shows as "On duty at Min Station, in Min Bus mk1 (a docked ship, not the station's own parts)". The seat is checked every 10 seconds, so moving between the station and a docked ship updates it. Traffic Controller will only take controllers from the station's own parts.
- The shift follows a part of the station, so a docking or undocking that changes the stack's id or name doesn't end it. A kerbal who leaves in a ship undocking from the station comes off duty ("left Min Station").
- **Job titles.** Each shift has a job title, given by the add-on whose job it is through a hook (`DutyRoster.JobOf`). With no title, the kerbal shows as **Awaiting orders**. The title is checked when they go on duty and every 10 seconds after. Each change is kept in the shift's job history ("Traffic Controller then Shuttle pilot"), for shifts and fatigue later. The title shows on the data card, in the roster and in the shift list.
- Titles today come from Traffic Controller, for pilots only: "Mun Station Traffic Controller" (or "KSC Traffic Controller") and "Shuttle pilot" ([TRAFFIC-CONTROLLER.md](TRAFFIC-CONTROLLER.md#controllers-probe-route-craft)). Later add-ons can give their own: a lab researcher, a refueling engineer.
- Every shift is kept, with when it started and ended and why it ended ("taken off", "left KSC", "left Mun Station", "dead"). That's the history the management update (shift lengths, fatigue) will work from.
- Duty doesn't do anything by itself yet. Traffic Controller's controller rules will use it: only kerbals on duty, at KSC or on a station, can control probe burns and earn controller experience.
- Later uses by trait (ideas, not built):
  - **Engineers:** the ore survey (agreed, see [Survey and science roles](#survey-and-science-roles)); refueling (the Refueler) only while an engineer is on duty there.
  - **Scientists:** working linked probes' experiments (agreed, see [Survey and science roles](#survey-and-science-roles)). A station's science lab researches only while a scientist aboard it is on duty. Going on duty starts research and coming off stops it, so the duty button works as the lab's switch.

## Moving crew

Built 2026-11-10, not tried in game. A kerbal can be moved to another part of the craft they're aboard, a docked stack counting as one craft: from the Station window's **Move…** button, in flight or at the space centre. A pilot in a hitchhiker can can't be a station's Traffic Controller, because that needs a part with controls, so moving them to the cupola fixes it.

- Only to a part with a free seat. Any part of the stack counts, the station's own or a docked ship's.
- On a loaded craft it's done the way KSP's own crew transfer does it, portraits and IVA included. On a craft that isn't loaded, the kerbal is moved in its saved record and given the first free seat. At the space centre the game is saved straight after, so the next scene sees the move.
- A kerbal on duty stays on duty, since they haven't left the station. Their seat and job title are checked again straight away, so a pilot moved into the cupola becomes its Traffic Controller.
- Automated shift changes will use the same move, when a kerbal coming off shift and the one taking over swap seats.

## Controller log

Every burn and hand-off a controller oversaw, kept as events, not a score. Traffic Controller writes the entries when the controller rules are built, and its capacity rules count them. Because the events are kept, changing the experience tiers in the settings recounts everyone.

- **Burn:** when, the controller, the body and their post, the probe, the kind of burn (capture, correction, phasing, departure, docking), its priority (1 to 10) and how long it took.
- **Hand-off:** a flight passed from one controller to the next as it leaves one SOI for another. Each side gets an entry: who gave it, who took it, and both bodies.
- Only a kerbal on duty gets entries. Their post and body come from their shift.

## Personnel files

Made up once, the first time Kerbin Resources sees a kerbal, and kept in the save.

### Lifespan and ages

Kerbals live about 200 years. This comes from the *Making a Dollar or Two* stories by Confused Scientist (KSP forums, [Book Three, page 9](https://forum.kerbalspaceprogram.com/topic/163067-making-a-dollar-or-two-book-three/page/9)):

| Stage | From age |
|---|---|
| Child | 0 |
| Adolescent | 20 |
| Adult | 30 |
| Middle-aged | 90 |
| Golden years | 120 |
| Elderly | 150 |

In the stories, kerbals vote and drive at 25, drink at 30, start work at 35 and retire around 130. Jeb is "entering middle age at around 90".

So new kerbals are 35 to 70 when they first appear, and the founding four (Jeb, Bill, Bob, Val) are 70 to 100. Ages count in Kerbin years of 426 days. A campaign of 10–20 years barely ages anyone. Retirement is only shown on the card for now; it will matter with the shifts and fatigue update.

### Birthplaces

Birthplaces are cities and nations from the [Kerbal Powers wiki](https://wiki.kerbalpowers.org/view/Home_-_Kerbal_Powers), the community roleplay's canon of Kerbin's nations. Its cities are dated around 2160–2200, which fits kerbals born in the 2200s. The list has 37 places, for example Ezekialgrad (Zokesia), Aerfel (Sarconia), Nexus (Ikonia), Khosa (Sahr) and Ajemla (Zeoul). Joke entries such as Pipistan are left out.

Kerbal Powers' continents, for reference: Caledon, Doren, Firesvar, Kafrica, Kolus, Veiid.

### The calendar

Dates use the Kerbin calendar from the Historian Expanded mod. It has twelve months, Unnam to Docenam, of 35 and 36 days, making 426 days a year. It has six weekdays, Akant to Flant. Game day 1 is the company's founding: 3 Diznam 2263 by default, an Akant. A save on Earth time shows plain "Year 2263, day 45" dates instead. The day counting is under [How the numbers are worked out](#how-the-numbers-are-worked-out).

### Names

A kerbal is named once, when KSP creates them, and keeps that name:

- **First name:** kept from KSP.
- **Last name:** "Kerman" is swapped for a name from the config's list, unused names first.
- **Families:** with the family chance (15 %), a new kerbal takes the last name of a family already on the roster that has room (at most 4). Everyone sharing a last name is shown as a sibling.
- **The stock four** keep their first names and get new last names too.
- **Existing saves:** kerbals already on the roster are named the first time Kerbin Resources sees the save, outside flight.
- **Left alone:** tourists and kerbals waiting to be rescued keep KSP's names, because their contracts know them by name.
- **Renames:** if a kerbal is renamed anyway (by the player in the Astronaut Complex), the records, the flight log and Traffic Controller's routes follow the new name.
- **How the name is changed:** a craft's seats remember their crew by name, and KSP's own rename doesn't update them. On the next save, KSP can't find the old name, takes the seat as empty and deletes the kerbal. On 2026-11-09 this wiped the crews of three stations from the test save, and it wouldn't load. So Kerbin Resources renames the seats in every craft along with the kerbal, and keeps the kerbal at the same place in the roster. A new kerbal gets their name just before KSP adds them to the roster, before they're in any seat.

### Call signs

Pilots get a call sign from the config's list when they're hired (or first seen), each used once.

## Survey and science roles

Agreed 2026-11-15, not built. An engineer or scientist works only while on duty, the same way pilots do for Traffic Controller. Their job title comes through the `DutyRoster.JobOf` hook, and from a station they can work the instruments of probes nearby.

### Who gets which job

| Trait | On duty at | Job title | What it does |
|---|---|---|---|
| Engineer | a station with a survey or narrow-band scanner, or with a linked probe that has one, seated in a research lab | **Survey Engineer** ("Min Station Survey Engineer") | The ore survey builds only while one is on duty ([SURVEY.md](SURVEY.md)). They work the station's own scanners and those of linked probes. |
| Engineer | KSC | **KSC Survey Engineer** | Works scanners on linked probes in Kerbin's SOI. |
| Engineer | where they surveyed a body that's now done | **Writing the Minmus survey report** | For one Kerbin day of duty, then files the final report ([SURVEY.md “When a body is done”](SURVEY.md#when-a-body-is-done-agreed-2026-11-15-not-built)). After that, with nothing left to survey: **Awaiting orders**. |
| Scientist | a station with experiments aboard, or with a linked probe that has some, seated in a research lab | **Science Officer** ("Min Station Science Officer") | Runs and sends linked probes' experiments ([SCIENCE.md](SCIENCE.md)). |
| Scientist | KSC | **KSC Science Officer** | The same for linked probes in Kerbin's SOI. |

KSC stands in for the tracking station, as it does for KSC Traffic Controllers. An engineer or scientist aboard but off duty does nothing, so the duty button works as the switch.

### Where they have to sit

Each job needs the right kind of seat in the station's own parts, not in a ship docked to it, the way a Traffic Controller needs a command part:

| Job | Seat |
|---|---|
| Traffic Controller | a command part: cupola, pod or cockpit (built) |
| Science Officer | a research lab: a part with `ModuleScienceLab`, the Mobile Processing Lab MPL-LG-2 in stock |
| Survey Engineer | a research lab too (agreed 2026-11-15: the lab is the station's lab and office) |

- The stock lab has 2 seats. A station that surveys and does science needs its scientist and its engineer both in labs, so it's a reason to carry two labs, or to take turns.
- On duty in the wrong seat (a hitchhiker can, a pod), they show as **Awaiting orders**, the same as a pilot outside a command part. *Move…* in the Station window puts them in a lab seat.
- At KSC there's no seat to check.

### The lab while they work probes

A scientist in the lab is also its researcher. Working a probe takes them off the research for that time:

- **During a probe session** (sign in to sign out), the lab works as if that scientist weren't aboard. With another scientist on duty in the lab it's slower; with none it stops. It picks up again when the officer signs out.
- An engineer in the lab doesn't do research, so the survey doesn't slow it.
- The station's Science Officer line in the Station window shows which it is: "Researching", or "Working Probe S-1 (lab paused)".

### A linked probe

A probe (an uncrewed craft) is linked to a post when both of these hold:

- it's in the same SOI as the post (KSC: Kerbin's SOI);
- there's a CommNet path between them (KSC: the probe's link home). This is the same check Traffic Controller's controllers use (its stage B), so a moon in the way breaks the link until the probe comes round.

The add-on checks links every 10 seconds, for unloaded craft too. It writes each stretch of time a probe was linked, and to whom, into the save. The planner reads those stretches to know when a probe's scanners were being worked, because the survey is worked out in the planner from saves.

### Survey Engineer at work

- **Coverage:** the station's own scanners, as today, but only while a Survey Engineer is on duty there. A linked probe's scanners count too, along the probe's own orbit, for the stretches it was linked. A polar probe worked from a station in an equatorial orbit maps the whole body.
- **One craft's scanners at a time** (agreed 2026-11-15): an engineer watches one narrow-band scanner, so they work either the station's own scanners or one linked probe's, never two at once. Both of that craft's scanners count (survey scanner and narrow-band).
- **They watch the scanner that's over new ground.** With two or more scanners to choose from (two probes, or a probe and the station), there's no point watching one over ground already mapped while another is over ground nobody has seen yet. So the engineer switches to the other one:
  - Along the way, the engineer stays with the craft they're watching while its strip is still mapping new ground.
  - Once it's over ground already mapped (for every level its scanners could add), they switch to another craft whose strip is mapping new ground right then.
  - If several are, they take the one mapping the most. If none is, they stay where they are.
  - Switching takes no time. It's another screen in the same office.
  - At the start of a stretch, they take the craft mapping the most new ground.
- **Who decides:** the planner does, because it holds the coverage. The add-on only records the link stretches (which probes were linked to which station, and when). While crediting the time between two readings of the save, the planner steps every candidate craft along its orbit together. At each step it marks only the strip of the craft the engineer is watching. Two engineers on duty watch two different craft, each choosing the same way.
- **Reports** say which craft's scanner did the mapping.
- **Reports** name who did the work and with what: "Bill Rockmore mapped Greater Flats with Probe S-1's narrow-band: ore ≈ 7%".
- **Survey log:** every report goes into Kerbin Resources against the engineer, like the controller log, so experience can count later. For now it's only shown on the data card.

### Science Officer at work

The player is the scientist for the craft they're flying. For any other linked probe, the Science Officer is.

- **Flying the probe yourself:** "Scan now" alerts as today (SCIENCE.md stage 4). Nothing is run for you.
- **Only science that can be sent home counts.** Someone on a station or at KSC can't recover anything, so the officer judges every experiment by its sent value (each part's transmit share, SCIENCE.md "Count as"). They never stop for an experiment that pays only when it's brought back to Kerbin. An experiment whose sent value is under the threshold is passed over, however much it would pay recovered.
- **A linked probe you're not flying:** when it enters a biome or situation where one of its experiments would pay at least the alert threshold *sent home*, the Science Officer runs that experiment and transmits it. The game credits the sent value, the same as pressing *Transmit*, and the subject fills by that much. A screen message says so: "Min Station Science Officer: Probe S-1 sent Gravity Scan from Lowlands (space low): 32".
- **Science log:** each run goes into Kerbin Resources against the officer.

Defaults taken (each can change):

- **Only experiments that can run again** (the gravioli detector, thermometer, barometer, seismic accelerometer, magnetometer, infrared telescope). Mystery Goo and the materials bay are used up after one run and need a scientist to reset them on site, so a Science Officer doesn't run them remotely.
- **The probe needs a link home** to send data, as in the game, as well as its link to the post.
- **Electric charge** isn't checked, because KSP doesn't follow it on craft that aren't loaded.
- **One run per place:** an experiment is run once each time the probe enters a biome or situation, never again while it stays there.

### The officer's work limit

Agreed 2026-11-15. A Science Officer works **one probe at a time**, and one experiment at a time on it. They sign in to the probe's interface, run each experiment, confirm the upload, wait for the transfer, confirm it arrived, and sign out. Each step takes time, in game time (so it passes quickly under time warp):

| Step | Time |
|---|---|
| Sign in to the probe | 15 s |
| Run an experiment | 10 s (stock experiments have no run time of their own, so the magnetometer is the same as the rest) |
| Send it home | the transfer time below |
| Sign out | 15 s |

**Transfer time** is the game's own: data goes in packets, one every `packetInterval` seconds, `packetSize` Mits each, from the probe's fastest antenna (the most Mits a second).

```
transfer = ceil(data ÷ packetSize) × packetInterval         data = baseValue × dataScale Mits for one run
```

| Antenna | Packet | Interval | Gravity scan (60 Mits) |
|---|---|---|---|
| Probe core's own | 2 Mits | 1.0 s | 30 s |
| Communotron 16, 16-S | 2 Mits | 0.6 s | 18 s |
| HG-5, RA-50 | 2 Mits | 0.35 s | 10.5 s |
| RA-100 | 4 Mits | 0.35 s | 5.25 s |
| Communotron 88-88 | 2 Mits | 0.1 s | 3 s |

Worked example: a probe with a Communotron 16 enters Minmus Lowlands (space low), where both the gravity scan (60 Mits) and the thermometer (8 Mits: 4 packets, 2.4 s) still pay.

- Sign in 15 s, then run the gravity scan 10 s and send it 18 s.
- Run the thermometer 10 s and send it 2.4 s, then sign out 15 s.
- In all, **70.4 s** before the officer is free again.

Rules for the session:

- **Order:** the experiment that pays most goes first. Each run takes its biome and situation from where the probe is when the run finishes, so a probe that has moved on while the officer signed in gets the new place's subject. Anything that no longer pays at least the threshold is skipped.
- **Queue:** other linked probes that enter somewhere that pays while the officer is busy wait their turn, first come first served. When the officer is free, the next probe is checked again from where it is now, and is passed over if nothing still pays there. Two officers at a post work two probes at once.
- **Losing the link** (to the post, or the probe's link home) during a transfer loses that transfer: nothing is earned for it. The officer signs out.
- **Flying the probe** yourself, while the officer is working it, ends the session after the current step. From then on you're the scientist.

### Stages

1. **Duty, seats and job titles.** The planner reads who's on duty from Kerbin Resources' records in the save. The Survey Engineer and Science Officer titles are added, given only in a research lab seat. The survey requires an engineer on duty at the station.
2. **Linked probes for the survey.** The add-on checks and records links, and the planner credits linked probes' scanners and names the engineer. The survey log is added.
   Also: a body being done, the report-writing day, the final report, and landmarks hidden until the survey finds them ([SURVEY.md](SURVEY.md#when-a-body-is-done-agreed-2026-11-15-not-built)).
3. **Science Officer.** Probes you're not flying run and send their experiments, one probe at a time, and the lab slows or stops while the officer is busy. The science log is added.
4. **KSC posts** for both, in Kerbin's SOI.

## Settings and lists

**Per save** (KSP's difficulty settings, "KSP Flight Planner" tab):

- the founding date
- ages of new kerbals, and of the founding four
- the family chance and largest family
- switches for new last names and for call signs

**The lists** are in `KerbinResources.cfg`, installed to `GameData/KspFlightPlanner` and free to edit:

- the calendar
- last names
- call signs
- birthplaces
- life stages
- retirement age

## How the numbers are worked out

The day counting behind Kerbin Resources' dates and ages.

### Dates


KSP counts game time in seconds from its Year 1, Day 1. On Kerbin time a day is 6 h (21,600 s) and a year is 426 days (9,201,600 s). The calendar's twelve months of 35 and 36 days add up to 6 × 35 + 6 × 36 = 426, exactly KSP's year, and six weekdays make 71 weeks a year.

Game time 0 is the founding date set in the save's settings (3 Diznam 2263 by default). Every year has the same 426 days, so a date is a single count of days since year 0, day 1:

```
founding day   N₀ = year·426 + (days in the months before its month) + day − 1
day number     N  = N₀ + ⌊ut / 21,600⌋
year           = ⌊N / 426⌋                    (floored, so times before the founding work too)
day of year    = N − 426·year, then months taken off one by one for the month and day
weekday        = (founding weekday + ⌊ut / 21,600⌋) mod 6
```

Example: the founding day is N₀ = 2263 × 426 + (35+36+35+36+35+36+35+36+35) + 3 − 1 = 964,038 + 319 + 2 = 964,359. A hundred days later (ut = 2,160,000 s), N = 964,459: year ⌊964,459 / 426⌋ = 2263, day of year 421 (0-based). The first eleven months hold 390 days, leaving 31: **32 Docenam 2263**. The weekday is (Akant + 100) mod 6 = 4: **Esant**.

On Earth time (24 h days, 365-day years) the months don't fit. Dates are then years and days (365 a year), counted from the same founding year.

- Code: `Calendar.DayNumber`, `.Date`, `.Print` (mod/KspFlightPlanner.AddOns/KerbinResources)
- Source: day and year lengths from KSP's date formatter

### Birthdays and ages


A birthday is made up once: an age picked evenly from the settings' range, plus a random part of a year, counted back from the moment the kerbal is first seen, and rounded down to the start of a day. Age is whole years between the two day numbers. Every year has the same days, so the age ticks over on the birthday.

```
birth = ⌊(now − (age + random 0…1) · 9,201,600) / 21,600⌋ · 21,600
age   = ⌊(N(now) − N(birth)) / 426⌋
```

Example: Jeb is first seen at the founding (ut 0) and is given 92.4 years. His birth is ⌊−92.4 × 426⌋ = ⌊−39,362.4⌋ = −39,363 days, so N = 964,359 − 39,363 = 924,996: year ⌊924,996 / 426⌋ = 2171, day of year 150. That is 8 days past the first four months (142 days): **9 Cinqnam 2171**, a (Akant − 39,363) mod 6 = 3: **Dovant**. On founding day he's ⌊39,363 / 426⌋ = **92**.

- Code: `Personnel.NewRecord`, `Calendar.YearsBetween` (mod/KspFlightPlanner.AddOns/KerbinResources)
- Source: none (game rules)

## Code map

| Where (`mod/KspFlightPlanner.AddOns/KerbinResources/`) | What |
|---|---|
| `FlightLog.cs`, `KerbinResourcesScenario.cs` | The flight log; watching undocking, launches and dockings; the save |
| `Personnel.cs` | Personnel files: birthday, birthplace, hire date, call sign, naming and families |
| `Calendar.cs` | The calendar, and reading `KerbinResources.cfg` |
| `KerbinResourcesSettings.cs` | Per-save settings |
| `DutyRoster.cs` | The duty roster and its shifts; finding where a kerbal is |
| `ControlLog.cs` | The controller log: burns and hand-offs |
| `CrewWindow.cs` | The window and the data card |
| `CrewMove.cs` | Moving a kerbal to another part of their craft, loaded or not |
| `KerbinResourcesService.cs` | kRPC "PlannerCrew": `Flights`, `OpenTrips`, `Shifts`, `Controllers`, `PutOnDuty`, `TakeOffDuty` |

## Credits

- Calendar month and weekday names: the Historian Expanded mod.
- Lifespan and life stages: *Making a Dollar or Two* by Confused Scientist (KSP forums).
- Birthplaces: the Kerbal Powers wiki and its community.
