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

Every crewed flight is logged with who was aboard. The player's own flights count as well as Traffic Controller's routes. A crewed craft undocking (or launching) opens a trip, and docking somewhere else logs it. The rules are in [MATH.md §11.12](MATH.md#1112-crew-rules-and-the-flight-log).

Later: decide which half of an undocking is travelling by which one thrusts first (engines or RCS), with mass as the fallback.

## Duty roster

Who's on duty, and where. A kerbal goes on duty where they are: **at KSC** (Kerbin's SOI, standing in for the tracking station) if they're in the Astronaut Complex, or **aboard their craft** (its SOI) if they're on one. Only hired crew can go on duty. The player puts kerbals on and off duty from the data card (or the planner, through kRPC).

- A kerbal comes off duty by themselves when they leave their post: a KSC kerbal put on a ship, a station kerbal who leaves the station, or one who dies or goes missing. Checked every 10 seconds.
- **The post is the station, and the seat is tracked separately.** A kerbal aboard a docked stack is on duty at the stack's main craft, the station KSP named the stack after. KR also tracks which craft their seat belongs to: the station's own parts, or a ship docked to it. Each docked port remembers the craft it came from, so the parts below it belong to that craft. A pilot in a shuttle docked at Min Station shows as "On duty at Min Station, in Min Bus mk1 (a docked ship, not the station's own parts)". The seat is checked every 10 seconds, so moving between the station and a docked ship updates it. Traffic Controller will only take controllers from the station's own parts.
- The shift follows a part of the station, so a docking or undocking that changes the stack's id or name doesn't end it. A kerbal who leaves in a ship undocking from the station comes off duty ("left Min Station").
- **Job titles.** Each shift has a job title, given by the add-on whose job it is through a hook (`DutyRoster.JobOf`). With no title, the kerbal shows as **Awaiting orders**. The title is checked when they go on duty and every 10 seconds after. Each change is kept in the shift's job history ("Traffic Controller then Shuttle pilot"), for shifts and fatigue later. The title shows on the data card, in the roster and in the shift list.
- Titles today come from Traffic Controller, for pilots only ([TRAFFIC-CONTROLLER.md](TRAFFIC-CONTROLLER.md#controllers-probe-route-craft)). Later add-ons can give their own: a lab researcher, a refueling engineer.
- Every shift is kept, with when it started and ended and why it ended ("taken off", "left KSC", "left Mun Station", "dead"). That's the history the management update (shift lengths, fatigue) will work from.
- Duty doesn't do anything by itself yet. Traffic Controller's controller rules will use it: only kerbals on duty, at KSC or on a station, can control probe burns and earn controller experience.
- Later uses by trait (ideas, not built):
  - **Engineers:** the ore survey's engineer requirement, and refueling (the Refueler), only while an engineer is on duty there.
  - **Scientists:** a station's science lab researches only while a scientist aboard it is on duty. Going on duty starts research and coming off stops it, so the duty button works as the lab's switch.

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

Dates use the Kerbin calendar from the Historian Expanded mod. It has twelve months, Unnam to Docenam, of 35 and 36 days, making 426 days a year. It has six weekdays, Akant to Flant. Game day 1 is the company's founding: 3 Diznam 2263 by default, an Akant. A save on Earth time shows plain "Year 2263, day 45" dates instead. The day counting is in [MATH.md §12](MATH.md#12-calendar-dates-and-ages).

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
| `KerbinResourcesService.cs` | kRPC "PlannerCrew": `Flights`, `OpenTrips`, `Shifts`, `Controllers`, `PutOnDuty`, `TakeOffDuty` |

## Credits

- Calendar month and weekday names: the Historian Expanded mod.
- Lifespan and life stages: *Making a Dollar or Two* by Confused Scientist (KSP forums).
- Birthplaces: the Kerbal Powers wiki and its community.
