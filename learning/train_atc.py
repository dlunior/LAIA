"""LAIA: learns how real controllers handle sequencing, spacing and speed control (Step 6, Release 4b).

User, 28 Nov: "The goal isn't to make planes fly the routes from the ads data just learn how real ATC handles sequencing
and speed control" — "learn the techniques from those busy airports and apply that to all airports".

LAIA Server writes the rows (atc_speed.bin, atc_order.bin, atc_gaps.bin, atc_meta.json) from the real arrivals at the
hubs and runs this script in the background on the LAIA PC's graphics card. It learns, never a route:
- nominal speeds: the indicated speed each kind of aircraft (0 regional, 1 narrowbody, 2 heavy) is flown at each nm to
  go from 45 to 3 when nothing's in the way (more than 4 minutes behind the one before it) — per airport, and across
  all the hubs ("general");
- speed control: how much of that speed it's given at each distance when it has time to lose, for how much it has to
  lose and how busy the runway is (a small network);
- the landing order: how much later (or sooner) than its own unimpeded landing time real controllers have an arrival
  land, from where it is as it comes within 35 nm — trained so that the order it gives is the real landing order
  (pairs landing within 10 minutes of each other on a runway);
- the gap at the threshold behind each kind of leader when the queue is pressed (the follower had a minute or more to
  lose): the median, per airport and general.
Each network has the airport's code as an input; one time in ten it's left out ("general"), so the network also knows
the way across all the hubs for airports it has no data for. Held-back days (every seventh day of the year) are never
trained on; the scores on them are printed. The result is written as JSON for LAIA Server, which runs it itself.

Usage: python train_atc.py --data <dir with atc_*.bin and atc_meta.json> --out <atc.json> [--pylib <dir>] [--cpu]
"""
import argparse
import json
import math
import os
import sys
import time

