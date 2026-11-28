"""LAIA: learns the ways in from real arrivals (Step 6, part 2).

LAIA Server writes the examples (examples.bin + meta.json) from the LAIA Collector's tracks at routegen's hubs and runs
this script in the background on the LAIA PC's graphics card. Each example is one real arrival: where it came within
45 nm of the airport (in its runway's frame), the wind, how busy it was, and the way it really flew to the threshold
(24 points at equal steps of its length, and its length). The network gives K candidate ways for each case and how
likely each is (trained "winner takes all": only the candidate nearest the real way learns from it, so the candidates
spread over the real choices — left or right downwind, straight in, the longer way round — instead of averaging them
into a way nobody flies). Held-back days (every seventh day of the year) are never trained on; the score on them is
printed each epoch. The weights are written as JSON for LAIA Server, which runs the network itself.

Usage: python train_routes.py --data <dir with examples.bin and meta.json> --out <model.json> [--pylib <dir>] [--epochs N]
"""
import argparse
import json
import os
import sys
import time


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--pylib", default="")
    ap.add_argument("--epochs", type=int, default=30)
    ap.add_argument("--k", type=int, default=6)
    ap.add_argument("--hidden", type=int, default=384)
    ap.add_argument("--cpu", action="store_true")
    a = ap.parse_args()
    if a.pylib and a.pylib not in sys.path:
        sys.path.insert(0, a.pylib)
    import numpy as np
    import torch
    import torch.nn as nn

    meta = json.load(open(os.path.join(a.data, "meta.json"), encoding="utf-8"))
    F, P = meta["f"], meta["p"]
    cols = 3 + F + P * 3 + 1
    raw = np.fromfile(os.path.join(a.data, "examples.bin"), dtype="<f4")
    if raw.size % cols != 0:
        print(f"examples.bin has {raw.size} numbers, not rows of {cols}", flush=True)
        return 2
    rows = raw.reshape(-1, cols)
    held = rows[:, 2] > 0.5
    train, test = rows[~held], rows[held]
    if len(train) < 200:
        print(f"only {len(train)} examples to learn from — not enough yet", flush=True)
        return 3
    dev = torch.device("cuda" if torch.cuda.is_available() and not a.cpu else "cpu")
    print(f"learning from {len(train)} real arrivals ({len(test)} held back) on {torch.cuda.get_device_name(0) if dev.type == 'cuda' else 'the processor'}", flush=True)
    nA = max(meta["airports"].values(), default=0) + 1
    nR = max(meta["runways"].values(), default=0) + 1
    K, H, E = a.k, a.hidden, 16

    class Net(nn.Module):
        def __init__(s):
            super().__init__()
            s.ea = nn.Embedding(nA, E)
            s.er = nn.Embedding(nR, E)
            s.l1 = nn.Linear(2 * E + F, H)
            s.l2 = nn.Linear(H, H)
            s.l3 = nn.Linear(H, H)
            s.out = nn.Linear(H, K * P * 3 + 2 * K)

        def forward(s, ai, ri, x):
            z = torch.cat([s.ea(ai), s.er(ri), x], dim=1)
            z = torch.relu(s.l1(z))
            z = torch.relu(s.l2(z))
            z = torch.relu(s.l3(z))
            return s.out(z)

    def tensors(r):
        ai = torch.tensor(r[:, 0], dtype=torch.long)
        ri = torch.tensor(r[:, 1], dtype=torch.long)
        x = torch.tensor(r[:, 3:3 + F], dtype=torch.float32)
        y = torch.tensor(r[:, 3 + F:], dtype=torch.float32)
        return ai, ri, x, y

    def losses(out, y):
        b = out.shape[0]
        paths = out[:, :K * P * 3].view(b, K, P, 3)
        logits = out[:, K * P * 3:K * P * 3 + K]
        lens = out[:, K * P * 3 + K:]
        t = y[:, :P * 3].view(b, 1, P, 3)
        xy = ((paths[..., :2] - t[..., :2]) ** 2).sum(-1).mean(-1)
        al = ((paths[..., 2] - t[..., 2]) ** 2).mean(-1)
        err = xy + 0.2 * al
        best = err.argmin(1)
        pick = err.gather(1, best[:, None]).squeeze(1)
        ce = nn.functional.cross_entropy(logits, best)
        le = (lens.gather(1, best[:, None]).squeeze(1) - y[:, P * 3]) ** 2
        return pick.mean() + 0.5 * ce + le.mean(), xy, logits

    net = Net().to(dev)
    opt = torch.optim.Adam(net.parameters(), lr=1e-3)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=a.epochs)
    tr = [t.to(dev) for t in tensors(train)]
    te = [t.to(dev) for t in tensors(test)] if len(test) else None
    n = len(train)
    bs = 512
    g = torch.Generator(device="cpu").manual_seed(7)
    t0 = time.time()
    for ep in range(1, a.epochs + 1):
        net.train()
        perm = torch.randperm(n, generator=g).to(dev)
        tot = 0.0
        for i in range(0, n, bs):
            idx = perm[i:i + bs]
            ai, ri, x, y = tr[0][idx], tr[1][idx], tr[2][idx], tr[3][idx]
            # Now and then the airport (and runway) hidden: the network also learns the general way traffic flows, for
            # airports it has never seen.
            drop = torch.rand(len(idx), device=dev) < 0.1
            ai = torch.where(drop, torch.zeros_like(ai), ai)
            ri = torch.where(drop, torch.zeros_like(ri), ri)
            loss, _, _ = losses(net(ai, ri, x), y)
            opt.zero_grad()
            loss.backward()
            opt.step()
            tot += float(loss) * len(idx)
        sched.step()
        msg = f"epoch {ep}/{a.epochs}: training {tot / n:.4f}"
        if te is not None and (ep % 5 == 0 or ep == a.epochs):
            net.eval()
            with torch.no_grad():
                out = net(te[0], te[1], te[2])
                _, xy, logits = losses(out, te[3])
                top = xy.gather(1, logits.argmax(1)[:, None]).squeeze(1)
                # Mean distance in nm (the targets are in units of 30 nm).
                msg += f"; held-back days: most likely way {float(top.sqrt().mean()) * 30:.2f} nm from the real one, best of {K} {float(xy.min(1).values.sqrt().mean()) * 30:.2f} nm"
        print(msg + f" ({time.time() - t0:.0f} s)", flush=True)

    net = net.cpu().eval()

    def mat(t):
        return [[round(float(v), 5) for v in row] for row in t.detach().numpy()]

    model = {
        "id": time.strftime("%Y%m%d-%H%M", time.gmtime()) + f"-{n}",
        "trained": time.strftime("%Y-%m-%d %H:%M UTC", time.gmtime()),
        "f": F, "p": P, "k": K,
        "airports": meta["airports"], "runways": meta["runways"],
        "emb_airport": mat(net.ea.weight), "emb_runway": mat(net.er.weight),
        "layers": [
            {"w": mat(net.l1.weight), "b": [round(float(v), 5) for v in net.l1.bias], "relu": True},
            {"w": mat(net.l2.weight), "b": [round(float(v), 5) for v in net.l2.bias], "relu": True},
            {"w": mat(net.l3.weight), "b": [round(float(v), 5) for v in net.l3.bias], "relu": True},
            {"w": mat(net.out.weight), "b": [round(float(v), 5) for v in net.out.bias], "relu": False},
        ],
    }
    tmp = a.out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(model, fh, separators=(",", ":"))
    os.replace(tmp, a.out)
    print(f"written {a.out} ({os.path.getsize(a.out) / 1e6:.1f} MB)", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
