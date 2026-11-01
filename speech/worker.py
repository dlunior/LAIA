"""LAIA speech engine.

Runs inside the LAIA Server service on the ATC PC. It listens on localhost only:
  GET  /health  -> is the engine loaded, which model/device
  POST /radio   -> body: WAV of the pilot's transmission; returns WAV of the controller's reply
  POST /say     -> body: {"text": "..."}; returns WAV (control page voice test)

Speech-to-text: faster-whisper (graphics card when available, otherwise processor).
Text-to-speech: Kokoro (bundled), with the user's FastKoko server as a backup.
"""
import argparse
import io
import json
import os
import sys
import threading
import time
import traceback
import urllib.parse
import urllib.request
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import numpy as np  # noqa: E402

from phraseology import build_reply  # noqa: E402
from radio_fx import radio_filter  # noqa: E402

WHISPER_PROMPT = (
    "Air traffic control radio. Delta, United, American, Air Canada, JetBlue, Southwest. "
    "LaGuardia Ground, Kennedy Tower, New York Approach, Atlanta Center. Ready to taxi, request IFR clearance, "
    "with information Alpha, holding short runway two two, climbing to one zero thousand, flight level three five zero, "
    "niner, tree, fife."
)


# Phrases Whisper is known to invent from silence, noise or a click.
PHANTOMS = {
    "thank you", "thanks", "thank you very much", "thanks for watching", "thank you for watching",
    "bye", "bye bye", "you", "okay", "ok", "so", "uh", "um", "hmm", "please subscribe",
    "subscribe", "music", "applause", "silence", "the end",
}


def is_phantom(text, seconds):
    t = "".join(ch for ch in text.lower() if ch.isalnum() or ch == " ").strip()
    t = " ".join(t.split())
    if not t:
        return True
    return t in PHANTOMS and seconds < 3.5


def data_dir():
    return os.environ.get("LAIA_DATA") or os.path.join(os.environ.get("PROGRAMDATA", HERE), "LAIA")


def default_config():
    models = os.path.join(data_dir(), "models")
    return {
        "whisper_model": "base.en",
        "whisper_device": "auto",
        "voice": "am_michael",
        "speed": 1.08,
        "kokoro_model": os.path.join(models, "kokoro", "kokoro-v1.0.onnx"),
        "kokoro_voices": os.path.join(models, "kokoro", "voices-v1.0.bin"),
        "fallback_tts_url": "https://voice.sageks.dev/v1/audio/speech",
        "fallback_tts_voice": "am_michael",
        "models_dir": models,
    }


def load_config(path):
    cfg = default_config()
    if path and os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8-sig") as f:
                cfg.update({k: v for k, v in json.load(f).items() if v not in (None, "")})
        except Exception as e:  # keep defaults if the file is damaged
            log(f"config unreadable ({e}); using defaults")
    return cfg


def log(msg):
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def add_cuda_paths(cfg=None):
    """The NVIDIA pip wheels keep their DLLs in <folder>/nvidia/*/bin; make them findable.
    Setup installs them in ProgramData/LAIA/cuda (kept across updates); older installs had them in site-packages."""
    bases = list(sys.path)
    cuda_dir = (cfg or {}).get("cuda_dir") or os.path.join(data_dir(), "cuda")
    bases.insert(0, cuda_dir)
    for base in bases:
        nv = os.path.join(base, "nvidia")
        if not os.path.isdir(nv):
            continue
        for sub in os.listdir(nv):
            b = os.path.join(nv, sub, "bin")
            if os.path.isdir(b):
                try:
                    os.add_dll_directory(b)
                except Exception:
                    pass
                os.environ["PATH"] = b + os.pathsep + os.environ.get("PATH", "")


def whisper_root(cfg):
    return os.path.join(cfg["models_dir"], "whisper")


# ----------------------------------------------------------------------------- audio helpers

def read_wav(data):
    with wave.open(io.BytesIO(data), "rb") as w:
        sr, ch, width, frames = w.getframerate(), w.getnchannels(), w.getsampwidth(), w.readframes(w.getnframes())
    if width == 2:
        a = np.frombuffer(frames, dtype="<i2").astype(np.float32) / 32768.0
    elif width == 4:
        a = np.frombuffer(frames, dtype="<i4").astype(np.float32) / 2147483648.0
    elif width == 1:
        a = (np.frombuffer(frames, dtype=np.uint8).astype(np.float32) - 128) / 128.0
    else:
        raise ValueError("unsupported WAV sample width")
    if ch > 1:
        a = a.reshape(-1, ch).mean(axis=1)
    if sr != 16000 and a.size:
        n = int(a.size * 16000 / sr)
        a = np.interp(np.linspace(0, a.size - 1, n), np.arange(a.size), a).astype(np.float32)
    return a


def to_wav(samples, sr):
    s = np.clip(np.asarray(samples, dtype=np.float32), -1, 1)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(int(sr))
        w.writeframes((s * 32767).astype("<i2").tobytes())
    return buf.getvalue()


