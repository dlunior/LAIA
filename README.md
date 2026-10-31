# LAIA
Local AI ATC: AI traffic and air traffic control for MSFS 2024

LAIA splits the work across two PCs on your home network:

| PC | Installer | What it does |
|----|-----------|--------------|
| ATC PC (graphics card) | `LAIA-Server-Setup.exe` | Runs LAIA as a Windows service: speech recognition (Whisper), controller voice (Kokoro), ATC logic, control page at `http://<pc-name>:47801` |
| Flight sim PC | `LAIA-Sim-Setup.exe` | A small tray radio: push-to-talk, sends your call to the server, plays the reply, shows a transcript |

Both installers detect everything on their own (simulator version, Community folder, AIG AI Manager,
graphics card, the server on the network). Download them from the latest release.

## Repository layout

```
src/Laia.Common         shared: network discovery, MSFS / AIG / hardware detection
src/Laia.Server         Windows service on the ATC PC (HTTP + UDP discovery + speech engine supervisor)
src/Laia.Sim            tray app on the sim PC (push-to-talk, audio, transcript)
src/Laia.Setup.Common   modern installer window, install engine, payload handling
src/Laia.Setup.Server   ATC PC installer
src/Laia.Setup.Sim      sim PC installer
speech/                 Python speech engine (faster-whisper + Kokoro) and step-1 phraseology
build/                  packaging and the end-to-end test run by GitHub Actions
```

Builds run on GitHub Actions (`.github/workflows/build.yml`): every push to `main` builds both installers,
installs them on a clean Windows machine, does a full voice round trip, uninstalls, and publishes a release.
