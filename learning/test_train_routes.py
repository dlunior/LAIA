"""CI check for train_routes.py: synthetic arrivals at a test airport ("TEST", runway 27) — straight in from the east
(the approach side) and round a left downwind from the west — written in LAIA Server's example format, trained for a
few epochs on the processor, and the model written where the C# tests read it (LAIA_TEST_MODEL)."""
import argparse
import json
import math
import os
import random
import struct
import subprocess
import sys
import tempfile

F, P = 9, 24


def resample(pts, alts):
    cum = [0.0]
    for i in range(1, len(pts)):
        cum.append(cum[-1] + math.dist(pts[i], pts[i - 1]))
    total = cum[-1]
    out, j = [], 0
    for k in range(1, P + 1):
        s = total * k / P
        while j + 1 < len(pts) - 1 and cum[j + 1] < s:
            j += 1
        seg = cum[j + 1] - cum[j]
        f = 0 if seg < 1e-9 else min(1, max(0, (s - cum[j]) / seg))
        x = pts[j][0] + (pts[j + 1][0] - pts[j][0]) * f
        y = pts[j][1] + (pts[j + 1][1] - pts[j][1]) * f
        a = alts[j] + (alts[j + 1] - alts[j]) * f
        out += [x / 30, y / 30, a / 10000]
    return out + [total / 100]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    rnd = random.Random(3)
    d = tempfile.mkdtemp(prefix="laia-train-")
    rows = []
    for i in range(1600):
        held = 1.0 if i % 7 == 0 else 0.0
        if i % 2 == 0:
            # Straight in from the east: 30–45 nm out on the centreline (± a little), flying the runway's way.
            x0, y0 = rnd.uniform(30, 45), rnd.uniform(-2, 2)
            path = [(x0, y0), (x0 * 0.5, y0 * 0.3), (12, 0), (0, 0)]
            dh = 0.0
        else:
            # From the west (beyond the field): a left downwind 4 nm out, base at 12 nm, final.
            x0, y0 = rnd.uniform(-35, -25), rnd.uniform(2, 8)
            path = [(x0, y0), (0, 4), (12, 4), (12, 0), (0, 0)]
            dh = math.pi
        alts = [12000 - 12000 * k / (len(path) - 1) for k in range(len(path))]
        feats = [x0 / 30, y0 / 30, alts[0] / 10000, math.sin(dh), math.cos(dh), 0, 0, 0.2, 0.2]
        rows.append([1.0, 1.0, held] + feats + resample(path, alts))
    with open(os.path.join(d, "examples.bin"), "wb") as fh:
        for r in rows:
            fh.write(struct.pack("<" + "f" * len(r), *r))
    json.dump({"f": F, "p": P, "rows": len(rows), "airports": {"TEST": 1}, "runways": {"TEST/27": 1}}, open(os.path.join(d, "meta.json"), "w"))
    here = os.path.dirname(os.path.abspath(__file__))
    code = subprocess.call([sys.executable, os.path.join(here, "train_routes.py"), "--data", d, "--out", a.out, "--epochs", "25", "--cpu"])
    if code != 0:
        print("training failed", code)
        return code
    m = json.load(open(a.out))
    assert m["k"] >= 2 and len(m["layers"]) == 4, "model shape"
    print("route model trained and written:", a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
