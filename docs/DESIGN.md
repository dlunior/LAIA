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
- **STARs that end at or over the airport (0.1.56):** many end with "expect radar vectors" (LaGuardia's VADDR1 ends over the field). They're flown only until about 8 nm from the airport, then the arrival is vectored (downwind, base, final). When a STAR doesn't lead into the chosen approach, the arrival is vectored to final, not sent straight to the approach's first fix.
- **Fixes from the 0.1.56 test (0.1.61):**
  - New arrivals start at least 5 nm clear of every arrival already flying.
  - An arrival below its descent path levels off until the path comes down to it (this had caused an unstable approach after a go-around).
  - A taxiing aircraft given a new route keeps to it.
  - A regional operator is taken from the livery only when its title says "opb …".
- **M1, energy-correct flying (0.1.67):**
  - Deceleration limited by configuration minus descent gradient (clean ≈10, approach flaps ≈12.5, gear and landing flaps ≈25 kt/nm, minus 1 per 32 ft/nm, plus speedbrakes).
  - A planned descent with level slowdowns (250→210 over 4 nm at 20 nm, 210→180 over 2.4 nm at 12 nm, 180→170 over 0.8 nm at 7 nm, glidepath joined at about 2,000 ft, 6 nm out). A shallower descent when told to slow.
  - The maximum speed at the final approach fix: Vapp + 10 kt per nm beyond 3 nm.
  - Holding at 265 kt and 1.5-minute legs above 14,000 ft.
  - Spacing measured continuously against the predicted gap at the threshold (compression, plus runway occupancy of about 65 s, plus room for departures), corrected with speed in 5-kt steps.
  - When speed can't fix it, by real spacing needs only: a hold beyond 15 nm, a 360 at 12–15 nm, or an early break-off at 5–12 nm.
  - Eased taxi turns (curvature ramps in and out) with a radius of about 1.1 × span (30–60 m), and lead-in arcs into the stands.
- **M2, the live map (0.1.67):** `/map` on the control site. It shows you, drawn traffic and simulated-only traffic in three colours. Click an aircraft for its details and route ahead, with published speed limits. It also shows runways in use, the drawing range, all routes, a follow-me option and a flight list. It is phone-friendly.
- **Approaches joined the published way (0.1.71):** when the STAR doesn't lead into the approach, the arrival flies the approach's published transition that starts nearest the end of the STAR (within about 12 nm). At LGA's RNAV Y 31, the southwest STARs ending at PROUD go via FEDEM (over the field at 4,000 ft, then out northeast and east to GEMKE, clear of JFK), and VADDR1 and NOBBI1 go via WASBO. Otherwise the arrival is vectored to the approach's first fix, lined up with its inbound course, never a generic straight-in final. Approaches are trimmed only at the runway fix, so transitions that start beyond the threshold are kept.
- **Peak (test) traffic level (0.1.54):** for testing at quiet times. It finds the airport's busiest 3 hours in its real schedule and replays them now, real flights and all, then replays them again when they finish. The control page says it isn't the real time. Busy and Peak both fly every scheduled flight; Peak has a higher object limit.
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

**Constant monitoring, with speed as the main tool (the user's decision, 9 Nov 2026).** As at a real facility, every arrival's spacing is measured and predicted all the time, and corrected as it drifts. Speed control is the controller's primary tool.
- **Re-measured every update (every few seconds), for every pair of aircraft in the sequence:**
  - the current gap,
  - the predicted gap at the runway threshold, allowing for compression (the leader slows to its approach speed first),
  - the predicted runway occupancy of the aircraft ahead.
- **The gap needed at the threshold** = wake or radar spacing (CWT or 3 nm), or the leader's runway occupancy time plus a margin, whichever is larger, plus a small buffer.
- **The rules for speed control (7110.65 5-7-1, checked 9 Nov 2026):**
  - "Determine the interval required and the point at which the interval is to be accomplished."
  - "Keep speed adjustments to the minimum necessary to achieve or maintain required or desired spacing."
  - "Avoid adjustments requiring alternate decreases and increases."
  - "Terminate speed adjustments when no longer needed."
  - Speeds are given in 5-knot increments of indicated airspeed. Pilots hold them within ±10 kt.
- **Speed corrections are made early and as often as the monitoring needs,** in 5-kt increments (in practice mostly round 10s and 20s): "reduce speed to 190", "increase to 210", "maintain 180 until FIXXX", "170 to a 5-mile final". In the terminal area an arrival may typically get several speed instructions. A new one is given as soon as the predicted gap drifts beyond a small tolerance, with enough lead to work within the energy rules.
- **Speeds stay within the rules:** 7110.65 5-7 minimum speeds, nothing inside 5 miles or the final approach fix, the maximum speed at the final approach fix, and the energy model (no slowing in a dive).
- **Then vectors:** when speed can't fix it, a vector, a longer downwind or a later base, and a rejoin.
- **Then an early break-off** (around 6–10 miles, before final) if the threshold gap is still predicted to be too small: "turn left heading 270, climb and maintain 3,000, I'll bring you back around". That's calmer and more realistic than a go-around at half a mile.
- **Holding** only when the whole flow is over capacity.
- **Go-arounds are a last resort, and rare,** for things that can't be predicted (a slow runway exit, a gust).
- **Runway occupancy:** the leader picks the first suitable exit (high-speed exits when traffic is close behind), and the exit choice counts in the predicted occupancy time.
- **The landing rule stays 7110.65 3-10-3:** an airliner may not cross the threshold until the aircraft ahead has cleared the runway, or has departed and crossed the runway end. The reduced distances apply only to small aircraft. Landing clearance may be anticipated (3-10-6) when the separation will exist in time.

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
- **Instructions:** speeds are adjusted as often as the monitoring needs (see above), in 5-kt increments with lead time. They never zigzag between slower and faster, and are cancelled ("resume normal speed") when no longer needed. Route changes (vectors and their rejoin) are rarer, at most every 3–5 minutes per aircraft, except for safety. Each comes as one clear plan ("turn left heading 240, expect direct CAMRN in 10 miles").
- **The user gets no priority: a sequence is a sequence (the user's decision).** The user is sequenced like any other flight, in the normal order (by predicted arrival, with wake and runway rules), so others don't always yield to them.
- **The user's slot is the most stable,** because the user can't react as instantly as an AI. Once set, it is only moved for a real reason: the user being well off their prediction, or a go-around.
- **Delay for the user is speed control first, then a small vector or extended downwind. A hold for the user is extremely rare,** only when nothing else can make the spacing. The AI flights take the holds.

### Step 2a: Radar vectors, the make-or-break of realism (top priority)

The user's words (9 Nov 2026): radar vectors matter more for realism than the ground traffic. That means the downwind, the base and the vectors to final, and vectors that never conflict with a neighbouring airport's airspace (LGA arrivals must not tangle with EWR's).

**Learned real vector patterns are built together with the sequencer,** not left for the general learning step. They're taken from the radar-vector part of step 6, using real ADS-B tracks:
- Downwind, base and final-intercept paths, the altitudes on each, and where controllers extend or turn short, per runway configuration.
- Each pattern is kept with its normal spread, so the sequencer's choices (extending a downwind, turning base early or late) all fall within what really happens there.

**Published transitions are a guide for the vectors, not a script (the user's guidance, 10 Nov 2026).** An
approach's published transitions (LGA's RNAV Y 31 via FEDEM, WASBO and so on) show how traffic is meant to flow to
final: which side the downwind is on, roughly where it turns, which areas it stays clear of. Controllers vector
along that shape, shortening or extending it as the sequence needs, rather than clearing everyone to fly it exactly.
- **Now (0.1.71, interim):** with no learned data, arrivals fly the nearest transition as published.
- **From M5/M6:** the transitions (and, once learned, the real ADS-B tracks) set the shape and envelope of the vectors. The arrival manager then vectors along that shape, with downwind length, base turn and shortcuts chosen for the sequence. The published path is used as-is only when nothing better is known.

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

### Step 2e: Smoother ground turns (can be done any time; planned with M1)
- **Easing curves:** the turn rate builds and unwinds gradually at the start and end of each turn, like a pilot feeding in nosewheel steering, instead of jumping straight to full curvature.
- **Radius by turn:** set by the angle of the turn, the aircraft's size and the real taxiway fillet, not a fixed 40 m.
- **Slow before the corner:** slowing finished on the straight, then a steady speed through the turn.
- **Oversteer on big jets:** the main gear tracks the centreline and the nose swings wide outside it (judgmental oversteering).
- **A light heading smoothing** in LAIA Sim as the final polish.

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

**Which approach is in use (the user's note, 10 Nov 2026; researched).** At airports with close neighbours, the
approach is tied to the neighbours' flows, and the alternatives are never used interchangeably. LaGuardia runway 31:
- The **RNAV X 31** (from the southwest via VARAZ or GGREG, then PACHU, with turns in its visual segment) lets N90 run 31 as a straight-in while JFK is on an east flow at the same time. It also gives a repeatable, noise-friendly track (FAA procedure package).
- The **RNAV Y 31** (from the east via GEMKE) was created in 2019 so JFK wouldn't be forced into a single configuration. The Port Authority's 2018 briefing gave the order of preference for runway 31 then as: Expressway Visual, ILS 4 circle to 31, RNAV 31, LOC 31.

The choice, in order:
1. **The real ATIS states the approach in use (M11).** Use exactly that for every arrival.
2. **Linked configurations.** With the whole TRACON simulated (M3/M4), pick the approach consistent with the neighbours' runway configurations (for example, JFK on east flow → LGA 31 via the X).
3. **Learned (M13).** From real ADS-B tracks, identify which approach was flown (track shape) against the neighbours' configurations at the time, giving a per-airport table.
4. **One approach at a time,** never a random mix. It changes only with a configuration change, as a planned changeover.

Since 0.1.116 (MCO run on 0.1.115, landing 18R and departing 18L/18R: all 15 departures queued for 18L, up to 9 more than 18R): a stand counts as "outside the runways" (the ramp rule) only when it is beyond every parallel runway the airport has, not just those in use — MCO's terminal sits between the west and east pairs, so with only the west pair in use it is not a ramp and both runways are open to it (SID side and stand side decide, queue evens out); a queue more than 3 longer than the other runway's now counts double. Short flights from fields inside the 200 nm hand-over ring are taken over once they've climbed to 10,000 ft (or their cruise level), not at take-off (AAL2072 had appeared 167 nm out at 50 ft). 0.1.118 reverts the 0.1.116 departure change (user: 18L was right — the Peak test replays daytime peak traffic but the live ATIS was MCO's midnight configuration, landing 18R with runways closed, so every terminal departure belongs on 18L and a long queue there is a test artefact, not a logic fault). Lesson: when a run pairs peak traffic with a night ATIS, judge runway use against the night configuration; a stand beyond the runways in use keeps to the runway on its own side rather than crossing an active landing runway. Kept from 0.1.116's round: short flights from fields inside the 200 nm hand-over ring are taken over once they've climbed to 10,000 ft (or cruise), not at take-off (0.1.115). Since 0.1.98 (user rule: no per-airport fixes — the app must find airport rules itself): LAIA reads every US airport's FAA "Attention All Users Pages" automatically. Each 28-day d-TPP cycle (code YYNN, 29 OCT 2026 = 2611) the server downloads the FAA chart list (aeronav.faa.gov/d-tpp/{cycle}/xml_data/d-tpp_Metafile.xml, kept for the cycle), finds the airport's charts named AAUP / ATTENTION ALL USERS, downloads those PDFs (aeronav.faa.gov/d-tpp/{cycle}/{pdf}), reads their text (PdfPig) and keeps it per airport per cycle. The text is turned into rules with the airport's own SID and STAR names: a line naming its procedures and a runway ("DDANY, MZULO, JEEMY, FATHE - Expect to Depart Runway 35L") is a rule, widened by a heading just before it ("Departing Runways 35L/R 17L/R") to that side's runways in either flow. Rules only narrow the general choice among runways in use (SIDs for departures, STARs for arrivals) and are quoted in the log. MCO's RNAV Departure AAUP (AL-571) is the test case: DDANY/MZULO/JEEMY/FATHE → 35L/R 17L/R, RDSOX/OSPRY/FSHUN → 36L/R 18L/R. Non-US airports have no AAUPs (other sources later). Since 0.1.96 (third MCO run: every northbound departure stacked on 36R, up to 5 waiting): a departure whose SID goes more or less straight out uses the runway on its stand's side of the field (terminal or airside closest to that runway), so straight-out traffic splits across the departure runways instead of all queueing for the one not used for landing; the log gives the reasons in compass terms ("exits to the west", "its stand is on the east side of the field"). Since 0.1.94: an arriving aircraft is not given a gate next to (within ~130 m of) an aircraft pushing back, starting engines, about to push or still taxiing among the stands, when another suitable gate is free, and a pushback waits for an inbound heading to a gate next door — so the two never meet head-on in the lane (the offline check went from 0 m to 79 m closest approach on the ground). Version note: the exit-logic change below shipped in 0.1.94 (0.1.90–0.1.93 were builds that failed their checks). Since 0.1.90 (user's question: an exit-logic problem, not something to space around): landing rollouts brake at ~2 m/s² to a fast taxi (~40 kt) and keep it to the exit, turning off at about 45 kt onto a high-speed (angled) exit (designed for 50 kt wet per ICAO Annex 14, up to 60 kt dry) or 20 kt onto a 90° one (curve-limited), instead of crawling at 30 kt from far back — and the exit is chosen with those turn-off speeds, so MCO 36L arrivals take the angled exit Y (~1.6 km) instead of rolling to J (~2.3 km). Expected occupancy ~40–50 s, as in real operations; the learned per-runway occupancy stays as a safety net. Since 0.1.89 (second MCO run): the side of the field decides, not how far across it a runway is — runways on the same side tie and the departure-only one wins (MCO: west departures use the inner 36R, not the outer 36L arrival runway, unless 36R is much busier); radar-vector SIDs and STARs (no fix, e.g. CITR1/ORLA6) are no longer treated as a perfect match for every direction — only used when nothing published points the right way; an arrival's runway and STAR are planned once, so the spacing slot is checked on the runway it really lands on (a 0.0 nm "behind" hold came from planning the slot on another runway); each runway learns its real occupancy time from the aircraft that land on it (MCO 36L ~70 s) and spaces arrivals by the larger of that and the exit-geometry estimate; the log notes any landing that takes over 60 s to clear. Since 0.1.87 (user rules, all airports): vectors to final are downwind → base (at right angles to the final) → intercept heading about 25° (never more than 30°, 7110.65 5-9-2) joining 8–10 nm out (9.5 nm, at least 2 nm outside the approach gate); a straight-in from behind inside a 30° cone goes direct to the join point. A published transition that hooks onto the final with more than a 35° turn far out (MCO ILS 36L via EXBAN: a 180° hairpin onto a 13 nm final), or joins from the side beyond 11.5 nm, is replaced by those vectors; short side-joins that are the procedure itself (LGA RNAV Y 31 at MANLL) and curved finals (RNAV X 31) stay as published. Extending the downwind for sequence (instead of holding) is still to do with M5. Ground: a pushback goes at most ~100 m straight back (about 2.2 aircraft lengths), onto whatever line is behind the stand — a taxilane, or a taxiway where it runs close to the gates (some fields push straight onto a taxiway; the rule is the distance, never the line type) — and not a long way out across the ramp (MCO: 151–205 m pushes jammed three 737s); standoffs of three or four aircraft in a ring are detected like head-on pairs (newer ones look for another way, the oldest goes first). Satellite-field STARs published for MCO (the user names LEESE, JAFAR and SLOAO; ALYNA4 is not one — it serves weather avoidance, international and oceanic routings) — fixed properly by real filed routes (M4); a stopgap that prefers STARs not also published at a neighbour came with M3 step 3. Since 0.1.86 (from the first MCO run): arrival spacing uses each runway's real occupancy time, worked out from where the first usable exit is (braking ~2 m/s² from touchdown ~400 m in, then rolling to it): MCO 36L's first east exit is ~2.3 km down, so its arrivals need ~75 s, not the flat 65 s that caused three "runway not clear: landing roll" go-arounds. Landing clearance anticipates separation (7110.65 3-10-3): a landing aircraft that will have turned off before the follower crosses the threshold no longer forces a go-around. Departures line up from a connection on their own side of the runway (never crossing it to enter from the far side) and never one that needs a hairpin turn. The log flags any route that turns more than a full circle in its first 25 nm. Since 0.1.85 (user rule, for every airport, not just MCO): runway assignment is a calculated decision from the ATIS (which runways are in use), the procedure and the gate or ramp. Arrivals: the STAR (and entry transition) comes from the origin's direction, and the runway from the STAR entry's side of the airport, across the runways in use (MCO: PRICY/GRNCH from the west → western runway, SNFLD/MUNGI from the east → eastern); a longer queue only moves an arrival when the imbalance is large (the ~1 in 10 exception). Departures: the SID (and exit) towards the destination, and the runway on that SID's side; a stand beyond the outermost runway on one side (cargo, FBO ramps) uses the runway on its own side whatever the direction; a runway also used for landing is the second choice. It is all geometry (runway positions across the field, fix bearings), so it applies anywhere; airports with special local rules (LAX's inboard/outboard split) will need their own rules later. Since 0.1.83: every runway the ATIS names is used (Orlando "landing 35L, 36L; departing 35L, 36R, 36L"). Each arrival lands on the runway on the side it comes from (by the bearing to its origin across the parallels), evened out so one runway doesn't take them all; each departure uses the departure runway nearest its gate, preferring one not also used for landing, evened out by queue. Spacing, wake and departure gaps are per runway; an arrival runway only leaves gaps for departures when departures use it (or cross it). STARs and SIDs are no longer random: the STAR whose entry fix (enroute transition) points at the origin airport, and the SID whose exit fix points at the destination (random only among options within 15° of the best, or when the other airport is unknown). The sim's "no suffix" approach code "0" is dropped (it showed "ILS 36L 0"). Since 0.1.82 the ATIS is read with the rules the FlightSim Route Generator learned from real texts that broke its parser (TrafficCore/AtisParse.cs): spelled-out runways (Denver "RUNWAY 3 4 LEFT"), stop at the NOTAMs, runway words in all forms ("RWY8", "RWY, 28L"), purpose from the words before the runway in the same sentence (context crosses commas, resets at full stops), runways with no runway word (Boston "RNAV 4L, DEP 4R", Atlanta "VIS 8L, ILS 9R"), "EXPECT 1R, 36" (Charlotte), closed runways removed (a closed taxiway between runways is not a closed runway, Cleveland), out-of-service items skipped; arrival/departure/combined broadcasts combined (a combined one naming one direction serves both). atis.info answers 404 for airports with no D-ATIS: that is kept 12 hours; failures back off 10 minutes. Without an ATIS the wind fallback works like a real tower: the longest runway whenever it's within limits (crosswind 20 kt, tailwind 5 kt), the end more into the wind; a shorter one only when the main one is out of limits. Since 0.1.81 the real ATIS always decides the runways when it names them ("LDG RWY 22, DEPG RWY 13", or the runway of the approach in use), even against the sim wind; the wind (tailwind limit 5 kt, crosswind 25 kt) is only the fallback when there is no ATIS (non-US airports, feed down). Since 0.1.79: one approach at a time per landing runway. It's the approach the real D-ATIS names (read every 10 minutes from atis.info for US airports, with the old datis.clowd.io address as a fallback; out-of-service and visual approaches are skipped), otherwise a fixed standard choice: an ILS first, else the RNAV with the most published transitions (LGA 31 → RNAV Y). It changes only when the runway or the ATIS changes, and the control page and map show it. Approaches whose final is short (the RNAV X 31 turns onto a 1.6 nm final at DCTRK) are now flown as published; before, they were rejected by a 2 nm minimum-final check and replaced by a straight-in. Step 2 (linked configurations, from the neighbours' simulated flows) is still to do with M4.

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

### Step 4a: Ground control (the user's notes, 10 Nov 2026: "golden towbar" on taxiway A, then an unrealistic 180 by SWA)
Today the ground logic only sees a conflict when two aircraft meet head-on, and breaks the stand-off by turning one
around. Airliners can't do that on a taxiway, and a real ground controller never lets it happen. The plan:
- **Routes planned so head-ons don't happen:**
  - One-way flows by runway configuration (inbound on one taxiway, outbound on another, as busy airports do; learned from real ADS-B ground tracks in M13).
  - Each route reserves its taxiway segments in time, and new routes avoid segments that opposing traffic will use.
- **Ground instructions, as controllers give them:**
  - "Hold short of taxiway B" at an intersection until crossing traffic passes.
  - "Give way to the Delta A220 from your left", decided ahead of time.
  - "Follow the Southwest 737" where routes merge.
  - The aircraft that waits stops at the hold line before the junction, never face to face.
- **No 180s on taxiways.** Aircraft turn around only on a ramp, a holding pad or a wide apron. If a head-on still happens (the user, or traffic LAIA doesn't control), one waits and the other is rerouted from an earlier junction.
- **For the user:** ground hints in traffic mode; real ground instructions in ATC mode.
- **Built with planned departures (M10),** since both use predicted taxi times.

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
  - Which approach is used with which neighbouring-airport configuration (e.g. LGA RNAV X vs Y 31 against JFK's flow).
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

## 4a. Feature schedule (the build order, with how each part is tested)

Section 4 says *what* LAIA will do; this section says *in which order* it gets built. Each milestone is a set of
releases that can be tested on its own before the next one starts. Every milestone also gets offline checks in
`Laia.Tests`, so CI proves the rules before the user flies.

Status key: **Next**, **Planned**, **Done**.

| # | Milestone | Builds | How the user tests it | Done when |
|---|---|---|---|---|
| **M0** | **Check 0.1.51 at LGA** (**Done**, 9 Nov 2026: fine apart from the runway pair not being re-picked when the traffic level changed, fixed in 0.1.52) | Nothing new; read the logs | Fly or sit at LGA in Normal or Busy for 30–60 min, then say "check the logs" | The runway pair is sensible (e.g. land 22, depart 13); no "not stable" go-arounds; holds or 360s only when needed; nothing stuck; departure spacing looks real |
| **M1** | **Energy-correct flying** (**Done**, 0.1.67; the main gear tracking the line on big jets is still to do) | Smoother ground turns (step 2e), the predicted gap at the threshold with runway occupancy, early break-offs instead of late go-arounds, and the energy model (descending and slowing compete), planned descents with level-off and slowdown points, flap and gear schedules, the maximum speed at the final approach fix, holding at 265 kt above 14,000 ft with 1.5-minute legs | Watch arrivals into LGA: they level off to slow, slow mostly on level pieces and on the localizer before the glideslope, and never slow while diving. The log shows each arrival's descent plan | Offline checks: no arrival slows faster than the model allows; every arrival is stable at 1,000 ft; FAF speeds fit the formula |
| **M2** | **Live map, first version** (**Done**, 0.1.67, at `/map`) | Map page on the control site: the three colours (drawn, user, simulated), click an aircraft for details, SID/STAR/approach overlay, runways in use, the 40 nm drawing ring | Open the control page on a tablet or PC while flying | Every drawn aircraft shows in the right place; clicking shows its route and procedure. This map is then the test tool for everything after it. |
| **M3** | **The whole sky, the network layer** (**Done**, 0.1.103: network layer 0.1.77, hand-over 0.1.101, neighbouring fields 0.1.102, drawing rules 0.1.103) | All AIG schedules read at once; every scheduled flight moving logically (gate, climb, cruise, descent, landed); the three layers with smooth hand-over between them; the drawing rules (gates and ground at the user's fields only, runway-only at neighbours, airborne within 40 nm and ±10,000 ft, object cap with priorities) | Map: thousands of simulated flights across the region. In the sim: neighbouring-field traffic appears on their runways only, and TCAS and the ND show traffic out to 40 nm | Sim PC frame rate unchanged; no pop-in inside view; the object cap is respected |

**M3 step 1 (network layer), how it works.** LAIA Sim reads every AIG schedule (installed models only) in one pass and sends it to the server as one gzipped, tab-separated file (`/api/network`), when the server asks (none yet or a day old). The server keeps it (`network.tsv.gz`) and places every airport from OurAirports' public airport list (downloaded monthly; the FAA runway file fills US gaps). Each flight is flown along the great circle by its schedule: about 12 min taxi out and 6 min taxi in (never more than 30% of a short block), a climb at airliner rates with speed building from 40% to 100%, the cruise, then a descent. The cruise level comes from distance and type (turboprops at most FL250) on the right semicircle (odd thousands eastbound, even westbound). Flights touching the airports run in detail are left to those worlds until the hand-over (step 2). The map shows flights within 400 nm as small grey dots (canvas, refreshed every 4 s); click one for its route, phase, levels and time to landing. **Realism principle (user, 10 Nov 2026): not robotic.** Aim to make unusual things rare, not impossible. Long landings and rollouts to a far exit happen in real life, mostly with heavier aircraft, so the traffic should vary from aircraft to aircraft (touchdown point, braking, exit taken, speeds), with the uncommon outcomes occurring at about real-world rates rather than never. To do: per-aircraft variation in touchdown point and braking (heavies longer), and the occasional far exit.

**Procedure restrictions, 0.1.113.** The navigation data LAIA reads is the user's Navigraph data (Community: navigraph-nav-base / navigraph-nav-fixedwing, AIRAC 2611, loaded by MSFS in place of the default; LAIA Sim reads it through the sim). Its legs carry altitude restrictions (at, at or above, at or below, between) and speed limits; the speed limits were already flown, the altitude ones were read but not used — the user saw arrivals and departures ignoring them. Now arrivals bend their planned descent to them: never above an "at or below"/"at" restriction ahead (starting down in time at ~300 ft per nm), never below an "at or above"/"at" one until its fix (level, not descending, while held up); departures level off at a SID's "at or below" restriction until the fix is passed. Chart notes (turboprop only, which airports a STAR is for) aren't in that data format; they're on the charts (FAA d-TPP, free; Navigraph Charts need its developer programme).

**M4 step 2 (filed routes and metering), 0.1.110.** (1) The FAA Preferred Routes database (www.fly.faa.gov/rmt/data_file/prefroutes_db.csv, free, ~1.4 MB, refreshed weekly) gives the route airlines file between US airport pairs: its route string ends with the STAR and the fix it's joined at, and starts with the SID and its exit, and many rows say which flow they're for ("MCO LANDING NORTH/SOUTH") or that they're the oceanic/weather option ("WATER", "DEEP OCEAN" — MCO's ALYNA4, used only when no row fits the flow). LAIA gives each arrival the STAR of the row that fits the flow in use (jets the high-altitude rows) and each departure the SID, when that procedure is published at the airport; otherwise the direction-based choice stays. Pairs not in the database (Seattle, San Juan, Punta Cana to MCO — seen getting JAFAR3 and SLOAO1, which the user identifies as satellite arrivals, on the 0.1.110 MCO peak test) get, since 0.1.111, the STAR most used by the database's routes from listed airports within 25° of the same direction for the flow in use (closer directions weigh more), and the same for SIDs towards a destination; only with nothing in that direction does the old direction logic decide. Since 0.1.112 the database also says which airport each STAR serves (the destination its routes end at: JAFAR3 → ORL, BITHO7 → MLB) and which are for propeller aircraft only (rows marked TURBOPROPS with no jet or unrestricted row): in the direction-based choice a STAR the database routes only to other airports counts 90° worse, one it doesn't name 30° worse, and a jet never gets a propeller-only STAR when anything else serves the runway. The user notes SLOAO is turboprop-only (a chart restriction MSFS data doesn't carry; it isn't in the preferred routes, so it's simply ranked below the STARs the database routes to MCO). Chosen because AirLabs and the Flightradar24 API have no filed routes, adsb.lol has none (tracks only), FlightAware AeroAPI has them per call ($0.005, $5/month free credit) and FAA SWIM has every filed plan live but needs registration — kept as later options. (2) Arrival manager (start of M5): every 5 s each runway's arrivals are ordered by their unhindered landing time; each gets a slot — the later of its own time and the slot ahead plus the gap needed — and an arrival that must lose more than 30 s flies slower far out (240 kt above 11,500 ft, 210 kt below, until 20 nm) until it's within 5 s of its slot. A hold is only given when the shortfall is more than speed control can still absorb before the runway (the first MCO run on 0.1.103 had holds 45 nm out because ~20 arrivals were taken over at once and only spaced inside 45 nm). (3) Runway exits: the exit is chosen with the braking actually used in the rollout (2.4 m/s²) and ~49 kt onto high-speed exits — MCO 36L arrivals were still taking 76 s (rolling past exit Y to J). From the 0.1.103 log: the AAUP download works (KMCO "RNAV DP AAUP" + "CONT.1", both rules read and applied); no neighbouring fields were chosen at MCO because SFB, ISM and ORL have under 14 airline flights a week in the user's AIG schedules (mostly business jets, which AIG files exclude).

**M4 step 1 (prediction), 0.1.104.** Every arrival's landing time is predicted every 5 seconds from its whole remaining path (its planned speeds and levels, TAS at altitude, the wind on final), from the moment it's taken over ~200 nm out. Each runway's landing order comes from those predictions. The map shows "lands in N min (time) · number K for runway" on every inbound and "#K in Nm" in the list. The predictions made 30, 10 and 5 minutes out are kept, and at touchdown the log says how far off each was ("landing time as predicted 30 min out +45 s, 10 min out +8 s, 5 min out -2 s"); the control page shows the running average miss. Targets (from the schedule): about 1–2 min from 30 min out, about 30 s from 10 min out. Still to do in M4: departures and neighbours in the prediction, ETAs at the STAR's fixes, and using the predictions to start sequencing earlier (M5).

**M3 step 2 (hand-over), done in 0.1.101.** A scheduled arrival into the traffic airport is taken over from the whole sky when the network has it within 200 nm (checked from 75 minutes before its gate time): it starts exactly where the network has it, at its level, flies direct to the first fix of its STAR that isn't further out than it already is, and descends from cruise along a planned path — about 290 ft per nm above 10,000 ft (top of descent a little early, as crews plan), a 4 nm level piece at 10,000 ft to slow from ~280 kt to 250 kt, never below 10,000 ft faster than 250 kt (14 CFR 91.117(a)), then the existing energy-correct profile. Far-out arrivals (beyond 60 nm) don't count against the traffic level and aren't drawn; sequencing (speed, holds) only acts within 45 nm until the arrival manager (M5). If the origin can't be placed, the old 7–40 nm start is used. The map shows each flight once: network flights are hidden while the detailed traffic is flying that airframe (by registration). Ground: a pushback has the right of way over taxiing traffic. **M3 step 3 (neighbouring fields), done in 0.1.102.** The busiest scheduled airports within 40 nm of the traffic airport (at least 14 flights a week in the AIG schedules, up to four) are flown runway-only on the main airport's schedule clock (so the peak test lines up): departures appear lined up about 12–16 minutes after their gate time, on the runway for their SID's side, take off and climb away into the whole sky; arrivals are taken over from the whole sky within 60 nm and disappear once slowed on the runway. No gates or taxiing there; at most 10 at once per field. Their layouts and schedules are asked from LAIA Sim like the main airport's; ATIS and AAUPs apply to them too. Their STAR names are used by the main airport: a STAR also published at a neighbour counts as 60° worse, so it's only flown when nothing of the airport's own fits. This is a stopgap based only on what the data publishes, not on knowing a STAR's purpose: the user notes MCO's ALYNA4 is not a satellite arrival but is used for weather avoidance, international and oceanic routings, while LEESE, JAFAR and SLOAO are satellite arrivals. The real fix is real-world filed routes (planned with M4): an arrival flies the STAR its flight plan files. **M3 step 4 (drawing rules), done in 0.1.103.** Drawn in the sim: airborne traffic within 40 nm and ±10,000 ft of the user; ground traffic at airports within 40 nm (the user's field with gates and taxiing, neighbours on their runways); at most 50 at once, nearest first (distance plus 1 nm per 2,000 ft of height difference). The map's "drawn" colour uses the same rule. M3 is complete.
| **M4** | **The airspace layer and prediction** (**In progress**: landing-time prediction and its measurement in 0.1.104) | Full flight paths from about 200 nm (cruise, top of descent, STAR, approach), departures climbing out, every airport in the TRACON active (JFK, EWR, satellites around LGA), ETAs at fixes and runways | Map: ETAs on every inbound. The log compares each landing's predicted and actual times | Predictions within about 1–2 min from 30 min out, and within about 30 s from 10 min out |
| **M5** | **Arrival manager** | Landing order and times per runway with the sequence states (unstable, stable, super-stable, frozen). **Constant monitoring** of every pair of arrivals every few seconds: the current gap, and the predicted gap at the threshold with compression and runway occupancy. **Speed control as the primary tool**, corrected as soon as a gap drifts, following 7110.65 5-7-1 (5-kt increments, minimum necessary, never alternating slower and faster, cancelled when no longer needed). Delay absorbed by speed, then vectors (generic, kept out of neighbours' finals), then holds; the user sequenced like any other flight (no priority, holds for the user extremely rare); a sequence panel on the map | A busy evening at LGA: watch the sequence panel and the map | Spacing is set up before the final approach (few 360s); holds only in real surges; the order rarely changes after "stable"; gaps are corrected continuously with speed (no zigzag of slower and faster), and route changes come at most every 3–5 min per aircraft |
| **M6** | **Learned radar vectors** | An adsb.lol history pipeline for chosen airports (researched ahead of time from the SimBrief plan): real downwind, base and intercept patterns per runway configuration; each airport's airspace envelope; a check for conflicts between airports; radar-vector SIDs and STARs | Map layer comparing real tracks with LAIA's vectors at LGA, EWR and JFK | LAIA's vectors fall inside the real spread; no predicted LGA/EWR/JFK conflicts left unsolved |
| **M7** | **Every aircraft in the sky** | LAIA Sim reads all other aircraft within about 100 nm (other traffic programs, live traffic, multiplayer); their paths are predicted; LAIA's flights are sequenced around them; the user's slot comes from the SimBrief plan | Run with MSFS live traffic or another traffic program on, and watch the fourth colour on the map | LAIA's flights space around traffic they don't control; no conflicts with it |
| **M8** | **Guidance in traffic mode** | Always-on-top LAIA Sim window (works in VR), optional first-officer voice: the user's place, who to follow, simple speeds (no time targets), rare extensions; ground hints (wait, give way, hold short, line up after …) | Fly an LGA arrival and departure following only the hints | Hints are few, flyable and always make sense; the user never meets traffic on the ground |
| **M9** | **Real-world traffic at the user's fields** | Today's real flights from AirLabs (delays, gates) plus live positions from OpenSky (credentials box in LAIA Sim settings), matched to AIG liveries, with no duplicates; falls back to AIG | Compare LAIA's LGA traffic with a live flight tracker at the same time | The same flights at about the same times, positions and gates; API call counts shown |
| **M10** | **Planned departures and ground control** (step 4a: one-way flows, segment reservation, hold short and give way, no 180s on taxiways) | Pushback and taxi times planned so each departure reaches the runway as its gap opens (including crossing-runway departures) | Busy LGA: watch the hold-short line | Short holding-short waits; departures fill the arrival gaps |
| **M11** | **Flow and runway changes** | A real-world ATIS (D-ATIS) feed at major airports, a wind check that requires the change to persist, a planned changeover (last and first aircraft), and notes to the user | Change the wind in the sim (or wait for a real ATIS change) | One clean changeover; committed aircraft keep their runway; the user gets a note with lead time |
| **M12** | **Sectors, workload and frequencies** | Sector map per TRACON (feeders by STAR group, final, departures, satellites, altitude layers), fixed split plans, the workload model, splits opened ahead of time and kept stable, handoffs, traffic management (miles-in-trail, gate holds); map layer | The LGA evening push: watch splits happen on the map before the sectors fill | Splits happen 15–20 min before overload, never flip-flop, and always split along neighbouring STAR groups |
| **M13** | **Learned airport profiles (GPU)** | Overnight training on the 2080 Ti: runway configurations by wind, speeds actually flown, delays, corridors, sector limits; shared learned data | Compare a learned airport against how it behaved before it was learned | Visible improvement in realism at learned airports |
| **M14** | **The radio** | LLM ATC on the GPU (user readbacks, 7110.65 phraseology, amended clearances, new expected approaches), AI chatter on each open frequency in different voices, one speaker at a time, real-world ATIS | Fly a full flight in ATC mode | Instructions match the sequence; the frequency load matches the sector workload; changes are logical |

**Ordering notes**
- **The map (M2) comes early** because it's how every later milestone is checked.
- **Generic radar vectors in M5 already keep out of neighbouring airports' finals.** M6 replaces them with learned vectors. Real-data vectors (the user's top priority) come straight after the sequencer, because the sequencer is what chooses between vector options.
- **The real-world traffic at the user's fields (M9)** can move earlier if the user wants, because it doesn't depend on M5–M8.
- **After each milestone:** update section 3 ("What is built") and this table's status, then agree the next milestone with the user.

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
