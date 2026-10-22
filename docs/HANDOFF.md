# Handoff and resume guide

**Snapshot:** 2026-10-22 (Asia/Seoul)
**Project root:** `C:\Users\wt201\OneDrive\Desktop\blocker`
**Product:** VeilDesk 1.0.0, Windows 10/11 x64, .NET 8 + WPF

This document is for the next developer or agent continuing the work. Read
`docs/CURRENT.md` first for the short status, then this file before running the
app. Claims in the repository are deliberately split into **VERIFIED**,
**OBSERVED**, **INFERRED** and **NOT RUN**; keep that discipline.

---

## 1. Where the project stands

Implemented and independently verified:

- Windows Graphics Capture + D3D11 capture on the reference laptop.
- Real local ONNX inference (NudeNet 320n, YOLOv8n 320/480, MediaPipe selfie
  segmentation), CPU by default.
- Static-image anatomical detection on Michelangelo's *David*; three correct
  person boxes on `bus.jpg`.
- Live Start/Stop, fail-closed supervision, a live session with 103 detection
  events and 11 peak tracks, and overlay exclusion from capture.
- Ten-page WPF UI rendered at 150% DPI.
- Screenshot export path plus a deterministic synthetic-region compositor probe
  (`tools/ExportProbe`).
- MJPEG AVI container, now with a correct `movi` LIST size and `idx1` index;
  Windows Media Foundation opens the 30-frame probe as 640×360, 2.000 s,
  96.4% non-black.
- 23/23 Core tests passing.
- Built-in pack validation and a first-launch copy into an empty data root.
- Self-contained Release package: 238.2 MB folder, 110.5 MB ZIP; final hash in
  `PACKAGE-SHA256.txt`. Published executable launched on the build host, all
  four packs installed.

Still requiring a live, unlocked desktop:

- Visual confirmation that the transparent overlay sits exactly over a detected
  anatomical region on the physical display.
- Visual confirmation of moving-target tracking / motion padding.
- An end-to-end run of the in-app `VideoRecorder` (start → frames → stop →
  decode).
- A screenshot taken while a real detection is active, showing the composited
  censor region.
- Two physical monitors (only one is attached to the reference machine).

Not claimed: detection accuracy/mAP, 24-hour stability, installer, code signing,
audio/video H.264 export, tray integration, or multi-monitor physical validation.

---

## 2. Environment

The reference machine uses a private .NET installation. Every shell must run:

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
$env:DOTNET_CLI_UI_LANGUAGE = "en"
dotnet --version        # expected 8.0.426 on the reference machine
```

Other prerequisites:

- Windows 10 build 19041+ or Windows 11, x64.
- The four pinned ONNX files in `models/` (see `models/manifest.json`).
- An unlocked interactive desktop for capture/overlay tests.
- No ffmpeg is required or used.

If the model files are missing:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/fetch-models.ps1
```

The script downloads only the pinned files and verifies SHA-256. Runtime code
never downloads anything.

---

## 3. Build, test, run

```powershell
# Fast verification
dotnet test tests\VeilDesk.Core.Tests\VeilDesk.Core.Tests.csproj --nologo

# Debug UI build
dotnet build src\VeilDesk.App\VeilDesk.App.csproj -c Debug --nologo

# Run (apphost needs DOTNET_ROOT in its environment)
& src\VeilDesk.App\bin\Debug\net8.0-windows10.0.19041.0\VeilDesk.App.exe
```

Expected test result: **23 passed, 0 failed, 0 skipped**.

Do not rebuild while `VeilDesk.App.exe` is running: Windows locks the apphost and
DLLs in `bin\`. Stop the process first:

```powershell
Get-Process VeilDesk.App -ErrorAction SilentlyContinue | Stop-Process -Force
```

Packaging (self-contained win-x64 folder + ZIP + checksums):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
```

The script verifies every model hash and refuses to publish on a mismatch.
Outputs are under `artifacts/`.

---

## 4. Live verification checklist (requires unlock)

The Windows session was locked during the last attempt. WGC returns black frames
on the secure desktop, so every step below is **blocked** until the user unlocks
the machine and dismisses the lock screen.

### 4.1 Prove capture is live before testing detection

```powershell
dotnet run --project tools\CaptureProbe\CaptureProbe.csproj
```

