"""Step-1 radio logic: work out who is calling and what they want, then answer in FAA phraseology.

This is deliberately a small rules engine. Later build steps replace the canned replies with
real ATC decisions (clearances, taxi routes, vectors) driven by the traffic engine.
"""
import re

AIRLINES = {
    "delta": "Delta", "united": "United", "american": "American", "southwest": "Southwest",
    "jetblue": "JetBlue", "jet blue": "JetBlue", "air canada": "Air Canada", "jazz": "Jazz",
    "westjet": "WestJet", "west jet": "WestJet", "speedbird": "Speedbird", "alaska": "Alaska",
    "spirit": "Spirit Wings", "spirit wings": "Spirit Wings", "frontier flight": "Frontier Flight",
    "frontier": "Frontier Flight", "fedex": "FedEx", "fed ex": "FedEx", "cactus": "Cactus",
    "brickyard": "Brickyard", "endeavor": "Endeavor", "sky west": "SkyWest", "skywest": "SkyWest",
    "envoy": "Envoy", "air shuttle": "Air Shuttle", "piedmont": "Piedmont", "ups": "UPS",
    "hawaiian": "Hawaiian", "sun country": "Sun Country", "allegiant": "Allegiant", "porter": "Porter",
    "flair": "Flair", "transat": "Transat", "air transat": "Transat", "lufthansa": "Lufthansa",
    "air france": "Air France", "shamrock": "Shamrock", "virgin": "Virgin", "klm": "KLM",
    "caribbean": "Caribbean", "bahamas": "Bahamas", "copa": "Copa", "aeromexico": "Aeromexico",
    "volaris": "Volaris", "gopher": "Gopher", "lifeguard": "Lifeguard", "november": "November",
    "cessna": "Cessna", "skyhawk": "Skyhawk", "citation": "Citation", "piper": "Piper",
}

UNITS = {"zero": 0, "oh": 0, "o": 0, "one": 1, "won": 1, "two": 2, "to": 2, "too": 2, "three": 3, "tree": 3,
         "four": 4, "for": 4, "fower": 4, "five": 5, "fife": 5, "six": 6, "seven": 7, "eight": 8,
         "nine": 9, "niner": 9}
TEENS = {"ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15,
         "sixteen": 16, "seventeen": 17, "eighteen": 18, "nineteen": 19}
TENS = {"twenty": 2, "thirty": 3, "forty": 4, "fourty": 4, "fifty": 5, "sixty": 6, "seventy": 7,
        "eighty": 8, "ninety": 9}
PHONETIC = {"alpha": "A", "alfa": "A", "bravo": "B", "charlie": "C", "delta": "D", "echo": "E",
            "foxtrot": "F", "golf": "G", "hotel": "H", "india": "I", "juliet": "J", "juliett": "J",
            "kilo": "K", "lima": "L", "mike": "M", "november": "N", "oscar": "O", "papa": "P",
            "quebec": "Q", "romeo": "R", "sierra": "S", "tango": "T", "uniform": "U", "victor": "V",
            "whiskey": "W", "xray": "X", "x-ray": "X", "yankee": "Y", "zulu": "Z", "heavy": "", "super": ""}
FACILITY_WORDS = {"clearance": "Clearance", "delivery": "Clearance", "ground": "Ground", "ramp": "Ramp",
                  "tower": "Tower", "approach": "Approach", "departure": "Departure", "center": "Center",
                  "centre": "Centre", "radio": "Radio", "unicom": "Traffic", "traffic": "Traffic"}
STOP = {"hello", "hi", "good", "morning", "afternoon", "evening", "day", "and", "with", "you", "the", "this",
        "is", "uh", "um", "request", "requesting", "on", "at", "to", "of", "a"}
SPOKEN_DIGIT = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"]
NATURAL = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven",
           "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"]
TENS_WORDS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]


def _tokens(text):
    text = text.lower().replace("-", " ")
    text = re.sub(r"[^a-z0-9 ]", " ", text)
    return [t for t in text.split() if t]