SPEED_COLS = 10   # airport, held, cls, dtg, agl, gs, ias, tRemain, load, gapLeader
ORDER_COLS = 13   # airport, held, group, cls, dtg, agl, gs, sin, cos, offsetNm, t, thrT, load
GAP_COLS = 8      # airport, held, clsL, clsF, gap, fDtg, fTRemain, load


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--pylib", default="")
    ap.add_argument("--epochs", type=int, default=40)
    ap.add_argument("--cpu", action="store_true")
    a = ap.parse_args()
    if a.pylib and a.pylib not in sys.path:
        sys.path.insert(0, a.pylib)
    import numpy as np
    import torch
    import torch.nn as nn

    meta = json.load(open(os.path.join(a.data, "atc_meta.json"), encoding="utf-8"))
    airports = meta["airports"]
    names = {v: k for k, v in airports.items()}
    n_from, n_to = meta.get("nominal_from", 3), meta.get("nominal_to", 45)
    bins = np.arange(n_from, n_to + 1)

    def load(name, cols):
        raw = np.fromfile(os.path.join(a.data, name), dtype="<f4")
        if raw.size % cols != 0:
            raise SystemExit(f"{name} has {raw.size} numbers, not rows of {cols}")
        return raw.reshape(-1, cols)

    sp = load("atc_speed.bin", SPEED_COLS)
    od = load("atc_order.bin", ORDER_COLS)
    gp = load("atc_gaps.bin", GAP_COLS)
    sp_tr = sp[sp[:, 1] < 0.5]
    if len(sp_tr) < 500 or len(od) < 100:
        print(f"only {len(sp_tr)} speed samples and {len(od)} arrivals to learn from — not enough yet", flush=True)
        return 3
    dev = torch.device("cuda" if torch.cuda.is_available() and not a.cpu else "cpu")
    where = torch.cuda.get_device_name(0) if dev.type == "cuda" else "the processor"
    print(f"learning from {len(sp_tr)} speed samples and {int((od[:, 1] < 0.5).sum())} arrivals ({int((od[:, 1] > 0.5).sum())} held back) on {where}", flush=True)
    t0 = time.time()

    # ---------------------------------------------------------------- nominal speeds and times, unimpeded
    free = sp_tr[sp_tr[:, 9] > 240]
    nominal, nom_rem = {}, {}

    def table(rows, col, min_n):
        out = np.full((3, len(bins)), np.nan)
        for c in range(3):
            rc = rows[rows[:, 2] == c]
            for k, b in enumerate(bins):
                v = rc[np.abs(rc[:, 3] - b) <= 0.75, col]
                if len(v) >= min_n:
                    out[c, k] = float(np.median(v))
            # a little smoothing along the distance (3-nm running median)
            sm = out[c].copy()
            for k in range(len(bins)):
                w = out[c, max(0, k - 1):k + 2]
                w = w[~np.isnan(w)]
                if len(w):
                    sm[k] = float(np.median(w))
            out[c] = np.where(np.isnan(out[c]), np.nan, sm)
        return out

    nominal["general"] = table(free, 6, 15)
    nom_rem["general"] = table(free, 7, 15)
    for ai in np.unique(free[:, 0]).astype(int):
        if ai in names:
            r = free[free[:, 0] == ai]
            nominal[names[ai]] = table(r, 6, 12)
            nom_rem[names[ai]] = table(r, 7, 12)

    def lookup2(tab, ai, cls, dtg):
        """Per-row value from the airport's own table, else the general one (linear in distance)."""
        x = np.clip(dtg, n_from, n_to) - n_from
        i0 = np.clip(np.floor(x).astype(int), 0, len(bins) - 2)
        f = x - i0
        c = cls.astype(int)

        def val(t, m):
            return t[c[m], i0[m]] * (1 - f[m]) + t[c[m], i0[m] + 1] * f[m]

        res = np.full(len(ai), np.nan)
        for key, t in tab.items():
            if key == "general" or key not in airports:
                continue
            m = ai == airports[key]
            if m.any():
                res[m] = val(t, m)
        hole = np.isnan(res)
        if hole.any():
            res[hole] = val(tab["general"], hole)
        return res

    gen = nominal["general"]
    print("unimpeded speeds across the hubs (kt indicated at 40/30/20/10/5 nm, regional / narrowbody / heavy): " +
          " | ".join("/".join("–" if np.isnan(gen[c, b - n_from]) else f"{gen[c, b - n_from]:.0f}" for b in (40, 30, 20, 10, 5)) for c in range(3)), flush=True)

    # ---------------------------------------------------------------- the networks
    E = 8
    nA = max(airports.values(), default=0) + 1

    class Net(nn.Module):
        def __init__(s, nin, h=64):
            super().__init__()
            s.emb = nn.Embedding(nA, E)
            s.l1 = nn.Linear(E + nin, h)
            s.l2 = nn.Linear(h, h)
            s.out = nn.Linear(h, 1)

        def forward(s, ai, x):
            z = torch.cat([s.emb(ai), x], dim=1)
            z = torch.relu(s.l1(z))
            z = torch.relu(s.l2(z))
            return s.out(z)

        def export(s):
            def rows(t):
                return [[round(float(v), 6) for v in r] for r in t.detach().cpu().numpy()]
            return {"emb": rows(s.emb.weight), "layers": [
                {"w": rows(s.l1.weight), "b": [round(float(v), 6) for v in s.l1.bias.detach().cpu().numpy()], "relu": True},
                {"w": rows(s.l2.weight), "b": [round(float(v), 6) for v in s.l2.bias.detach().cpu().numpy()], "relu": True},
                {"w": rows(s.out.weight), "b": [round(float(v), 6) for v in s.out.bias.detach().cpu().numpy()], "relu": False}]}

    def onehot(c):
        c = c.astype(int)
        return np.stack([(c == 0), (c == 1), (c == 2)], 1).astype(np.float32)

    def drop_airport(ai, gen_share=0.1):
        ai = ai.clone()
        m = torch.rand(len(ai), device=ai.device) < gen_share
        ai[m] = 0
        return ai

    # Speed control: the share of the unimpeded speed flown, from distance, height, time to lose, load and class.
    nom_s = lookup2(nominal, sp[:, 0].astype(int), sp[:, 2], sp[:, 3])
    rem_s = lookup2(nom_rem, sp[:, 0].astype(int), sp[:, 2], sp[:, 3])
    ok = ~np.isnan(nom_s) & ~np.isnan(rem_s) & (nom_s > 100)
    s_all = sp[ok]
    ratio = np.clip(s_all[:, 6] / nom_s[ok], 0.5, 1.4)
    lose = np.clip(s_all[:, 7] - rem_s[ok], -300, 1200)
    xs = np.concatenate([onehot(s_all[:, 2]), np.stack([s_all[:, 3] / 45, s_all[:, 4] / 20000, lose / 600, s_all[:, 8] / 20], 1)], 1).astype(np.float32)
    held_s = s_all[:, 1] > 0.5
    speed_net = Net(xs.shape[1]).to(dev)
    A = torch.tensor(s_all[:, 0], dtype=torch.long, device=dev)
    X = torch.tensor(xs, device=dev)
    Y = torch.tensor(ratio, dtype=torch.float32, device=dev).unsqueeze(1)
    tr = torch.tensor(~held_s, device=dev)
    te = torch.tensor(held_s, device=dev)
    opt = torch.optim.Adam(speed_net.parameters(), lr=2e-3)
    n_tr = int(tr.sum())
    for ep in range(a.epochs):
        idx = torch.nonzero(tr).squeeze(1)
        idx = idx[torch.randperm(len(idx), device=dev)]
        for k in range(0, len(idx), 4096):
            b = idx[k:k + 4096]
            loss = nn.functional.smooth_l1_loss(speed_net(drop_airport(A[b]), X[b]), Y[b], beta=0.05)
            opt.zero_grad(); loss.backward(); opt.step()
        if ep % 10 == 9 or ep == a.epochs - 1:
            with torch.no_grad():
                if te.any():
                    p = speed_net(A[te], X[te])
                    mae = float((p - Y[te]).abs().mean()); base = float((Y[te] - 1).abs().mean())
                    print(f"epoch {ep + 1}: speeds on the held-back days {100 * mae:.1f}% off the real ones (standard speeds alone {100 * base:.1f}%)", flush=True)
    with torch.no_grad():
        if te.any():
            p = speed_net(A[te], X[te]); mae = float((p - Y[te]).abs().mean()); base = float((Y[te] - 1).abs().mean())
            print(f"speed control on held-back days: {100 * mae:.1f}% off the real speeds against {100 * base:.1f}% flying the unimpeded ones (from {n_tr} samples)", flush=True)

    # Landing order: an offset (minutes) added to each arrival's own unimpeded landing time.
    rem_o = lookup2(nom_rem, od[:, 0].astype(int), od[:, 3], od[:, 4])
    ok = ~np.isnan(rem_o)
    o = od[ok]; rem_o = rem_o[ok]
    eta = o[:, 10].astype(np.float64) + rem_o                       # its own landing time, flown unimpeded from here
    xo = np.concatenate([onehot(o[:, 3]), np.stack([o[:, 4] / 45, o[:, 5] / 20000, o[:, 6] / 300, o[:, 7], o[:, 8], o[:, 9] / 30, o[:, 12] / 20], 1)], 1).astype(np.float32)
    # Pairs on the same runway and day landing within 10 minutes: i really landed before j.
    pi, pj = [], []
    order = np.lexsort((o[:, 11], o[:, 2]))
    g, thr = o[order, 2], o[order, 11]
    for k in range(len(order)):
        m = k + 1
        while m < len(order) and g[m] == g[k] and thr[m] - thr[k] < 600:
            if thr[m] > thr[k]:
                pi.append(order[k]); pj.append(order[m])
            m += 1
    pi, pj = np.array(pi, dtype=np.int64), np.array(pj, dtype=np.int64)
    if len(pi) < 50:
        print(f"only {len(pi)} pairs landing close together — landing order not learned", flush=True)
        order_net = None
    else:
        held_p = o[pi, 1] > 0.5
        order_net = Net(xo.shape[1]).to(dev)
        Ao = torch.tensor(o[:, 0], dtype=torch.long, device=dev)
        Xo = torch.tensor(xo, device=dev)
        ETA = torch.tensor(eta / 60, dtype=torch.float32, device=dev)
        PI = torch.tensor(pi, device=dev); PJ = torch.tensor(pj, device=dev)
        trp = torch.tensor(~held_p, device=dev); tep = torch.tensor(held_p, device=dev)
        opt = torch.optim.Adam(order_net.parameters(), lr=2e-3)

        def acc(mask, use_net):
            with torch.no_grad():
                s = ETA + (order_net(Ao, Xo).squeeze(1) if use_net else 0)
                return float((s[PI[mask]] < s[PJ[mask]]).float().mean())

        print(f"landing order on held-back days by each one's own time: {100 * acc(tep, False):.0f}% of {int(tep.sum())} pairs as real" if tep.any() else "no held-back pairs", flush=True)
        for ep in range(a.epochs):
            idx = torch.nonzero(trp).squeeze(1)
            idx = idx[torch.randperm(len(idx), device=dev)]
            for k in range(0, len(idx), 8192):
                b = idx[k:k + 8192]
                ia, ja = PI[b], PJ[b]
                oi = order_net(drop_airport(Ao[ia]), Xo[ia]).squeeze(1)
                oj = order_net(drop_airport(Ao[ja]), Xo[ja]).squeeze(1)
                d = (ETA[ia] + oi) - (ETA[ja] + oj)      # < 0 when the order is right (minutes)
                loss = nn.functional.softplus(d * 2).mean() + 0.01 * (oi ** 2 + oj ** 2).mean()
                opt.zero_grad(); loss.backward(); opt.step()
            if (ep % 10 == 9 or ep == a.epochs - 1) and tep.any():
                print(f"epoch {ep + 1}: landing order on held-back days {100 * acc(tep, True):.0f}% as real", flush=True)
        if tep.any():
            print(f"landing order on held-back days: {100 * acc(tep, True):.0f}% as real, against {100 * acc(tep, False):.0f}% by each one's own time ({len(pi)} pairs)", flush=True)

    # Gaps at the threshold when the queue was pressed.
    gaps = {}
    g_tr = gp[(gp[:, 1] < 0.5) & (gp[:, 5] > 0)]
    if len(g_tr):
        rem_g = lookup2(nom_rem, g_tr[:, 0].astype(int), g_tr[:, 3], g_tr[:, 5])
        pressed = g_tr[(g_tr[:, 6] - rem_g > 60) & (g_tr[:, 4] < 300) & (g_tr[:, 4] > 30)]
        for key, rows in [("general", pressed)] + [(names[ai], pressed[pressed[:, 0] == ai]) for ai in np.unique(pressed[:, 0]).astype(int) if ai in names]:
            d = {}
            for cl in range(3):
                for cf in range(3):
                    v = rows[(rows[:, 2] == cl) & (rows[:, 3] == cf), 4]
                    if len(v) >= 20:
                        d[f"{cl}-{cf}"] = round(float(np.median(v)), 1)
            if d:
                gaps[key] = d
        gg = gaps.get("general", {})
        print("gaps at the threshold when pressed, across the hubs: " + ", ".join(f"{k} {v:.0f} s" for k, v in sorted(gg.items())) + f" ({len(pressed)} pairs)", flush=True)

    def tab_json(t):
        return [[None if np.isnan(v) else round(float(v), 1) for v in row] for row in t]

    model = {
        "id": "atc-" + time.strftime("%Y%m%d-%H%M", time.gmtime()),
        "trained": time.strftime("%Y-%m-%d %H:%M UTC", time.gmtime()),
        "airports": airports,
        "nominal": {k: tab_json(v) for k, v in nominal.items()},
        "gaps": gaps,
        "speed": speed_net.export(),
    }
    if order_net is not None:
        model["order"] = order_net.export()
    tmp = a.out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(model, fh, allow_nan=False)
    os.replace(tmp, a.out)
    print(f"written {model['id']} in {time.time() - t0:.0f} s: unimpeded speeds for {len(nominal) - 1} hubs, gaps for {max(0, len(gaps) - 1)}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