Require: a non-black percentage near 100% and a plausible frame rate. If the
probe is black, stop; do not interpret zero detections as a model failure.

### 4.2 Overlay placement and moving-target tracking

1. Start VeilDesk. Confirm the intended display is checked under **Displays**.
   Leave fail-closed enabled.
2. Open a controlled test image (for example the local *David* test image or a
   plain test pattern) in a normal window. Do not use a DRM/protected player.
3. Press **Start protection** and confirm the dashboard reaches **Protecting**
   with a CPU provider.
4. Move the image window slowly across the display. The censor region should
   follow with short motion padding and stable track IDs.
5. Photograph the physical screen with a phone. **Do not** use VeilDesk's own
   screenshot as proof of overlay placement: the overlay is deliberately
   excluded from WGC, and a screenshot composites the renderer over the original
   frame instead of capturing the live overlay.
6. Repeat with two subjects/windows to check multi-subject association.

Acceptance evidence: a phone photo or external recording showing the physical
overlay over the expected region, plus the dashboard/session counters.

### 4.3 Screenshot export

With a detection active, use **Export → Save censored screenshot**. Confirm:

- the PNG dimensions match the selected monitor;
- the censor region is composited at the same coordinates as the live overlay;
- the image is not a screen grab of the overlay window (no UI chrome from the
  overlay).

Reference output from the earlier run:

```
C:\Users\wt201\OneDrive\Pictures\VeilDesk\VeilDesk-20261022-101512-Generic-PnP-Monitor.png
```

`MyPictures` is redirected to OneDrive on the reference machine. That earlier
file proves WGC capture + PNG encoding, but visual inspection found no censor
region. The compositor itself passes a deterministic synthetic-region probe
(`tools/ExportProbe`); a screenshot containing a validated live detection is
still **NOT RUN**.

### 4.4 In-app video recording

1. Start protection, then start recording from **Export**.
2. Keep a moving subject visible for at least 10–20 seconds.
3. Stop recording before stopping protection (the app also orders shutdown this
   way).
4. Inspect the file:

```powershell
dotnet build tools\AviProbe\AviProbe.csproj
& tools\AviProbe\bin\Debug\net8.0-windows10.0.19041.0\AviProbe.exe --inspect `
  "C:\path\to\recording.avi"
```

Expect `frames > 0`, `idx1=yes`, `playback: MEDIA_OPENED`, and a non-black poster.
Record the measured frame count, file size, dropped frames and effective duration
in `docs/TEST_RESULTS.md` and `docs/BENCHMARKS.md`. Do not claim H.264 or audio.

### 4.5 Multi-monitor (only if hardware is available)

- Attach a second display with a different DPI and relative position.
- Confirm one overlay per monitor, including negative virtual-desktop
  coordinates.
- Use a known test pattern and photograph both screens; do not rely on WGC
  screenshots for overlay placement.
- If hardware is unavailable, leave this marked **NOT VERIFIED**.

### 4.6 Packaged build (already executed on the build host)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
& artifacts\VeilDesk-win-x64\VeilDesk.App.exe
```

Measured on 2026-10-22: publish folder 238.2 MB / 494 files, ZIP 110.5 MB.
The final ZIP SHA-256 is written to `PACKAGE-SHA256.txt` at the repository root
(after zipping, so it is not part of the archive).
The published app started on the build host and installed all four built-ins
into a throw-away `VEILDESK_HOME`. Still pending: a clean VM/machine install.
Re-run packaging after any source or documentation change; the ZIP hash is
printed by the script.

```powershell
$env:VEILDESK_HOME = Join-Path $env:TEMP "veildesk-clean"
& artifacts\VeilDesk-win-x64\VeilDesk.App.exe
```

---

## 5. Pitfalls and safety rules

- **Never set `VEILDESK_DISABLE_CAPTURE_EXCLUSION=1` while protection is
  running.** With fail-closed on, the engine can capture its own cover and feed
  it back. The variable is Debug-only and is intended for static UI inspection
  with protection stopped. Release builds always exclude overlays.
- **Locked desktop = black frames.** This is expected WGC behaviour, not a
  detector regression.
- **Screenshots do not prove live overlay placement.** The overlay is excluded
  from capture; use an external camera/phone.