def _read_number(tokens, i):
    """Read a spoken flight number starting at tokens[i]. Returns (digits, next_index)."""
    digits = ""
    while i < len(tokens):
        t = tokens[i]
        if t.isdigit():
            digits += t
            i += 1
        elif t in UNITS:
            digits += str(UNITS[t])
            i += 1
        elif t in TEENS:
            digits += str(TEENS[t])
            i += 1
        elif t in TENS:
            nxt = tokens[i + 1] if i + 1 < len(tokens) else ""
            if nxt in UNITS and UNITS[nxt] > 0:
                digits += f"{TENS[t]}{UNITS[nxt]}"
                i += 2
            else:
                digits += f"{TENS[t]}0"
                i += 1
        elif t == "hundred" and digits:
            digits += "00"
            i += 1
        else:
            break
    return digits, i


def find_callsign(text):
    """Return (spoken_callsign, airline, number) or (None, None, None)."""
    toks = _tokens(text)
    # Whisper often writes "DAL123" or "Delta123".
    joined = []
    for t in toks:
        m = re.fullmatch(r"([a-z]+)(\d+)", t)
        joined.extend([m.group(1), m.group(2)] if m else [t])
    toks = joined
    for i in range(len(toks)):
        for words in (3, 2, 1):
            key = " ".join(toks[i:i + words])
            if key not in AIRLINES:
                continue
            name = AIRLINES[key]
            j = i + words
            if name == "November":  # US civil registration: November one two three alpha bravo
                reg = ""
                while j < len(toks) and (toks[j] in UNITS or toks[j].isdigit() or toks[j] in PHONETIC):
                    t = toks[j]
                    reg += str(UNITS[t]) if t in UNITS else (t.upper() if t.isdigit() else PHONETIC[t])
                    j += 1
                if reg:
                    return "November " + " ".join(_say_char(c) for c in reg), name, reg
                continue
            digits, j2 = _read_number(toks, j)
            suffix = ""
            while j2 < len(toks) and toks[j2] in PHONETIC and toks[j2] not in ("heavy", "super") and len(suffix) < 2:
                suffix += PHONETIC[toks[j2]]
                j2 += 1
            heavy = " heavy" if j2 < len(toks) and toks[j2] == "heavy" else ""
            if digits:
                spoken = f"{name} {say_flight_number(digits)}"
                if suffix:
                    spoken += " " + " ".join(_say_char(c) for c in suffix)
                return spoken + heavy, name, digits + suffix
    return None, None, None


def _say_char(c):
    if c.isdigit():
        return SPOKEN_DIGIT[int(c)]
    for word, letter in PHONETIC.items():
        if letter == c and word not in ("alfa", "juliett", "x-ray"):
            return word.capitalize()
    return c


def _pair(n):
    if n < 20:
        return NATURAL[n]
    tens, unit = divmod(n, 10)
    return TENS_WORDS[tens] + ("" if unit == 0 else "-" + NATURAL[unit])


def say_flight_number(digits):
    """FAA 7110.65 2-4-20: air carrier flight numbers in group form (e.g. 1234 -> twelve thirty-four)."""
    digits = digits.lstrip("0") or "0"
    n = int(digits)
    if len(digits) <= 2:
        return _pair(n)
    if len(digits) == 3:
        head, tail = digits[0], int(digits[1:])
        if tail == 0:
            return f"{NATURAL[int(head)]} hundred"
        if tail < 10:
            return f"{NATURAL[int(head)]} zero {NATURAL[tail]}"
        return f"{NATURAL[int(head)]} {_pair(tail)}"
    if len(digits) == 4:
        head, tail = int(digits[:2]), int(digits[2:])
        if tail == 0:
            return f"{_pair(head)} hundred"
        if tail < 10:
            return f"{_pair(head)} zero {NATURAL[tail]}"
        return f"{_pair(head)} {_pair(tail)}"
    return " ".join(SPOKEN_DIGIT[int(c)] for c in digits)


