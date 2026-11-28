"""CI check for train_atc.py: synthetic arrivals at a test airport ("TEST") written in LAIA Server's rows, with known
controller habits — unimpeded speeds 250 kt beyond 20 nm, 210 to 12 nm, 180 to 7 nm, then 150; arrivals with time to
lose flown at 75 % of that beyond 20 nm; heavies landing two minutes later than their own time would give; a 90 s gap
behind a narrowbody when pressed. Trained for a few epochs on the processor, the habits checked in the model, and the
model written where the C# tests read it (LAIA_TEST_ATC)."""
import argparse
import json
import math
import os
import random
import struct
import subprocess
import sys
import tempfile


def nominal(d):
    return 250 if d > 20 else 210 if d > 12 else 180 if d > 7 else 150


def remaining(d, slow=1.0):
    t, x = 0.0, d
    while x > 0:
        v = nominal(x) * (slow if x > 20 else 1.0)
        t += min(0.25, x) / v * 3600
        x -= 0.25
    return t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    rnd = random.Random(5)
    d = tempfile.mkdtemp(prefix="laia-atc-")
    speed, order, gaps = [], [], []
    group = 0
    for day in range(28):
        held = 1.0 if day % 7 == 0 else 0.0
        group += 1
        # 40 arrivals an hour, some heavies.
        t_land = 3600.0
        prev_cls = None
        for k in range(120):
            cls = 2 if k % 5 == 0 else 1 if k % 5 != 1 else 0
            pressed = k % 3 != 0
            gap = 90 if (pressed and prev_cls == 1) else 140 if pressed else rnd.uniform(250, 400)
            t_land += gap
            slow = 0.75 if pressed else 1.0
            # its own (unimpeded) landing time at 35 nm, and the real one: heavies 120 s later than their own time
            t35 = t_land - remaining(35, slow)
            own = t35 + remaining(35)
            for dist in range(45, 2, -1):
                ias = nominal(dist) * (slow if dist > 20 else 1.0)
                trem = remaining(dist, slow)
                alt = min(12000, dist * 320)
                gs = ias * (1 + 0.02 * alt / 1000)
                speed.append([1, held, cls, dist, alt, gs, ias, trem, 10, gap])
            # where it is at 35 nm: heavies placed so their own time says sooner than they really land
            order_t = t35 if cls != 2 else t35 - 120
            order.append([1, held, group, cls, 35, 10000, 300, 0.0, 1.0, 0.0, order_t, t_land, 10])
            if prev_cls is not None:
                gaps.append([1, held, prev_cls, cls, gap, 35, remaining(35, slow), 10])
            prev_cls = cls
    for name, rows in (("atc_speed.bin", speed), ("atc_order.bin", order), ("atc_gaps.bin", gaps)):
        with open(os.path.join(d, name), "wb") as fh:
            for r in rows:
                fh.write(struct.pack("<" + "f" * len(r), *r))
    json.dump({"format": "atc1", "airports": {"TEST": 1}, "nominal_from": 3, "nominal_to": 45}, open(os.path.join(d, "atc_meta.json"), "w"))
    here = os.path.dirname(os.path.abspath(__file__))
    code = subprocess.call([sys.executable, os.path.join(here, "train_atc.py"), "--data", d, "--out", a.out, "--epochs", "30", "--cpu"])
    if code != 0:
        print("training failed", code)
        return code
    m = json.load(open(a.out))
    gen = m["nominal"]["general"]
    v30 = gen[1][30 - 3]
    assert v30 is not None and abs(v30 - 250) < 15, f"unimpeded narrowbody speed at 30 nm {v30}"
    g11 = m["gaps"].get("general", {}).get("1-1")
    assert g11 is not None and abs(g11 - 90) < 10, f"pressed gap behind a narrowbody {g11}"
    assert "speed" in m and "order" in m, "networks written"
    print("ATC model trained and written:", a.out, "— 30 nm", v30, "kt, gap", g11, "s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
