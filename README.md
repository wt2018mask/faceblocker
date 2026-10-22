# VeilDesk

> 2026-10-22 복구 업데이트: 보호 회귀 39개, Release 빌드와 창 없는 실제 ONNX→검열 픽셀 합성 검증 PASS.
> 수정 후 실제 화면 깜박임·비상키·Start/Stop·녹화는 대화형 검증 대기다. **production-ready 아님.**
> 최신 상태: [복구 기록](docs/RECOVERY-20261022.md), [현재 상태](docs/CURRENT.md).
> 안전한 비대화형 검증: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Headless.ps1`.
> 대화형 스크립트는 `-Interactive`와 명시적 이미지/앱 경로가 필요하다. 기본 이미지나 자동 보호 재시작은 없다.
> 비상 중지: `Ctrl+Shift+F12`(실제 키 입력은 검증 대기). 기존 배포 폴더는 보존하며 패키징에는 새로운 `-Output` 경로를 사용한다.

**A local, privacy-first real-time censorship overlay for Windows 10/11 x64.**

VeilDesk captures selected displays with Windows Graphics Capture, runs three
ONNX models on the local machine, tracks subjects, and draws censorship over
the detected regions in transparent click-through overlays. No frame, crop,
detection or session record leaves the machine. There is no account, cloud
inference, subscription or telemetry.

This is an independent implementation inspired by publicly described features
of Isla2D Beta Blocker 4.0. It contains no proprietary source, assets, logos,
names or licence mechanisms. See `docs/REFERENCE_EVIDENCE.md`.

---

## Status at a glance

Verified on 2026-10-22 on an Intel i7-1065G7 / Iris Plus laptop running Windows
11 build 26200, 2560×1600 at 150% DPI.

| Gate | Status | Evidence |
|---|---|---|
| Real screen capture (WGC + D3D11) | **VERIFIED** | `CaptureProbe` / `WgcProbe`; 2560×1600 BGRA, 100% non-black frames |
| Real local ONNX inference | **VERIFIED** | `VisionProbe`; 3 person boxes on `bus.jpg`, NudeNet anatomical class on a test image |
| Real anatomical detection | **VERIFIED (static image)** | NudeNet `FEMALE_BREAST_EXPOSED` on Michelangelo's *David*; live session log recorded 103 detection events |
| Multi-subject tracking | **VERIFIED (unit + observed)** | tracker tests; one live session peaked at 11 tracks |
| Start / Stop and fail-closed | **VERIFIED** | live UI run; CPU provider, ~12–14 capture fps, overlay excluded from capture |
| Correct overlay placement | **PARTIAL** | geometry/DPI tests and renderer implemented; live visual check blocked by a locked Windows session |
| Screenshot export (PNG path) | **VERIFIED** | app wrote a real 2560×1600 WGC frame to `Pictures\VeilDesk` |
| Screenshot censor compositing | **VERIFIED (synthetic)** | `tools/ExportProbe`: known region rendered as `#0B0D12`, 11.46% of pixels changed, outside pixels unchanged |
| Screenshot with a live detection | **NOT YET RUN** | the exported frame contained no active detection; needs an unlocked desktop |
| MJPEG AVI writer / playback | **VERIFIED** | 30 real JPEG frames; Windows Media Foundation opened and decoded 640×360, 96.4% non-black |
| End-to-end video recording in the app | **NOT YET RUN** | recorder loop requires an unlocked live capture session |
| Offline operation | **VERIFIED** | no network client in app code; models loaded from disk only |
| Tests | **VERIFIED** | 23/23 passed (`dotnet test`) |
| Reproducible package | **VERIFIED (build host)** | self-contained win-x64 publish (238.2 MB folder), 110.5 MB ZIP; launched with a throw-away data root and installed all four packs. Final hash: `PACKAGE-SHA256.txt` (outside the ZIP) |
| Multi-monitor with two physical displays | **NOT VERIFIED** | one display available on the test machine |