# ----------------------------------------------------------------------------- engine

class Engine:
    def __init__(self, cfg):
        self.cfg = cfg
        self.whisper = None
        self.whisper_device = None
        self.kokoro = None
        self.tts_name = None
        self.ready = False
        self.failed = False
        self.detail = "Loading speech models…"
        self.last_ms = None
        self.lock = threading.Lock()

    def load(self):
        try:
            self._load_whisper()
            self._load_tts()
            self.ready = True
            self.detail = None
            log("speech engine ready")
        except Exception as e:
            self.failed = True
            self.detail = f"Speech engine failed to load: {e}"
            log(traceback.format_exc())

    def _load_whisper(self):
        from faster_whisper import WhisperModel
        name = self.cfg["whisper_model"]
        want = self.cfg.get("whisper_device", "auto")
        attempts = []
        if want in ("auto", "cuda"):
            add_cuda_paths(self.cfg)
            attempts.append(("cuda", "float16"))
        attempts.append(("cpu", "int8"))
        last = None
        for device, compute in attempts:
            try:
                self.detail = f"Loading speech recognition ({name}) on {'graphics card' if device == 'cuda' else 'processor'}…"
                log(self.detail)
                m = WhisperModel(name, device=device, compute_type=compute, download_root=whisper_root(self.cfg))
                segs, _ = m.transcribe(np.zeros(16000, dtype=np.float32), language="en", beam_size=1)
                list(segs)  # forces CUDA libraries to actually load
                self.whisper, self.whisper_device = m, device
                log(f"whisper {name} loaded on {device}")
                return
            except Exception as e:
                last = e
                log(f"whisper on {device} failed: {e}")
        raise RuntimeError(f"could not load Whisper: {last}")

    def _load_tts(self):
        mp, vp = self.cfg["kokoro_model"], self.cfg["kokoro_voices"]
        if os.path.exists(mp) and os.path.exists(vp):
            try:
                self.detail = "Loading controller voice…"
                from kokoro_onnx import Kokoro
                self.kokoro = Kokoro(mp, vp)
                self.kokoro.create("Ready.", voice=self.cfg["voice"], speed=1.0, lang="en-us")
                self.tts_name = f"Kokoro ({self.cfg['voice']}) on this PC"
                log("kokoro loaded")
                return
            except Exception as e:
                log(f"kokoro failed: {e}")
        if self.cfg.get("fallback_tts_url"):
            self.tts_name = "FastKoko backup server"
            log("using fallback TTS server")
            return
        raise RuntimeError("no text-to-speech available")

    # ---- operations

    def transcribe(self, audio):
        """Returns what the pilot said, or "" for a stray key-up / silence / Whisper phantom phrase."""
        if audio.size < 4800:  # under 0.3 s
            return ""
        rms = float(np.sqrt(np.mean(np.square(audio))))
        if rms < 0.004:  # essentially silence: Whisper would invent "Thank you."
            log(f"ignored near-silent transmission (rms {rms:.4f})")
            return ""
        segs, _ = self.whisper.transcribe(
            audio, language="en", beam_size=5, vad_filter=True, initial_prompt=WHISPER_PROMPT,
            condition_on_previous_text=False, without_timestamps=True)
        kept = []
        for s in segs:
            if s.no_speech_prob > 0.6 and s.avg_logprob < -0.8:
                continue
            kept.append(s.text.strip())
        text = " ".join(kept).strip()
        if is_phantom(text, audio.size / 16000.0):
            log(f"ignored phantom phrase {text!r}")
            return ""
        return text

    def speak(self, text, radio=True):
        samples, sr = None, None
        if self.kokoro is not None:
            try:
                samples, sr = self.kokoro.create(text, voice=self.cfg["voice"], speed=float(self.cfg.get("speed", 1.0)), lang="en-us")
            except Exception as e:
                log(f"kokoro synth failed: {e}")
        if samples is None:
            samples, sr = self._fallback_tts(text)
        if radio:
            samples = radio_filter(samples, sr)
        return to_wav(samples, sr)

    def _fallback_tts(self, text):
        body = json.dumps({"model": "kokoro", "input": text, "voice": self.cfg.get("fallback_tts_voice", "am_michael"),
                           "response_format": "wav", "speed": float(self.cfg.get("speed", 1.0))}).encode()
        req = urllib.request.Request(self.cfg["fallback_tts_url"], data=body, headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=30) as r:
            data = r.read()
        with wave.open(io.BytesIO(data), "rb") as w:
            sr = w.getframerate()
            a = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
            if w.getnchannels() > 1:
                a = a.reshape(-1, w.getnchannels()).mean(axis=1)
        return a, sr

    def health(self):
        return {"ready": self.ready, "failed": self.failed, "detail": self.detail,
                "whisper_model": self.cfg["whisper_model"], "whisper_device": self.whisper_device,
                "tts": self.tts_name, "last_ms": self.last_ms}


