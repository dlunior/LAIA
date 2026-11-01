"""Checks for the radio reply logic. Run by the automated build; fails the build on any mistake."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import phraseology as ph  # noqa: E402


def check(heard, ctx, must, must_not=None):
    reply = ph.build_reply(heard, ctx)
    ok = all(m.lower() in reply.lower() for m in must) and not any(m.lower() in reply.lower() for m in (must_not or []))
    print(("PASS " if ok else "FAIL ") + f"{heard!r}\n     -> {reply!r}")
    return ok


plan = {"callsign": "DAL1196", "dest_city": "Charleston", "sid": "TOTTZ1", "cruise": 39000, "departure_freq": "124.7", "squawk": "2026"}
center = {"tuned": {"name": "Atlanta Center", "kind": "Center", "freq": "132.975"}, "on_ground": False, "plan": plan}
clr = {"tuned": {"name": "Minneapolis Clearance", "kind": "Clearance", "freq": "133.2"}, "on_ground": True, "plan": plan,
       "airport_freqs": {"Ground": {"name": "Minneapolis Ground", "freq": "121.9"}}}
gnd = dict(clr, tuned={"name": "Minneapolis Ground", "kind": "Ground", "freq": "121.9"},
           airport_freqs={"Clearance": {"name": "Minneapolis Clearance", "freq": "133.2"}, "Ground": {"name": "Minneapolis Ground", "freq": "121.9"}})

results = [
    check("Atlanta Center, Delta 1196, flight level three niner zero, direct Foothills.", center, ["Delta eleven ninety-six, Atlanta Center, roger"]),
    check("Atlanta Center, Delta, alumni six, flight level three nine zero.", center, ["Delta eleven ninety-six"], ["say again"]),
    check("Atlanta Center, Delta 1196, IFR to Charleston.", center, ["say request"], ["cleared to"]),
    check("Minneapolis Ground, Delta 1196, IFR to Charleston.", gnd, ["contact Minneapolis Clearance", "one three three point two"]),
    check("Minneapolis Clearance, Delta 1196, IFR to Charleston.", clr, ["cleared to the Charleston airport", "TOTTZ one", "squawk two zero two six"]),
    check("Cleared to Charleston via the TOTTZ one, climb via SID, expect three nine zero, 124.7, squawk 2026.", clr, ["readback correct", "Minneapolis Ground"]),
    check("LaGuardia Ground, United 1532, radio check", {}, ["United fifteen thirty-two", "LaGuardia Ground", "five by five"]),
    check("hello hello", {}, ["say again"]),
]
print(f"{sum(results)}/{len(results)} passed")
sys.exit(0 if all(results) else 1)