The detailed record is in `docs/TEST_RESULTS.md`; measurements and methodology
are in `docs/BENCHMARKS.md`. Nothing in this README claims an accuracy or
performance number that was not measured.

---

## What it does

- Captures every selected monitor with Windows Graphics Capture and Direct3D 11.
- Runs **NudeNet 320n** for 18 anatomical classes and **YOLOv8n 320/480** for
  person detection. Optional **MediaPipe Selfie Segmentation** produces
  whole-person masks.
- Associates body-part detections with person boxes, tracks multiple subjects,
  predicts short motion, and keeps a track alive briefly through occlusion.
- Draws one transparent, click-through, topmost overlay per monitor, using
  physical-pixel coordinates converted to WPF device-independent units.
- Supports ten censorship styles: solid, blur, pixelate, image, text, outline,
  silhouette, marker, night vision and frosted glass.
- **Fail-closed:** if no successful analysis arrives within the configured
  interval, the entire display is covered rather than exposing unprocessed
  content.
- Exports censored PNG screenshots and screen-recorded censored MJPEG AVI files
  locally.
- Imports inert `.veilpack` style packs with bounded ZIP extraction and safe
  path validation. Four built-in packs are installed on first launch.
- Stores settings, session history and achievements under
  `%LOCALAPPDATA%\VeilDesk` (or a portable/overridden data root).

## What it deliberately does not do

- No cloud inference, account, subscription, telemetry, crash upload or update
  check.
- No proprietary Beta Blocker code, model, icon, logo or UI asset.
- No hidden screen transmission. The process has no HTTP client.
- No fake "AI accuracy" claims. Model limitations are documented.

---

## Requirements

- Windows 10 version 2004 (build 19041) or newer, or Windows 11. x64.
- .NET 8 SDK to build (`8.0.100` or newer 8.0 feature band).
- A CPU that can run the three ONNX models; an integrated GPU is sufficient.
- About 400 MB free disk for a self-contained build and models.
- The model files described below. They are not committed to Git.

Windows Graphics Capture may not capture protected DRM surfaces or exclusive
fullscreen games; those cases are documented in `docs/KNOWN_LIMITATIONS.md`.

---

## Quick start (development)

```powershell
# 1. Fetch and verify the pinned model weights (requires network once)
powershell -ExecutionPolicy Bypass -File scripts/fetch-models.ps1

# 2. Build
dotnet build VeilDesk.sln -c Debug

# 3. Run
dotnet run --project src/VeilDesk.App/VeilDesk.App.csproj
```

If you use a private .NET installation, set `DOTNET_ROOT` and prepend its folder
to `PATH` before running the apphost.

### First use

1. Open **Displays** and confirm the monitor selection.
2. Open **Detection** and choose anatomical classes or whole-person mode.
3. Open **Censoring** and choose a style. Fail-closed is on by default.
4. Press **Start protection**. The overlay is excluded from capture, so the
   exported image uses the original frame plus the same local renderer.
5. Use **Export** for a censored screenshot or a local recording.
6. Use **Packs** to apply a built-in style or import a `.veilpack` ZIP.

Changing scan rates or the execution provider takes effect on the next Start.
Style, thresholds, prediction and fail-closed settings apply live.

---

## Models and licences

| Runtime file | Purpose | Licence | SHA-256 |
|---|---|---|---|
| `nudenet-320n.onnx` | 18 anatomical classes | AGPL-3.0 | `C15D8273ADAD2D0A92F014CC69AB2D6C311A06777A55545F2C4EB46F51911F0F` |
| `yolov8n-320.onnx` | person detection | AGPL-3.0 | `995548C2D6DB15CC9418D037BA8EBABD46E029201B9C35E49CB92D8C825A8004` |
| `yolov8n-480.onnx` | person detection, high-res option | AGPL-3.0 | `D030CCDE37BC8B757299733DE24C3AAF74A1FBDA00277FF3EB48533B25E4C9D3` |
| `selfie_segmentation.onnx` | whole-person silhouette | Apache-2.0 | `3241AC4AD8AA35BDAF33946776DB29F7C283A413AA0B0DACB9483594B4531AAD` |

