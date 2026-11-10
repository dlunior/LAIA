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
- **Research first (the user's rule, 9 Nov 2026).** The user is not the only source of truth. When a concept is added to the design, research it independently (FAA orders, AIM, FARs, manufacturer and safety-foundation guidance, real ATC tools) and check the user's idea against what's found. Write the numbers and the sources into this document. Where findings differ from what was planned, change the plan and say so.

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
  - **Holding (AIM 5-3-8)** far out. 360s are used only within 15 nm. Today's holds use 200 kt up to 6,000 ft and 230 kt above. The full AIM limits are 200 kt up to 6,000 ft MSL, 230 kt from 6,001 to 14,000 ft (210 where charted), and 265 kt above 14,000 ft, with one-minute legs at or below 14,000 ft and 1.5 minutes above. The 265 kt and 1.5-minute rules still need adding.
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
- **Energy management (the user's lesson, 9 Nov 2026: a jet can't slow down and descend at the same time).** This is where AI and app ATC usually fail. LAIA's flight model, its prediction and its instructions all use the same energy budget. Researched 9 Nov 2026; sources at the end of this section.

  **What the sources say**

  **Descending**
  - At idle, clean, at normal descent speed, a jet needs about 3 nm per 1,000 ft.
  - Add about 2 nm per 10 kt of average tailwind; subtract for headwind.
  - Add about 1 nm for a heavy aircraft.

  **Slowing down**
  - Level flight, clean, idle: about 1 nm per 10 kt. So 300 → 250 → 210 adds about 10 nm.
  - Level flight, approach flaps: 10–15 kt per nm.
  - Level flight, gear down and landing flaps: 20–30 kt per nm.
  - On a 3° glide path, clean: usually not possible at all.
  - On a 3° glide path, gear down and landing flaps: 10–20 kt per nm (Airbus and FSF plan with 10).
  - Heavier aircraft slow more slowly.

  **Descent rates while slowing**
  - About 2,500–3,000 fpm from top of descent at high speed.
  - Falls to about 500–700 fpm while slowing to 250 near 10,000 ft.
  - Then about 1,500 fpm at 250 kt.
  - On a 3° path: about 300 ft per nm (groundspeed ÷ 2 ≈ fpm).

  **Checkpoints pilots use**
  - About 10,000 ft and 250 kt at 30–40 nm from touchdown.
  - About 35 nm for a straight-in, 30 nm for a base entry, 25 nm for a downwind entry.

  **Speedbrakes**
  - They help, but are weak below about 250 kt.
  - Not used below 1,000 ft or with landing flaps.
  - On Airbus they raise the minimum speed.

  **ATC rules**
  - 7110.65 5-7-2: "Simultaneous speed reduction and descent can be extremely difficult, particularly for turbojet aircraft." Controllers must say which comes first, with "then": "reduce speed to 250, then descend and maintain …", or "descend and maintain …, then reduce speed".
  - 7110.65 5-7-1: speed changes "are not achieved instantaneously", and the pilot may refuse one they consider excessive.
  - When descending below 10,000 ft from above 250 kt, the pilot may have to level off to slow down (91.117). ATC must expect that.
  - No speed adjustments in holding, or inside the final approach fix or 5 miles, whichever is closer to the runway.

  **Approach speeds (FSF / NTSB)**
  - Maximum speed at the final approach fix ≈ approach speed + 10 kt per nm × (distance of the fix from the threshold − 3 nm). That leaves the last 3 nm to slow and stabilize by 1,000 ft. "170 to a 5-mile final" only fits jets whose approach speed is 150 kt or more; slower jets need an earlier speed.
  - No speed restriction within 3–4 nm of the final approach fix.
  - Stabilized by 1,000 ft (IMC) or 500 ft (VMC): speed Vref to Vref + 20 kt, sink rate no more than 1,000 fpm, on the localizer and glideslope. Otherwise go around.

  **The model LAIA uses**
  - **Deceleration available, in kt per nm** = what the configuration gives in level flight, minus what the descent takes:
    - About 10 clean, about 12.5 with approach flaps, about 25 with gear and landing flaps.
    - Minus about 1 kt per nm for every 32 ft per nm of descent gradient. A 3° path (about 318 ft per nm) takes about 10, which reproduces the numbers above: clean on 3° ≈ 0, landing configuration on 3° ≈ 15.
    - Speedbrakes add about 3 kt per nm above 250 kt and about 1.5 below.
    - A negative result means the aircraft can't slow on that path: it must level off, shallow out, or get more miles.
  - **Configuration by speed:** flaps follow the type's speed schedule; gear goes down around the final approach fix (or earlier when needed to slow).
  - **Every arrival's descent is planned with these numbers:**
    - Top of descent = 3 nm per 1,000 ft, plus 1 nm per 10 kt of slowing, plus the wind.
    - A 10,000 ft / 250 kt point about 30–40 nm out.
    - Level or shallow pieces where it slows.
    - The prediction uses the same plan, so the sequence knows how long the slowdowns really take.
  - **The flight model obeys it:** speed and descent no longer change independently, as they do today. This is fixed first, in step 1.

  **Instructions are checked against the model before they're given**
  - Speed and descent are given in an order with "then", never as an impossible combination. There's no "descend to 3,000 and slow to 180" five miles from the turn.
  - An aircraft too high or too fast for the distance left gets more track miles (a longer downwind, a wider base) or a stepped plan, not a demand.
  - The maximum speed at the final approach fix comes from the formula above, per aircraft.
  - The same checks apply to the user's guidance in traffic mode and the user's instructions in ATC mode.

  **Sources**
  - Airbus, "Aircraft Energy Management during Approach" (Flight Operations Briefing Note, via the FAA)
  - Flight Safety Foundation, ALAR Briefing Note 4.2: Energy Management
  - FAA JO 7110.65, Chapter 5 Section 7: Speed Adjustment
  - Airbus Safety First, "Control your speed during descent, approach and landing" (SKYbrary)
  - 737 pilots' rules of thumb (PPRuNe)
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
- **Modelled on real arrival managers.** Researched 9 Nov 2026: FAA TBFM, and the MAESTRO arrival manager used on VATSIM.
  - Aircraft join the sequence about 150–200 nm out, with an ETA and a scheduled time of arrival (STA).
  - The sequence passes through set states, timed against the feeder (meter) fix at the TRACON boundary and against landing:

    | State | When | What may change |
    |---|---|---|
    | **Unstable** | More than about 12 min from the feeder fix | Order and times may be recalculated, under the swap rules below |
    | **Stable** | Within 12 min of the feeder fix | The order is kept; times are only fine-tuned |
    | **Super-stable** | Within 8 min of the feeder fix | No changes, except putting a missed approach back in |
    | **Frozen** | Within 5 min of landing | No changes |
  - **Delay is shown as time to lose (or gain)** against the STA. Controllers absorb it with speed, delaying vectors or holding, and gain time with shortcuts. A sector hands its aircraft on at the feeder fix's STA, with no delay left.
  - **Rescheduling everyone ("rippling") is avoided** once metering is running, as TBFM guidance says.
- **Unstable, the order may still change,** but two aircraft only swap places if it saves real delay (at least about a minute in total), and an aircraft changes position at most a couple of times.
- **Stable onward, the order is fixed.** A landing time moves by at most about 30 s per update.
- **Instructions:** each aircraft gets a new speed or vector at most every 3–5 minutes, except for safety (a real loss of spacing). One clear plan per aircraft (for example "210 knots until DIXIE", or "turn left heading 240, expect direct CAMRN in 10 miles") rather than constant nudges.
- **The user gets no priority: a sequence is a sequence (the user's decision).** The user is sequenced like any other flight, in the normal order (by predicted arrival, with wake and runway rules), so others don't always yield to them.
- **The user's slot is the most stable,** because the user can't react as instantly as an AI. Once set, it is only moved for a real reason: the user being well off their prediction, or a go-around.
- **Delay for the user is speed control first, then a small vector or extended downwind. A hold for the user is extremely rare,** only when nothing else can make the spacing. The AI flights take the holds.

### Step 2a: Radar vectors, the make-or-break of realism (top priority)

The user's words (9 Nov 2026): radar vectors matter more for realism than the ground traffic. That means the downwind, the base and the vectors to final, and vectors that never conflict with a neighbouring airport's airspace (LGA arrivals must not tangle with EWR's).

**Learned real vector patterns are built together with the sequencer,** not left for the general learning step. They're taken from the radar-vector part of step 6, using real ADS-B tracks:
- Downwind, base and final-intercept paths, the altitudes on each, and where controllers extend or turn short, per runway configuration.
- Each pattern is kept with its normal spread, so the sequencer's choices (extending a downwind, turning base early or late) all fall within what really happens there.

**Each airport stays in its own airspace**
- **An envelope per airport and configuration:** from the real tracks, LAIA learns the area, and the altitudes, each airport's vectored traffic actually uses. Generated vectors stay inside it.
- **Other airports' envelopes are respected,** for whichever configuration each neighbour is using at that moment. Around New York, that keeps LGA's downwind clear of EWR's and JFK's finals.
- **Where real traffic overlaps, it's separated by altitude** (one airport's arrivals over another's), and LAIA uses the same altitudes real controllers do there.

**A cross-airport conflict check**
- Every planned path is checked against every other airport's planned paths ahead of time, using radar separation (3 nm or 1,000 ft) and the wake rules.
- A conflict is solved the way the real facility does: an altitude restriction first ("maintain 3,000 until past …"), then a different vector inside the envelope, then speed.

**Before an airport has been learned**
- The generic vectors are shaped to stay out of neighbouring airports' final approach corridors (the extended centreline out to about 15 nm, within about 3 nm and 3,000 ft).
- If needed, the pattern is tightened or flown on the other side of the airport rather than crossing them.

**On the map:** the airport envelopes, and any predicted cross-airport conflict and how it was solved, can be switched on as layers.

### Step 2b: The live map (on the control page)
An interactive map on the ATC PC's control page, which works from any PC, tablet or phone on the home network.
- **Colours:** drawn (injected) traffic in one colour, the user in another, and simulated-but-not-drawn traffic in a third. Optionally a fourth for the other traffic from step 3 (injected by other programs, multiplayer).
- **Click an aircraft** to see its callsign, type, airline, origin and destination, altitude and speed, phase, sector and frequency, its sequence number and landing time (ETA and scheduled), the delay being absorbed and how (speed, vector, hold), and its current instruction.
- **Procedure overlays:** the SID, STAR and approach the selected aircraft (or every drawn aircraft) is flying, drawn on the map with its fixes and the published altitude and speed limits.
- **Sequence panel:** the arrival sequence for each runway (order, landing times, spacing, delay, frozen or not), and the departure queue. Changes are highlighted, so you can watch the sequence adapt.
- **Layers to switch on and off:** the drawn-traffic radius (40 nm), sector boundaries with the open splits and their frequencies, the airspace layer (250 nm), runways in use, and holding patterns.
- **Style:** a dark map, as in SimRouteGen. Updated about once a second.

### Step 2c: Guidance for the user in traffic mode (no AI ATC)
In traffic mode, with no AI ATC, the user still needs to know where they fit, so they can fly to it, and when to stop or wait on the ground. The user's decision, 9 Nov 2026.
These are hints, not clearances: the wording is plain ("aim for", "wait", "after the landing A321"), never ATC phraseology.

**In the air**
- **Sequence:** "#4 for runway 22, following DAL1132 (A321), 7 nm ahead".
- **No time targets (the user's decision):** a new pilot shouldn't have to hit a time at a fix. The user still takes their normal turn in the sequence (no priority). The plan is built so the user's part can be flown with simple instructions, and the AI flights do the fine-tuning (exact speeds, vectors, holds). The guidance only ever asks for simple things a controller might ask for:
  - **A speed,** now and next: "210 kt now", "you can keep 250 until CAMRN", "slow to 180, you're catching the A321".
  - **The expected STAR, approach and runway,** from the runway in use.
  - **Rarely, a simple delay:** "extend your downwind 2 miles". A hold ("expect one turn in the hold at BRAND") is extremely rare for the user, only when nothing else can make the spacing.
- **Speed hints are few and steady:** at most one change every few minutes, always a round number, never a constant stream of small corrections.
- **Who's waiting for you:** the aircraft holding or being slowed for your slot ("UAL455 holding at BRAND for you, 4 min").
- **When the slot changes:** a short note explaining why, for example "#5 now, a 777 needs extra spacing ahead of you". This is as rare as the sequence-stability rules allow.

**On the ground**
- **Pushback:** "wait, DAL455 is taxiing behind you", or "clear to push, tail to the south".
- **Stop or give way:** "stop, UAL22 crossing ahead on B from the left", or "give way to the A321 on your right".
- **Runway crossings and hold short:** "hold short of 13, departure rolling", or "cross now, next arrival is 6 nm out".
- **Departure:** "#2 for departure behind the E175", "line up after the landing A321", or "go: arrival is 7 nm out".

**Where it shows**
- A small always-on-top LAIA Sim window on the sim PC, which also works in VR.
- The live map, on a tablet or phone.
- Optionally spoken by a neutral "first officer" voice (not the controller), so the user doesn't have to look.
- Later, possibly an in-sim toolbar panel (an MSFS Community package).

**The AI works around the user too**
- On the ground, the AI gives way to the user as it does today.
- In the air, the user keeps their place in the sequence, which tracks the user's actual progress. If the user ignores the guidance, the AI flights around them adapt, within the stability rules. If the user falls well behind or ahead, they are re-slotted in the normal order, not given priority.

### Step 2d: Flow and runway changes (on the ball when the real world changes)
LAIA must notice quickly when the real airport changes flow, and switch over logically, as a real facility does. The user's decision, 9 Nov 2026.

**Noticing the change**
- **At major airports, real-world ATIS comes first:** a new runway configuration in the D-ATIS is the trigger.
- **Otherwise, live wind (METAR and the sim's weather) against the runway limits:** tailwind over 5 kt or crosswind over 25 kt.
- **Later, the learned airport profile** (which configuration this airport really uses in this wind), so changes can be anticipated rather than reacted to.
- **No flip-flopping:** a wind-based change needs the new wind to hold for about 10–15 minutes, unless the current runway is actually out of limits.

**Switching over logically (a planned changeover, not a jump)**
- **The cut-off:** LAIA picks the last arrival to the old runway and the first arrival to the new one.
- **Aircraft committed to the old runway keep it:** on final, or inside the sequence freeze where switching would cost more than finishing. Aircraft further out are moved to the new runway's STAR runway transition and approach at a sensible point, not mid-turn.
- **Departures:** aircraft at the gate or early in their taxi get the new runway and SID, with their taxi rerouted. Aircraft already at the hold short line for the old runway depart from it if it's still within limits.
- **The sequence is rebuilt once,** for the new configuration. The stability rules still apply.
- **Sectors and frequencies follow** if the change moves which corridors feed which runway.

**Telling the user**
- **Traffic mode (no ATC):** plain notes with lead time:
  - "LGA is changing to land 31 / depart 4"
  - "you're after the change: expect the ILS 31"
  - "you're before it: still runway 22"

  On the ground, before taxi: "expect runway 4 for departure now".
- **ATC mode:**
  - In the air: an amended clearance or a new expected approach, issued at a sensible moment (not on final, not in the middle of a busy exchange), for example "expect ILS runway 31", or a new STAR runway transition or a new arrival.
  - On the ground: a new runway and SID, an amended clearance if the route changes, and a new taxi route.
  - The AI flights get the same kinds of changes on frequency, so the frequency sounds like a real configuration change.

**Changes must be logical**
- One change per aircraft for a configuration change, never several.
- Nothing is changed that's already committed, and every change comes with what to expect next.

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
- **The workload model** (researched 9 Nov 2026: FAA Monitor Alert Parameters and MIT Lincoln Lab's sector workload model):
  - The FAA alerts when a sector's predicted peak aircraft count passes its Monitor Alert Parameter (MAP). The older FAA rule set the MAP from the average time aircraft spend in the sector: about 5 aircraft for a 3-minute transit, rising by about 1.7 aircraft per minute of transit, capped at 18. Since 2019 the FAA uses a workload-based baseline, adjusted per sector (for example, lowered in bad weather).
  - Lincoln Lab's model of controller workload per sector:
    - A constant background load of about 10%.
    - About 28 s of work for each aircraft entering or leaving.
    - About 62 s per potential conflict, which grows with the square of the number of aircraft and with how many are climbing or descending.
    - A sector is at its limit at about 80% workload.
  - LAIA uses this model, plus each aircraft's predicted radio time, to decide when to split.
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
  - **Radar-vector procedures, researched ahead of time.** Many SIDs are "fly runway heading, expect vectors" (LaGuardia's LGA departure, for example), and many STARs end in vectors with no joining point to an approach. For those, LAIA learns from real previous flights (ADS-B tracks) where they actually go:
    - **Departures:** the headings, turn points and climb at each altitude after takeoff, per runway and per departure fix or direction, until they join their route.
    - **Arrivals:** the downwind, base and intercept paths from the STAR's end to final, per runway configuration. This includes how far out they're turned in at busy and quiet times, and where traffic is extended or turned short.
    - **Stored per airport and runway configuration,** as typical paths plus their normal spread, so not every flight flies exactly the same line.
    - **Used by:** the AI traffic (vectors that look like the real airport's), the sequencer (its vector and extension options are the ones real controllers use there) and, in ATC mode, the controller's instructions to the user.
    - **Until an airport has been learned,** the current generic vectors (downwind, base, intercept about 8 nm out) are used.
  - Arrival corridors (used to check and adjust the STAR groups and sector limits).
  - The speeds actually flown.
  - Typical delays.
- **Researched ahead of time, tied to flight planning:**
  - When a SimBrief plan is loaded, LAIA researches the departure and arrival airports and their neighbours in the background, so the learned data is ready before the user gets there.
  - Airports already learned are refreshed now and then, for example when the procedures change or the profile is a few months old.
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
