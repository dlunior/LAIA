# LAIA (Local AI ATC): design and roadmap

The reference for what LAIA is, what has been decided, what is built and what comes next. Keep it up to date
whenever a decision is made or a step is finished. Last updated: 9 November 2026 (v0.1.51).

---

## 1. What LAIA is

LAIA gives Microsoft Flight Simulator 2024 realistic AI traffic and, later, AI air traffic control. The heavy
work runs on a second PC on the home network, so the sim PC stays fast.

| Part | Where | What it does |
|---|---|---|
| **LAIA Sim** | Sim PC (d-gaming-pc), tray app | Talks to MSFS (SimConnect). Draws the AI aircraft, reads airport layouts, procedures, AIG models and AIG schedules from MSFS, and sends them to the server. Copies the server's logs to `%APPDATA%\LAIA`. Has the tray **Update LAIA (both PCs)** button. |
| **LAIA Server** | ATC PC HEROVIII (192.168.0.211, RTX 2080 Ti, Ryzen 9 3950X, 64 GB), Windows service | Runs the traffic engine, speech recognition (Whisper) and voices (Kokoro), and later the language model for ATC. Hosts the control web page at port 47801. |

### Goals (the user's own words, kept)
- **Immersion is the point.** The traffic should feel like the real airport, with real airlines, aircraft, routes, gates and timing.
- **Fixes and features must work at every airport.** KLGA is only where testing happens. The one exception allowed is a real regulation that applies to one airport only (for example the LGA perimeter rule).
- **Follow the real rules:** FAA JO 7110.65 (controller procedures), the FARs (for example 91.117 speeds) and the AIM (pilot technique).
- **Use real data over invented data:** real schedules (AIG flight plans, AirLabs), real gates (AirLabs and SimRouteGen's pool) overriding the scenery developer's gate assignments, and later learned airport behaviour from real ADS-B data.
- **Finish the AI traffic before any AI radio interaction.**
- **Sequencing starts long before the approach,** using speed control and temporary vectors with a rejoin to the route, accounting for every aircraft in the sky, LAIA's or not.
- **Frequencies are planned and split realistically,** before a sector gets busy, never at random.
- **Use the 2080 Ti for more than voice.**
- **Respect the user's budget:** keep API call counts visible, and cache and reuse data.

### About the user
- Not a coder. Always deliver finished installers, and explain any step outside the app in plain English.
- Flies airline jets (mostly Delta, United and Air Canada) with SimBrief plans and live weather, mostly in North America and the Caribbean.
- Is a VATSIM controller, so knows real facility practice. Take their facility knowledge seriously.

---

## 2. Working conventions

- **Code:** GitHub `dlunior/LAIA` (public for now; will be made private before sharing). .NET 8.
- **Build:** GitHub Actions only (there is no local dotnet in the cloud sessions).
- **Every push to main runs CI:**
  1. The offline traffic tests (`src/Laia.Tests`, a simulated hour of busy traffic plus rule checks).
  2. A build of both installers.
  3. A prerelease `v0.1.<run number>`. Add `[skip ci]` to the commit message to skip a build.
- **Updates:** the user clicks **Update LAIA (both PCs)** in the tray. The server updates itself, then the sim PC updates. If the sim-side installer doesn't finish within 5 minutes, LAIA Sim restarts itself (0.1.50).
- **Logs (sim PC):** `%APPDATA%\LAIA` holds:
  - `laia-sim.log`
  - `server.log` (copied every 60 s)
  - `server-setup.log`
  - `setup.log`
  - `airports\*.json` (layouts read from MSFS)

  Cloud sessions read these through the connected-folder bridge.
- **Test after each update:** **Remove traffic**, then set the level again on the control page.
- **Release notes go to the user, not into the app.**

---

## 3. What is built (status at v0.1.51)

### Rendering (LAIA Sim)
- Frame-synced position updates with a steady clock, so there is no double-update stutter. Each aircraft's resting pitch is measured when it is placed in the sim, so noses sit level while taxiing.
- Gear is animated on the gear's actual positions. Lights follow AIM 4-3-23, with no cabin-light changes on takeoff or landing.
- The exact AIG model and livery is used when the schedule names it.

### Airport knowledge (read from MSFS)
- Taxi network, parking (sizes, airline codes), runways, SIDs, STARs and approaches, via the SimConnect facility API.
- Airline codes on stands, including partner codes (DALX = Delta Connection operators and so on).

### Traffic engine (LAIA Server, `TrafficCore`)
- **The flight cycle:** gates, pushback, engine start, taxi, hold short, line-up, takeoff, SID, then STAR, approach, landing, exit, taxi-in and gate.
- **Pushback planner:** it looks behind each stand for a clean push and turn, avoiding other stands, roads and dead ends.
- **Real schedules:**
  - LAIA follows the AIG flight plans installed on the sim PC (the exact model, registration and flight number). Times are UTC, minutes from Monday 00:00 (verified).
  - Turnarounds use the same airframe.
  - Aircraft that landed and are waiting for their next flight sit at gates, overnight too.
  - The traffic level thins whole airframes.
- **Real gates:**
  - Sources: AirLabs (each flight's terminal and gate, learned and kept per airport) plus SimRouteGen's shared gate pool (`dfg.dlunior10.workers.dev/shared`, public read, version-checked every 6 h).
  - They override the scenery's codes. A flight with a known gate today goes to exactly that gate, and regionals park at their brand's gates.
- **Rules (0.1.51):**
  - **7110.65 5-7 speeds:** controller-style profile 250 → 210 → 180 → 170 to a 5-mile final. Minimum assignable speeds: jets 210/170, turboprops 200/150. No assigned speeds inside 5 nm.
  - **91.117 speeds:** 250 kt below 10,000 ft; 200 kt within 4 nm and below 2,500 ft of a Class C/D airport.
  - **Published speed limits** on SIDs, STARs and approaches are flown.
  - **CWT wake categories A–I:** spacing on final, and 3-9-6 departure wake times.
  - **Same-runway departures:** the 6,000 ft rule, and diverging departures 5-8-3.
  - **Crossing-runway operations (3-9-8, 3-10-4):** a crossing runway is used for departures at Normal and Busy levels when its departures stay clear of the final.
  - **Runway choice:** tailwind limit 5 kt, crosswind limit 25 kt.
  - **Stabilized approach by 1,000 ft (AIM 5-4),** or the aircraft goes around.
  - **Holding (AIM 5-3-8)** far out. 360s are used only within 15 nm.
  - **Taxi speeds:** 20 kt straight, 16 kt for heavies, slower in turns, 10 kt on the ramp.
- **Safety nets:** a stuck-aircraft watchdog (reroute after 20 s, another gate after 2 min, removed after 5 min with the reason logged), ground separation and intersection yielding.

### Known gaps / open items
- **The CWT table is only partly filled in.** Distances for leaders A–E are taken from JO 7110.126A. Leaders F–I fall back to the 3 nm radar minimum, because the official PDF table didn't extract cleanly. Fill in the full official table.
- **Crossing-runway operations and holding** are only tested in isolation. LaGuardia is their first real test.
- **Pushback problems at KLGA** (0.1.49 survey): 8 stands where the push "turns among the gates" (Parking 61–86) and 1 that "faces a dead end" (Gate 46).
- **Some real-gate airline codes look like codeshares** (WJA and VIR at KLGA). Check them against the AirLabs codeshare flag.
- **The arrival spawn distance is 7–40 nm.** This will be replaced by the TRACON-wide world (step 1).

---

## 4. Roadmap (agreed order)

### Step 1: The whole sky, simulated in layers, with prediction

All scheduled traffic flies logically, not just at the user's departure and arrival airports. Every flight in
the installed AIG schedules exists and moves according to its schedule. Only the aircraft near the user are drawn in
the sim. The user's decision (9 Nov 2026): "they all don't need to be rendered in the sim but definitely flying logically".

Three layers, with a flight moving smoothly from one to the next:

| Layer | Where | How it's simulated | Cost |
|---|---|---|---|
| **Network** | Every scheduled flight in the AIG schedules (the whole region, about 480,000 legs a week) | Its position along the route from the schedule's times: at the gate, taxi, climb, cruise, descent, landed. Its gate time comes from the schedule. | Tiny: thousands of flights, a few milliseconds |
| **Airspace** | Within about 250 nm of the user, plus the user's departure and arrival TRACONs | Full flight paths (SID, airways or a direct route, STAR, approach), speeds and winds; trajectory prediction (ETAs at fixes and runways); sequencing; sectors and frequencies. Every airport in those TRACONs is active: around LGA that is JFK, EWR, the satellites (ISP, HPN, TEB …) and overflights. | Modest: the ATC PC's CPU handles hundreds of flights |
| **Rendered** | Within about 25–40 nm of the user | Everything above, plus ground movement on the real taxiways and gates, drawn in MSFS. | As today |

- **Moving between layers:** a flight entering the airspace layer keeps its position, altitude, speed and schedule. On the ground at an airport without a layout read from MSFS, gate and taxi times are simulated, not drawn.
- **Arrivals start in cruise:** they're already flying when they reach the airspace layer (top of descent, then the STAR). Departures climb all the way out of the area.
- **Trajectory prediction** for every flight in the airspace layer: its ETA at each fix and at the runway, from its route, speed profile and winds, recomputed every few seconds.
**What the user sees (what is drawn in MSFS).** This is the user's decision (9 Nov 2026). Only the user's fields get gate and ground traffic. Neighbouring fields get runway, approach and departure traffic only.

| | Drawn |
|---|---|
| **User's fields** (SimBrief departure and arrival, or the airport the user is at) | Everything: parked aircraft at their real gates, pushback, taxi, runway, approach and departure. The destination's gates are filled once the user is about 80 nm out, before the airport is in view. The departure field's ground traffic stops being drawn once the user is about 15 nm away. |
| **Neighbouring fields** (inside the airspace layer) | Only the runway and the air. Departures appear lined up on the runway and take off. Arrivals fly the approach, land, and disappear on the runway once slowed to taxi speed. Their ground part is simulated, not drawn. |
| **Airborne traffic** (anyone's) | Within 40 nm horizontally and ±10,000 ft of the user, matching a TCAS display's range, so the user's TCAS and ND show the real picture, not just what's close enough to see. Drawn at 40 nm and removed at 45, so aircraft don't flicker at the edge. |

- **Object limit:** a cap on the number of drawn objects, set by traffic level, protects the sim PC. If the cap is reached, the aircraft that matter most are kept: closest, converging with the user's path, at the user's runway, or about to enter the user's view.
- **No pop-in at neighbouring runways:** if the user is within about 3 nm of a neighbouring runway and low enough to see it, a departure there is drawn earlier, at the hold short line, and an arrival is removed only after it has turned off the runway.

- **Reading the schedules:** the server reads all of them at once (one pass over the AIG files, a few seconds), instead of one airport at a time.

**Hybrid sources (the user's decision, 9 Nov 2026).** Traffic at neighbouring airfields and en route can come
from the AIG schedules. Traffic into and out of the user's own fields (the SimBrief departure and arrival, or the
airport the user is at) must be as close to the real world as possible:

| | User's departure and arrival fields | Everything else |
|---|---|---|
| **Which flights, when** | Today's real flights, with their real delays and estimated times (AirLabs, already 2 calls per field every 6 h, counted) | AIG schedules |
| **Where they are now** | Real live positions of airborne inbound and outbound flights (OpenSky, free with the user's account), so arrivals already in the air join at their real position | Their position from the schedule |
| **Gate** | The real gate (AirLabs, plus the SimRouteGen pool) | Real gates where known, else the scenery's |
| **Aircraft and livery** | The AIG leg with the same flight number (exact model and registration). Otherwise an installed livery of that airline, on a type the SimRouteGen pool says flies that route. | The AIG model and livery |

- **No duplicates:** at the user's fields, the real-world list replaces the AIG schedule. A real flight whose other end is a neighbouring airport is the same flight there, so it isn't flown twice.
- **Fallback:** if AirLabs or OpenSky is unavailable, or out of calls, the AIG schedule is used for that field.
- **No AeroAPI** (it is paid per call), unless the user decides otherwise.

### Step 2: Arrival sequencing (like a real arrival manager)
- **Landing order and times:** build them from the predictions, respecting CWT spacing, the runway's landing rate and departure gaps.
- **Delay is absorbed in this order:**
  1. Speed control far out (cruise and descent).
  2. Temporary vectors or a dogleg, then "direct to [fix], rejoin the arrival".
  3. Holding as the last resort.
- **The plan is frozen** as aircraft get close, so nobody is re-sequenced on final.

**A sequence that is dynamic but stable (the user's decision, 9 Nov 2026).** The sequence keeps adapting to reality
(winds, delays, a slow climber, the user), but it must not churn so much that aircraft keep needing new instructions.
- **Far out (before the freeze, about 30–40 minutes or ~150 nm from landing):** the order may change, but two aircraft only swap places if it saves real delay (at least about a minute in total), and an aircraft changes position at most a couple of times.
- **Inside the freeze:** the order is fixed. Only the timing is fine-tuned, and a landing time moves by at most about 30 s per update.
- **Instructions:** each aircraft gets a new speed or vector at most every 3–5 minutes, except for safety (a real loss of spacing). One clear plan per aircraft (for example "210 knots until DIXIE", or "turn left heading 240, expect direct CAMRN in 10 miles") rather than constant nudges.
- **The user's slot is the most stable of all,** because the user can't react as instantly as an AI. It is only moved for a real reason (the user being well off their predicted time, or a go-around).

### Step 2b: The live map (on the control page)
An interactive map on the ATC PC's control page, which works from any PC, tablet or phone on the home network.
- **Colours:** drawn (injected) traffic in one colour, the user in another, and simulated-but-not-drawn traffic in a third. Optionally a fourth for the other traffic from step 3 (injected by other programs, multiplayer).
- **Click an aircraft** to see its callsign, type, airline, origin and destination, altitude and speed, phase, sector and frequency, its sequence number and landing time (ETA and scheduled), the delay being absorbed and how (speed, vector, hold), and its current instruction.
- **Procedure overlays:** the SID, STAR and approach the selected aircraft (or every drawn aircraft) is flying, drawn on the map with its fixes and the published altitude and speed limits.
- **Sequence panel:** the arrival sequence for each runway (order, landing times, spacing, delay, frozen or not), and the departure queue. Changes are highlighted, so you can watch the sequence adapt.
- **Layers to switch on and off:** the drawn-traffic radius (40 nm), sector boundaries with the open splits and their frequencies, the airspace layer (250 nm), runways in use, and holding patterns.
- **Style:** a dark map, as in SimRouteGen. Updated about once a second.

### Step 3: Every aircraft in the sky
- **What LAIA Sim reads:** every other aircraft MSFS knows about within about 100 nm (injected, live traffic, multiplayer), plus the user's aircraft. It sends them to the server as traffic LAIA can't control.
- **How they're used:** their paths are predicted, and LAIA's flights are sequenced and spaced around them.
- **The user gets a slot too,** from their SimBrief plan and live position.

### Step 4: Planned departures
- Pushback and taxi times are planned so each departure reaches the runway as a gap opens, including crossing-runway departures.

### Step 5: Sectors, workload and frequency planning (with traffic management)

**Sector map**
- Center boundaries come from public FAA data. TRACON and tower internal lines aren't published, so they're built from each airport's procedures:
  - **Arrival feeders:** one per arrival corridor (corner post) group.
  - **Final:** per runway where there is more than one.
  - **Departures:** by SID direction.
  - **Satellites:** by area.
  - **Altitude layers** between them.
  - **Tower:** clearance, ground and local.
- Real frequencies come from the FAA frequency data LAIA already loads.

**Fixed split plans per TRACON, never improvised**
- STARs are grouped by the direction they arrive from. The ordered plans are 1 feeder, then 2 feeders, then 3 if ever needed.
- Each group is a continuous slice of the compass (no feeder works opposite corridors).
- Groups are balanced by real scheduled load, not simply half the STARs each.
- The same plan is used every session; it is only recomputed when the procedures change.
- Departures, final and satellites get equivalent ordered plans.

**Act before it gets busy**
- Predict the next 60 minutes of each sector: the aircraft in it, the instructions each will need, and radio seconds per minute.
- Open the next split when a sector is predicted to pass its limit in about 15–20 minutes.
- Combine only when the next 45 minutes stay well under the limit.
- The trigger for combining is much lower than the one for splitting, and a split stays open for at least 30–45 minutes, so it never flip-flops.

**A realistic handover**
- Aircraft already in a conversation finish it before they're moved.
- The others move to the new feeder at their next natural checkpoint.
- The control page shows each split and why it happened.

**Traffic management**
- If even the fully split configuration is overloaded, apply miles-in-trail, speed control upstream or departures held at the gate, on the corridors causing the overload.

**Handoffs**
- Every AI flight is handed from sector to sector at real boundaries and altitudes. The user's handoffs give the frequency of whichever sector is actually open.

### Step 6: Learned airport profiles (GPU training)
- **Sources:** adsb.lol daily history dumps (main source) and the user's OpenSky account for live recording (OAuth2 client `credentials.json`, to be added to LAIA Sim settings).
- **Learned per airport:**
  - Runway configurations used in each wind.
  - Real approach paths and vector patterns.
  - Arrival corridors (used to check and adjust the STAR groups and sector limits).
  - The speeds actually flown.
  - Typical delays.
- **Training** runs on the 2080 Ti overnight. Learned data is shareable with other users, all in the background with no user action.

### Step 7: The radio
- **The language model** (on the GPU) understands the user's readbacks and produces the controller's responses in 7110.65 phraseology.
- **AI frequency chatter:**
  - The AI flights and controllers talk on each open frequency, in different voices.
  - Their transmissions are timed to the actual sequence: an instruction is spoken when it is actually given.
  - One speaker at a time per frequency, with a queue, readbacks after the instruction, and the occasional "blocked" or step-on when a frequency is jammed.
- **The user hears only the frequency they're tuned to.**
- **First Officer mode, PTT button, transcript and audio-device choice** (already in the user's requirements).
- **Real-world ATIS** for runway assignments at major airports.

### Later / nice to have
- AirLabs as a supplement for airlines that AIG doesn't cover.
- Full CWT table, realistic schedule delays (a small spread, mostly late), minimum turnaround times by aircraft type, and published missed approaches.
- Approach type chosen by weather (visual or ILS), and published SID/STAR altitudes.
- Cruise traffic. The user calls it a nice-to-have.

---

## 5. Data sources

| Data | Source | Cost / limits |
|---|---|---|
| Airport layout, procedures | MSFS (SimConnect facility API) via LAIA Sim | Free. Kept per airport and re-read after 28 days. |
| AI models | AIG (Community `aig-aitraffic-oci`) | Free. A catalog of installed liveries. |
| Schedules | AIG flight plans (`Traffic Files\AIGFP\*.aigfp`) | Free. Read per airport on demand. |
| Real flights and gates | AirLabs (user's key) | 2 calls per airport per 6 h, cached and counted. |
| Shared gate pool | SimRouteGen Worker `/shared` (repo `dlunior/simroutegen-worker`) | Free. Public read. Version checked every 6 h. |
| Frequencies | FAA data (cycle-updated) | Free. |
| Runways (fallback) | Public runway database | Free. |
| History (future) | adsb.lol dumps, OpenSky (user account) | Free. |