def find_facility(text):
    """Return the facility the pilot called, e.g. 'LaGuardia Ground', or None."""
    toks = _tokens(text)
    raw = re.sub(r"[^A-Za-z0-9 ]", " ", text).split()
    airline_words = {w for k in AIRLINES for w in k.split()}
    for i, t in enumerate(toks):
        if t not in FACILITY_WORDS:
            continue
        name = []
        k = i - 1
        while k >= 0 and len(name) < 2:
            w = toks[k]
            if w in STOP or w in UNITS or w in TEENS or w in TENS or w.isdigit() or w in airline_words or w in FACILITY_WORDS:
                break
            name.insert(0, raw[k] if k < len(raw) else w)
            k -= 1
        if name:
            pretty = " ".join(n if any(ch.isupper() for ch in n[1:]) else n.capitalize() for n in name)
            return f"{pretty} {FACILITY_WORDS[t]}", FACILITY_WORDS[t]
        return FACILITY_WORDS[t], FACILITY_WORDS[t]
    return None, None


# Remembers which facility each callsign last talked to, so a call without a facility name
# ("Delta 1196, with you") is answered by the same controller. Reset when the engine restarts.
_LAST_FACILITY = {}
_LAST_ANY = {"facility": None}
_CLEARED = {}  # callsign key -> squawk given in the clearance (for readback checks)

# ICAO airline code -> radio telephony, for turning a SimBrief callsign (DAL1196) into speech.
TELEPHONY = {
    "DAL": "Delta", "UAL": "United", "AAL": "American", "SWA": "Southwest", "JBU": "JetBlue", "ACA": "Air Canada",
    "ROU": "Rouge", "JZA": "Jazz", "WJA": "WestJet", "ASA": "Alaska", "NKS": "Spirit Wings", "FFT": "Frontier Flight",
    "SKW": "SkyWest", "RPA": "Brickyard", "EDV": "Endeavor", "ENY": "Envoy", "PDT": "Piedmont", "JIA": "Blue Streak",
    "FDX": "FedEx", "UPS": "UPS", "AAY": "Allegiant", "SCX": "Sun Country", "POE": "Porter", "FLE": "Flair",
    "TSC": "Transat", "HAL": "Hawaiian", "BAW": "Speedbird", "DLH": "Lufthansa", "AFR": "Air France", "KLM": "KLM",
    "VIR": "Virgin", "CMP": "Copa", "AMX": "Aeromexico", "VOI": "Volaris", "BHS": "Bahamas", "BWA": "Caribbean",
    "GJS": "Lindbergh", "QXE": "Horizon", "CPZ": "Compass", "MXY": "Moxy", "VXP": "Avelo", "AWI": "Air Wisconsin",
}


def plan_callsign(callsign):
    """'DAL1196' -> ('Delta eleven ninety-six', 'Delta', '1196') or (None, None, None)."""
    m = re.match(r"^([A-Z]{3})(\d+[A-Z]{0,2})$", (callsign or "").upper())
    if not m or m.group(1) not in TELEPHONY:
        return None, None, None
    name = TELEPHONY[m.group(1)]
    num = re.match(r"\d+", m.group(2)).group(0)
    return f"{name} {say_flight_number(num)}", name, m.group(2)


KIND_WORDS = {"Clearance": "clearance", "Ground": "ground", "Tower": "tower", "Approach": "approach",
              "Departure": "departure", "Center": "center", "Ramp": "ramp"}


def say_frequency(freq):
    """121.7 -> 'one two one point seven'; 118.725 -> 'one one eight point seven two five'."""
    s = f"{float(freq):.3f}".rstrip("0")
    if s.endswith("."):
        s += "0"
    head, tail = s.split(".")
    words = [SPOKEN_DIGIT[int(c)] for c in head] + ["point"] + [SPOKEN_DIGIT[int(c)] for c in tail]
    return " ".join(words)


def say_digits(text):
    return " ".join(SPOKEN_DIGIT[int(c)] if c.isdigit() else c for c in str(text))


def say_altitude(feet):
    feet = int(round(float(feet) / 100.0)) * 100
    if feet >= 18000:
        return "flight level " + say_digits(f"{feet // 100:03d}")
    thousands, hundreds = divmod(feet, 1000)
    words = []
    if thousands:
        words.append(f"{say_digits(thousands) if thousands >= 10 else NATURAL[thousands]} thousand")
    if hundreds:
        words.append(f"{NATURAL[hundreds // 100]} hundred")
    return " ".join(words)