The frozen source weights, preprocessing contracts and provenance are in
`models/manifest.json` and `models/README.md`. The application never downloads
anything at runtime; `scripts/fetch-models.ps1` is an install-time/developer
tool that verifies every hash.

Because NudeNet and Ultralytics YOLO weights are AGPL-3.0, VeilDesk is
distributed under **AGPL-3.0**. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
Redistributing a build requires making the corresponding source available.

---

## Build and packaging

```powershell
# Tests
dotnet test tests/VeilDesk.Core.Tests/VeilDesk.Core.Tests.csproj

# Self-contained Windows x64 publish + ZIP + SHA256SUMS.txt
powershell -ExecutionPolicy Bypass -File scripts/package.ps1

# Publish folder only
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -SkipZip
```

Output: `artifacts/VeilDesk-win-x64/` and
`artifacts/VeilDesk-1.0.0-win-x64.zip`. The measured build was 238.2 MB
uncompressed and 110.5 MB zipped. The final SHA-256 is written to
`PACKAGE-SHA256.txt` at the repository root (deliberately outside the ZIP, so
the checksum is not embedded in the file it describes). The package contains the
self-contained runtime, `models/`, `builtin-packs/`,
docs, licence and a build info file. The script refuses to package if a model
hash differs from `models/manifest.json`. The published executable was launched
on the build host with a temporary `VEILDESK_HOME`; it started and installed all
four built-in packs. Clean-machine validation, an installer and code signing are
still pending.

---

## Project layout

```
src/
  VeilDesk.Core/        pure .NET library: geometry, decoders, tracker,
                        composer, settings, sessions, packs, AVI writer
  VeilDesk.Inference/    ONNX Runtime sessions, preprocessing, vision pipeline
  VeilDesk.App/          WPF UI, WGC capture, overlays, export, engine
tests/
  VeilDesk.Core.Tests/   unit and container tests (23)
tools/
  WgcProbe/              real WGC capture probe
  CaptureProbe/          WGC readback/BMP/PNG probe
  VisionProbe/           real ONNX inference probe
  AviProbe/              real JPEG AVI + Media Foundation playback probe
builtin-packs/           validated inert style packs shipped with the app
models/                  hashes/provenance; weights fetched separately
docs/                    architecture, evidence, benchmarks, handoff, limits
scripts/                 model fetch and packaging scripts
```

## Privacy model

- Capture buffers and inference tensors exist only in process memory.
- The only files written are settings, local session history, screenshots and
  recordings explicitly requested by the user, plus local pack extraction.
- No network API is referenced by the application projects.
- Overlay windows are excluded from Windows Graphics Capture with
  `WDA_EXCLUDEFROMCAPTURE`; screenshots composite the same renderer over the
  original captured frame.
- The Debug-only environment variable
  `VEILDESK_DISABLE_CAPTURE_EXCLUSION=1` exists for UI inspection. It causes a
  fail-closed feedback loop when protection is running and must not be used for
  live detection tests. Release builds always exclude overlays.

## Current limitations

Live visual verification of the overlay was interrupted when the Windows
session locked; WGC returns black frames on the secure desktop. The screenshot
path, AVI writer, models, capture and UI are verified independently. See
`docs/KNOWN_LIMITATIONS.md` for the complete list, including model bias,
exclusive-fullscreen capture, DRM surfaces, MJPEG AVI limits, the missing
installer/signing, and untested multi-monitor configurations.

## Documentation

- `docs/CURRENT.md` — current state and next actions
- `docs/HANDOFF.md` — how to resume verification and development
- `docs/ARCHITECTURE.md` — pipeline and threading design
- `docs/REFERENCE_EVIDENCE.md` — public references and provenance
- `docs/BENCHMARKS.md` — measured numbers and methodology
- `docs/TEST_RESULTS.md` — executed tests and observed results
- `docs/KNOWN_LIMITATIONS.md` — honest limits
