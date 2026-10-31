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


def build_reply(heard):
    """Return the controller's reply text for what the pilot said."""
    text = (heard or "").strip()
    low = text.lower()
    if len(_tokens(text)) < 2:
        return "Station calling, say again."

    callsign, _, cs_key = find_callsign(text)
    facility, kind = find_facility(text)
    if facility:
        _LAST_ANY["facility"] = facility
        if cs_key:
            _LAST_FACILITY[cs_key] = facility
    else:
        facility = _LAST_FACILITY.get(cs_key) if cs_key else _LAST_ANY["facility"]

    if not callsign:
        return f"Station calling {facility}, say again your call sign." if facility else "Station calling, say again your call sign."

    # FAA phraseology: callsign, then facility (optional when already in contact).
    head = f"{callsign}, {facility}" if facility else callsign

    def has(*phrases):
        return any(p in low for p in phrases)

    if has("radio check", "how do you read", "how do you hear"):
        return f"{head}, read you five by five."
    if has("say again", "repeat"):
        return f"{head}, I say again, go ahead."
    if has("clearance", "ifr to", "i f r to", "ready to copy"):
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