def say_procedure(ident):
    """'LGA7' -> 'LGA seven' (letters kept, trailing number spoken)."""
    m = re.match(r"^([A-Z]+)(\d)?$", (ident or "").upper())
    if not m:
        return ident
    return m.group(1) + (f" {NATURAL[int(m.group(2))]}" if m.group(2) else "")


def _clearance(head, cs_key, ctx):
    plan = (ctx or {}).get("plan") or {}
    dest = plan.get("dest_city") or plan.get("dest_name") or plan.get("dest")
    if not dest:
        return f"{head}, roger. I don't have a flight plan for you, say destination."
    parts = [f"{head}, cleared to the {dest} airport"]
    if plan.get("sid"):
        parts[0] += f" via the {say_procedure(plan['sid'])} departure, then as filed"
    else:
        parts[0] += " as filed"
    climb = "climb via SID" if plan.get("sid") else f"maintain {say_altitude(plan.get('initial_climb') or 5000)}"
    parts.append(climb)
    if plan.get("cruise"):
        parts.append(f"expect {say_altitude(plan['cruise'])} one zero minutes after departure")
    if plan.get("departure_freq"):
        parts.append(f"departure frequency {say_frequency(plan['departure_freq'])}")
    if plan.get("squawk"):
        parts.append(f"squawk {say_digits(plan['squawk'])}")
        if cs_key:
            _CLEARED[cs_key] = str(plan["squawk"])
    return ". ".join(p[0].upper() + p[1:] if i else p for i, p in enumerate(parts)) + "."


def build_reply(heard, ctx=None):
    """Return the controller's reply text for what the pilot said.

    ctx (optional, from the server) describes the radio the pilot is on:
      tuned: {"name": "LaGuardia Ground", "kind": "Ground", "freq": "121.7"}
      airport_freqs: {"Clearance": {"name": ..., "freq": ...}, "Tower": {...}, ...}
      plan: SimBrief summary for the clearance (dest_city, sid, cruise, departure_freq, squawk)
    """
    ctx = ctx or {}
    text = (heard or "").strip()
    low = text.lower()
    if len(_tokens(text)) < 2:
        return "Station calling, say again."

    callsign, _, cs_key = find_callsign(text)
    named, named_kind = find_facility(text)
    tuned = ctx.get("tuned")

    # The user is the only aircraft on frequency for now: if the airline name matches their SimBrief
    # callsign (or the call contains the squawk we just gave them) but the number was misheard, it's them.
    plan_spoken, plan_airline, plan_key = plan_callsign(((ctx.get("plan") or {}).get("callsign")))
    if plan_spoken and cs_key != plan_key:
        digits_said = re.sub(r"\D", "", " ".join(str(UNITS.get(t, t)) for t in _tokens(text)))
        squawk = _CLEARED.get(plan_key)
        if (not callsign and (plan_airline.lower() in low or (squawk and squawk in digits_said))) or \
           (callsign and callsign.split(" ")[0].lower() == plan_airline.split(" ")[0].lower() and len(text) > 0 and cs_key and len(cs_key) < 2):
            callsign, cs_key = plan_spoken, plan_key

    if tuned:
        facility, kind = tuned["name"], tuned["kind"]
    else:
        facility, kind = named, named_kind
        if facility:
            _LAST_ANY["facility"] = facility
            if cs_key:
                _LAST_FACILITY[cs_key] = facility
        else:
            facility = _LAST_FACILITY.get(cs_key) if cs_key else _LAST_ANY["facility"]

    if not callsign:
        return f"Station calling {facility}, say again your call sign." if facility else "Station calling, say again your call sign."

    head = f"{callsign}, {facility}" if facility else callsign

    def has(*phrases):
        return any(p in low for p in phrases)

    wants_clearance = has("clearance", "ifr to", "i f r to", "ready to copy", "looking for ifr")
    freqs = ctx.get("airport_freqs") or {}

    def redirect(to_kind):
        target = freqs.get(to_kind)
        if not target:
            return None
        return f"{head}, contact {target['name']} on {say_frequency(target['freq'])}."

    # Pilot asked for something this position doesn't do: send them to the right frequency.
    if tuned:
        if wants_clearance and kind != "Clearance" and kind in ("Ground", "Tower", "Ramp"):
            r = redirect("Clearance")
            if r:
                return r
        if named_kind and named_kind != kind and named_kind in freqs and named_kind not in ("Approach", "Departure"):
            r = redirect(named_kind)
            if r:
                return r

    # Readback of a clearance
    if cs_key and cs_key in _CLEARED and _CLEARED[cs_key] in re.sub(r"\D", "", " ".join(
            str(UNITS.get(t, t)) for t in _tokens(text))):
        del _CLEARED[cs_key]
        nxt = freqs.get("Ground")
        tail = f" Contact {nxt['name']} on {say_frequency(nxt['freq'])} when ready to push." if nxt else ""
        return f"{callsign}, readback correct.{tail}"

    if has("radio check", "how do you read", "how do you hear"):
        return f"{head}, read you five by five."
    if has("say again", "repeat"):
        return f"{head}, I say again, go ahead."
    if wants_clearance:
        if ctx.get("on_ground") is False:
            return f"{head}, roger, say request."
        if ctx.get("plan"):
            return _clearance(head, cs_key, ctx)
        return f"{head}, roger. Your clearance is on request, I'll call you back shortly."
    if has("push", "pushback", "push back"):
        return f"{head}, push back approved at your discretion, advise ready to taxi."
    if has("ready to taxi", "request taxi", "taxi"):
        return f"{head}, roger, hold position, I'll have your taxi instructions shortly."
    if has("ready for departure", "ready to go", "holding short", "ready at"):
        return f"{head}, roger, hold short, number one for departure."
    if has("inbound", "with information", "have information", "landing", "full stop"):
        return f"{head}, roger, report established, expect the visual approach."
    if has("thank", "good day", "good night", "so long"):
        return f"{callsign}, good day."
    return f"{head}, roger."