- **Do not test with protected/DRM content.** It may be black to WGC, and
  content you do not have rights to use should not be used as test material.
- **Start/Stop lifecycle matters.** Scan-rate and provider changes apply on the
  next Start. Style/threshold changes apply live.
- **One physical display on the reference machine.** Per-monitor DPI code is
  unit-tested, but mixed-DPI hardware behaviour is unverified.
- **DirectML is opt-in.** Cold start on the reference Iris Plus was 46.6 s; CPU
  is the default for a reason.
- **The recorder is capped** at 1280×800, 15 fps, 9,000 frames / 10 minutes,
  1.9 GB, MJPEG AVI, no audio.
- **Built-in packs install once.** A marker file
  `packs\.builtin-packs-installed` is written after the first copy. Deleting an
  individual pack does not make it reappear; delete the marker to force a retry.
- **App startup installs packs silently.** A broken built-in pack must never
  block startup; validation errors are swallowed by design.
- **Export directory** defaults to the user's Pictures folder under
  `VeilDesk\`; `MyPictures` is OneDrive-redirected on this machine.
- **150% DPI** on the reference display. UI coordinates are DIPs; capture and
  censor regions are physical pixels.

---

## 6. Data and diagnostics

Default data root: `%LOCALAPPDATA%\VeilDesk`

| Path | Contents |
|---|---|
| `settings.json` | User settings (plain JSON, atomic write, `.bak` backup) |
| `sessions.jsonl` | Local session history |
| `packs/` | Installed `.veilpack` files, extracted assets, install marker |
| `exports/` | Default export location when not overridden |

Overrides:

- `VEILDESK_HOME` environment variable.
- A `portable.flag` file beside the executable → `data/` beside the app.
- `AppPaths.OverrideRoot` for tests.

Diagnostics are available under **About & diagnostics**; there is no telemetry or
automatic upload. To reset a corrupt state, stop the app, rename
`settings.json` (the loader also quarantines corrupt files), and restart.

Useful probes:

| Tool | Purpose |
|---|---|
| `tools/WgcProbe` | Minimal WGC/D3D11 capture check |
| `tools/CaptureProbe` | Frame readback, non-black percentage, BMP/PNG |
| `tools/VisionProbe` | Real ONNX inference on an image (`--cpu`) |
| `tools/AviProbe` | Write/play an MJPEG AVI; `--inspect` validates an existing file |
| `tools/ExportProbe` | Deterministic screenshot-compositor pixel check (no live detection needed) |

---

## 7. Source map

| Area | Location |
|---|---|
| Capture / D3D11 / WGC | `src/VeilDesk.App/Capture/` |
| Overlay windows and styles | `src/VeilDesk.App/Overlay/` |
| Engine orchestration | `src/VeilDesk.App/VeilDeskEngine.cs` |
| UI and settings wiring | `src/VeilDesk.App/MainWindow.xaml(.cs)` |
| Screenshot / video export | `src/VeilDesk.App/Export/` |
| ONNX sessions and preprocessing | `src/VeilDesk.Inference/` |
| Geometry, tracker, composer, packs, AVI | `src/VeilDesk.Core/` |
| Tests | `tests/VeilDesk.Core.Tests/CoreTests.cs` |
| Packaging | `scripts/package.ps1` |
| Pinned model provenance | `models/manifest.json`, `models/README.md` |

---

## 8. Definition of done for the remaining slice

The project can be called complete for this vertical slice when all of the
following have been executed and recorded:

1. An unlocked-desktop phone/external-camera photo showing the live overlay over
   a real detected region, with coordinates/dashboard context.
2. A moving-target sequence showing the region following the subject.
3. A screenshot export taken during a real detection, verified against the
   source frame.
4. A 10–20 s in-app recording, `AviProbe --inspect` passing, and measured
   throughput/dropped frames.
5. Re-run the final clean `dotnet test` and Release package after the last code
   change, with package size recorded in `docs/BENCHMARKS.md` and
   `docs/TEST_RESULTS.md` (done for the 2026-10-22 artifact; re-run if code
   changes).
6. All changes committed; `docs/CURRENT.md` updated to match the evidence.

If the session locks again, stop rather than substituting a static image or a
synthetic result for a live test. Mark the item **BLOCKED** and preserve the
previous evidence.
