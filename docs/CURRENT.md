# Current state

**Snapshot:** 2026-10-22 (Asia/Seoul)
**Project root:** `C:\Users\wt201\OneDrive\Desktop\blocker`
**Version:** 1.0.0
**Platform:** Windows 10/11 x64, .NET 8, WPF

This file is the short operational snapshot. Deeper records live in
`TEST_RESULTS.md`, `BENCHMARKS.md`, `ARCHITECTURE.md` and `HANDOFF.md`.

## Objective

Deliver a genuinely working vertical slice of a privacy-first, fully local
real-time censorship application: real capture, real local CV inference,
multi-subject tracking, correctly positioned censorship overlays, a usable
settings UI, tests, measured performance, and reproducible packaging.

## Implemented

### Core (`VeilDesk.Core`)

- Pixel geometry, monitor descriptors, detection records and body regions.
- NudeNet and YOLO output decoders, NMS, letterbox transforms.
- Segmentation mask decoding, thresholding and largest-component cleanup.
- Scene-change detector and adaptive inference scheduler.
- Latency tracker (P50/P95).
- Subject tracker with velocity prediction, person evidence association,
  track persistence, masks and whole-person regions.
- Censor composer: enabled regions, confidence, gender/whole-body rules,
  fail-closed, motion padding, prediction horizon.
- Settings model, clamping, atomic save with backup and corrupt-file
  quarantine.
- Session history, statistics, streaks and achievements.
- `.veilpack` validator with size/entry caps, extension allowlist and
  path-traversal rejection.
- Pooled `VideoFrame` and frame mailbox.
- Self-contained MJPEG AVI writer with RIFF headers, `idx1` index and bounded
  streaming.

### Inference (`VeilDesk.Inference`)

- Three ONNX Runtime sessions: NudeNet 320n, YOLOv8n 320/480, selfie
  segmentation.
- DirectML-first with CPU fallback; CPU is the default because DirectML cold
  start is unacceptable on the tested iGPU.
- Pure C# bilinear BGRA→RGB NCHW preprocessing (no OpenCV).
- Per-person segmentation crops, whole-person silhouettes, serialized runs.
- Per-monitor pipeline with separate person/anatomical cadences, adaptive
  scene skip, fail-closed supervision, region generation and timing.

### App (`VeilDesk.App`)

- Windows Graphics Capture with a dedicated STA dispatcher thread, persistent
  D3D11 device, per-monitor sessions and 15 fps readback.
- Transparent, click-through, topmost WPF overlays per monitor; physical
  pixels converted to DIPs using the monitor DPI; ten censor styles.
- Overlay windows excluded from WGC (`WDA_EXCLUDEFROMCAPTURE`).
- `VeilDeskEngine` orchestration: Start/Stop, shared model set, per-monitor
  runners, adaptive analysis, 30 Hz overlay refresh, fail-closed, sessions,
  screenshot API and settings updates.
- Ten-page dark UI: Dashboard, Detection, Censoring, Displays, Performance,
  Export, Packs, Sessions, Settings, About.
- Screenshot export compositing the same censor renderer; optional scaling.
- Video recorder loop and MJPEG AVI writer; capped at 1280×800, 15 fps.
- Built-in pack installation on first launch with an install marker; pack apply
  preserves unrelated settings sections.
- Start-minimized support; close-to-tray is intentionally disabled.

### Tooling / repository

- `tools/WgcProbe`, `tools/CaptureProbe`, `tools/VisionProbe`,
  `tools/AviProbe`.
- `scripts/fetch-models.ps1` with pinned SHA-256 checks.
- `scripts/package.ps1`: self-contained x64 publish, model/hash verification,
  docs/packs/models copy, `SHA256SUMS.txt`, ZIP.
- Models manifest, third-party notices, AGPL licence.
- 23 Core tests, including AVI container parsing and built-in pack validation.

## Verified evidence

- **Capture:** real 2560×1600 BGRA frames, 13–19 fps, 100% non-black on an
  unlocked desktop; start 360–636 ms, stop ~1 s.
- **Inference:** CPU model load ~281 ms; steady full pass ~54–67 ms; three
  correct person boxes on `bus.jpg`; NudeNet `FEMALE_BREAST_EXPOSED` on the
  David test image, visually aligned.
- **Live engine:** CPU provider, ~12–14 capture fps, analysis P50 ~28–43 ms;
  overlay running and excluded from capture; Start/Stop worked.
- **Live session log:** 97.1 s, 103 detection events, peak 11 tracks.
- **Screenshot export path:** real WGC frame written to
  `C:\Users\wt201\OneDrive\Pictures\VeilDesk\VeilDesk-20261022-101512-Generic-PnP-Monitor.png`.
  Visual inspection showed no censor region, so this file proves capture +
  PNG encoding, not compositing during a live detection.
- **Screenshot censor compositing:** `tools/ExportProbe` passed with a
  synthetic 640×360 frame and a known region: center pixel became `#0B0D12`,
  11.46% of pixels changed, and outside pixels were unchanged.
- **Video container:** 30 real JPEG frames → 884,818-byte AVI; Windows Media
  Foundation opened it as 640×360, 2.000 s, with `idx1=yes`, and decoded a
  96.4% non-black poster.
- **Tests:** 23/23 passed.
- **UI:** all ten pages rendered at 150% DPI via `PrintWindow`.
- **Package:** self-contained Release publish 238.2 MB / 494 files; ZIP 110.5 MB;
  final SHA-256 in `PACKAGE-SHA256.txt`; published app launched with a
  throw-away data root and installed all four built-in packs.

## Active / incomplete

- Live visual confirmation of censor overlay placement and tracking on an
  unlocked desktop, including a moving target.
- End-to-end `VideoRecorder` run from the app (start/stop, frame count, file
  playback, measured recording throughput).
- Multi-monitor validation with two physical displays (hardware not available).
- Clean-machine / VM install validation (only the build host has been used).
- Installer and code signing (portable ZIP only).

## Blocked

The Windows session is locked. A fresh `CaptureProbe` run on 2026-10-22
returned a 2560×1600 frame with `nonBlack=0.0%`, showing only the secure-desktop
spinner. Live anatomical detection, overlay visuals, recording and
screenshot-with-detection cannot be validated until the user unlocks.
Do **not** set `VEILDESK_DISABLE_CAPTURE_EXCLUSION=1` for live tests: with
fail-closed enabled, the engine captures its own cover and feeds it back.

## Next actions

1. Unlock the session; start protection with overlay exclusion enabled.
2. Show a controlled moving test subject; verify track IDs, motion padding and
   overlay alignment on the physical display; capture evidence.
3. Run the in-app recorder for ~20 s; verify AVI header/frame count and decode
   with `AviProbe`/Media Foundation; record measured throughput.
4. Test with a second monitor if available; verify per-monitor DPI and negative
   virtual-desktop coordinates.
5. Optionally validate the ZIP on a clean VM; no installer/signing is provided.
7. Commit the final docs and tag/record the artifact hash.