ENGINE = None


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _send(self, code, body, ctype, headers=None):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def _json(self, code, obj):
        self._send(code, json.dumps(obj).encode(), "application/json")

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        if self.path.startswith("/health"):
            self._json(200, ENGINE.health())
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self):
        try:
            if not ENGINE.ready:
                return self._json(503, {"error": ENGINE.detail or "loading"})
            if self.path.startswith("/radio"):
                t0 = time.time()
                audio = read_wav(self._body())
                ctx = {}
                raw_ctx = self.headers.get("X-LAIA-Context")
                if raw_ctx:
                    try:
                        ctx = json.loads(urllib.parse.unquote(raw_ctx))
                    except Exception as e:
                        log(f"bad context header: {e}")
                with ENGINE.lock:
                    heard = ENGINE.transcribe(audio)
                    if not heard:
                        # Nothing real was said (stray key-up): stay quiet like a real controller.
                        return self._send(204, b"", "text/plain", {"X-LAIA-Heard": "", "X-LAIA-Reply": ""})
                    if ctx.get("no_atc"):
                        # Tuned to a frequency nobody answers on (CTAF, ATIS, or nothing nearby).
                        log(f"heard on unanswered frequency: {heard!r}")
                        return self._send(204, b"", "text/plain", {
                            "X-LAIA-Heard": urllib.parse.quote(heard),
                            "X-LAIA-Note": urllib.parse.quote(ctx.get("note") or "")})
                    reply = build_reply(heard, ctx)
                    wav = ENGINE.speak(reply)
                ENGINE.last_ms = (time.time() - t0) * 1000
                log(f"heard: {heard!r} -> {reply!r} ({ENGINE.last_ms:.0f} ms)")
                return self._send(200, wav, "audio/wav", {
                    "X-LAIA-Heard": urllib.parse.quote(heard), "X-LAIA-Reply": urllib.parse.quote(reply)})
            if self.path.startswith("/say"):
                req = json.loads(self._body() or b"{}")
                text = (req.get("text") or "").strip()[:600]
                if not text:
                    return self._json(400, {"error": "no text"})
                with ENGINE.lock:
                    wav = ENGINE.speak(text, radio=bool(req.get("radio", True)))
                return self._send(200, wav, "audio/wav")
            self._json(404, {"error": "not found"})
        except Exception as e:
            log(traceback.format_exc())
            self._json(500, {"error": str(e)})


def watch_parent(pid):
    """Exit if the LAIA service that started us goes away, so we never leave an orphan holding the port."""
    if not pid or os.name != "nt":
        return
    import ctypes
    k32 = ctypes.windll.kernel32
    h = k32.OpenProcess(0x00100000, False, int(pid))  # SYNCHRONIZE
    if not h:
        os._exit(0)
    k32.WaitForSingleObject(h, 0xFFFFFFFF)
    log("parent service exited; stopping")
    os._exit(0)


def prefetch_whisper(cfg, name):
    from faster_whisper.utils import download_model
    root = whisper_root(cfg)
    os.makedirs(root, exist_ok=True)
    log(f"downloading Whisper {name} to {root}")
    path = download_model(name, cache_dir=root)
    log(f"done: {path}")


def selftest(cfg):
    """Speak a radio call with Kokoro, then transcribe it back with Whisper and run the reply logic."""
    eng = Engine(cfg)
    eng.load()
    if not eng.ready:
        print("SELFTEST FAIL: engine did not load", flush=True)
        return 1
    call = "LaGuardia Ground, Delta twelve thirty four, ready to taxi."
    wav = eng.speak(call, radio=False)
    heard = eng.transcribe(read_wav(wav))
    reply = build_reply(heard)
    print(f"SELFTEST spoke: {call}\nSELFTEST heard: {heard}\nSELFTEST reply: {reply}", flush=True)
    ok = "delta" in heard.lower() and reply.lower().startswith("delta")
    print("SELFTEST " + ("PASS" if ok else "FAIL"), flush=True)
    return 0 if ok else 1


def main():
    global ENGINE
    ap = argparse.ArgumentParser()
    ap.add_argument("--config")
    ap.add_argument("--port", type=int, default=47811)
    ap.add_argument("--parent-pid", type=int)
    ap.add_argument("--prefetch-whisper")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    cfg = load_config(a.config)
    if a.prefetch_whisper:
        prefetch_whisper(cfg, a.prefetch_whisper)
        return 0
    if a.selftest:
        return selftest(cfg)

    ENGINE = Engine(cfg)
    threading.Thread(target=watch_parent, args=(a.parent_pid,), daemon=True).start()
    threading.Thread(target=ENGINE.load, daemon=True).start()
    server = ThreadingHTTPServer(("127.0.0.1", a.port), Handler)
    log(f"speech engine listening on 127.0.0.1:{a.port}")
    server.serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
