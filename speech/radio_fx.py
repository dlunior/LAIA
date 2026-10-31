"""Make clean TTS audio sound like it came over a VHF aircraft radio."""
import numpy as np


def radio_filter(samples, sr, seed=None):
    x = np.asarray(samples, dtype=np.float32).flatten()
    if x.size == 0:
        return x
    rng = np.random.default_rng(seed)
    # Band-limit to roughly 300-3400 Hz like an AM airband receiver.
    n = int(2 ** np.ceil(np.log2(x.size + 1)))
    spec = np.fft.rfft(x, n)
    freqs = np.fft.rfftfreq(n, 1.0 / sr)
    lo, hi = 300.0, 3400.0
    gain = np.clip((freqs - lo * 0.7) / (lo * 0.3), 0, 1) * np.clip((hi * 1.15 - freqs) / (hi * 0.15), 0, 1)
    x = np.fft.irfft(spec * gain, n)[: x.size].astype(np.float32)
    # Gentle compression / saturation.
    peak = np.max(np.abs(x)) or 1.0
    x = np.tanh(2.2 * x / peak) / np.tanh(2.2)
    # A little hiss, plus a squelch tail at the end.
    x = x * 0.82 + rng.normal(0, 0.012, x.size).astype(np.float32)
    tail = rng.normal(0, 0.05, int(sr * 0.07)).astype(np.float32) * np.linspace(1, 0, int(sr * 0.07), dtype=np.float32)
    lead = np.zeros(int(sr * 0.05), dtype=np.float32)
    return np.concatenate([lead, x, tail]).astype(np.float32)