if __name__ == "__main__":
    tests = [
        "LaGuardia Ground, Delta twelve thirty-four at gate C5, ready to taxi with information Bravo",
        "New York Approach, United 1532 with you passing one two thousand",
        "Clearance, Air Canada one eight five, request IFR to Toronto",
        "Cranbrook Radio, November one two three alpha bravo, radio check",
        "Atlanta Tower Delta 405 holding short runway two six left ready for departure",
        "hello hello",
        "Kennedy Ground JetBlue 7 push back gate 5",
        "Center, Southwest twenty-one hundred level three five zero",
        "Delta 1196",
        "Delta 1196 checking in",
        "LaGuardia Ground, Delta 1196, looking for IFR to Orlando",
        "Delta eleven ninety six, with you",
    ]
    for t in tests:
        print(f"{t}\n  -> {build_reply(t)}")
    ctx = {"tuned": {"name": "LaGuardia Clearance", "kind": "Clearance", "freq": "135.2"},
           "airport_freqs": {"Ground": {"name": "LaGuardia Ground", "freq": "121.7"},
                             "Clearance": {"name": "LaGuardia Clearance", "freq": "135.2"}},
           "plan": {"dest_city": "Orlando", "sid": "LGA7", "cruise": 36000, "departure_freq": "120.4", "squawk": "4521"}}
    print(build_reply("LaGuardia Clearance, Delta 1196, IFR to Orlando", ctx))
    print(build_reply("Cleared to Orlando via the LGA7, climb via SID, expect FL360, 120.4, squawk 4521, Delta 1196", ctx))
    gctx = dict(ctx, tuned={"name": "LaGuardia Ground", "kind": "Ground", "freq": "121.7"},
                airport_freqs=dict(ctx["airport_freqs"]))
    print(build_reply("LaGuardia Ground, Delta 1196, looking for IFR to Orlando", gctx))
    print(say_frequency(121.7), "|", say_frequency(118.725), "|", say_altitude(36000), "|", say_altitude(5000))
