# Architecture

VeilDesk is a .NET 8 WPF desktop application with a testable, UI-free core and a
separate ONNX Runtime inference library. All processing is local. The design
favours explicit lifetimes, bounded buffers and fail-closed behaviour over
clever shortcuts.

```
                    ┌──────────────────────────────────────────────┐
                    │                 VeilDesk.App                 │
                    │  MainWindow (10 pages)   VeilDeskEngine      │
                    │        │                    │                │
                    │        │ settings/status    │                │
                    │        ▼                    ▼                │
                    │  OverlayService ◄──── CensorRegions          │
                    │  OverlayWindow(s)          ▲                 │
                    │                            │                 │
                    │  ExportService      MonitorRunner(s)         │
                    │  VideoRecorder        │       │              │
                    │        ▲              │       │              │
                    │        │        CaptureService  VisionPipeline│
                    └────────┼──────────────┼───────┼──────────────┘
                             │              │       │
                       MJPEG AVI       WGC + D3D11  ONNX Runtime
                                                    │
                                      NudeNet / YOLOv8 / Selfie Seg
```

## Projects

| Project | Target | Responsibility |
|---|---|---|
| `VeilDesk.Core` | `net8.0` | Pure algorithms: geometry, decoders, NMS, segmentation cleanup, tracker, composer, settings, sessions, packs, frame pooling, AVI writer |
| `VeilDesk.Inference` | `net8.0-windows` | ONNX Runtime sessions, preprocessing, per-monitor vision pipeline |
| `VeilDesk.App` | `net8.0-windows10.0.19041.0`, WPF, x64 | Capture, overlays, engine orchestration, UI, export, packaging surface |
| `VeilDesk.Core.Tests` | `net8.0` | xUnit tests; no GPU or display required |

No project references a network API. The only `Uri` use in app code is loading
local images from disk.

## Capture

`CaptureService` owns a dedicated STA thread with a `Dispatcher` and a persistent
Direct3D 11 device. For each selected `MonitorDescriptor` it creates:

- a `GraphicsCaptureItem` from the monitor's `HMONITOR`,
- a `Direct3D11CaptureFramePool` (BGRA8, two buffers),
- a capture session at the requested frame rate (default 15 fps).

Each frame is copied from the GPU texture into a reusable staging texture,
mapped, and copied into a pooled `VideoFrame`. The latest frame is retained for
screenshots/recordings; a `FrameMailbox` hands the newest frame to analysis
without building a queue. If analysis is slow, old frames are dropped rather
than accumulating latency. A frame is borrowed from a small pool and returned
on dispose; the mailbox replaces and disposes the previous frame safely.

Coordinate systems:

- `MonitorDescriptor` stores the physical virtual-desktop rectangle
  (`X`, `Y`, `Width`, `Height`), DPI scale, and a local bounds rectangle
  `(0, 0, Width, Height)`.
- Detection and censor regions are in physical pixels local to the monitor.
- WPF overlay canvases are sized in device-independent units and positioned
  with physical virtual-desktop coordinates via `SetWindowPos`.

Resolution changes require Stop/Start because capture items are rebuilt.

## Inference

`VisionModels` loads three ONNX sessions once and shares them across monitors.
Runs are serialized by a gate to avoid oversubscribing the CPU/iGPU. The default
provider is CPU; DirectML is optional. The loader catches DirectML
initialisation failure and falls back to CPU, exposing the provider and error
in the UI.

Models and preprocessing:

| Model | Input | Preprocessing | Output use |
|---|---|---|---|
| NudeNet 320n | `[1,3,320,320]` RGB 0..1 | right/bottom square padding | 18 class scores; best class per anchor |
| YOLOv8n 320/480 | `[1,3,S,S]` RGB 0..1 | centered letterbox, fill 114 | person class only |
| Selfie segmentation | `[1,3,256,256]` RGB 0..1 | direct bilinear resize | `alphas` sigmoid probabilities |

`FramePreprocessor` performs bilinear BGRA→RGB conversion and NCHW packing in
pure C#. There is no OpenCV dependency at runtime.

Pipeline per monitor:

1. Wait for a frame from the mailbox.
2. Adaptive scheduler decides whether to run: person detection at
   `ScanRateHz`, anatomy at `BodyScanRateHz`, with a forced safety scan every
   750 ms even on a static scene.
3. Decode YOLO person boxes; decode NudeNet body parts; run NMS.
4. Associate body parts with person boxes, update the tracker, and optionally
   run selfie segmentation on crops for whole-person masks/silhouettes.
5. Compose `CensorRegion`s from confirmed tracks, enabled classes, confidence,
   gender/whole-body rules, motion padding and prediction.
6. Publish regions and timing; the overlay timer reads the latest prediction.

The person and anatomical models run at separate cadences. A scene-change
detector skips redundant work only when allowed, but the safety scan prevents a
permanently static scene from suppressing fail-closed supervision.

## Tracking and composition

`SubjectTracker` keeps tracks with a stable ID, last box, velocity, person
evidence and optional mask. On each update it:

- associates new detections with existing tracks using IoU/distance,
- updates velocity and predicts a short horizon,
- expands boxes by motion padding,
- keeps a track alive for `TrackPersistenceMs` after the last observation,
- expires tracks beyond persistence.

`CensorComposer` turns tracks into regions:

- anatomical regions only if the class is enabled and confidence passes,
- whole-person regions only if enabled and (when configured) nudity evidence,
  gender filters and person confidence pass,
- fail-closed region covering the monitor when analysis is stale, the engine is
  unsure, or capture is unavailable,
- prediction and mask expansion applied consistently.

The tracker and composer live in Core and are covered by unit tests. They do not
depend on WPF or ONNX Runtime.

## Overlay

`OverlayService` creates one `OverlayWindow` per captured monitor. Each window:

- is borderless, transparent, topmost, non-activating and click-through,
- uses a `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` extended
  style,
- is positioned in physical virtual-desktop coordinates with `SetWindowPos`,
- has a canvas in DIPs; `CensorVisual` converts physical boxes to DIPs using the
  monitor's DPI scale,
- applies `WDA_EXCLUDEFROMCAPTURE` after the HWND exists,
- reasserts topmost approximately once per second.

`CensorVisual` implements all ten styles with WPF primitives. Blur/pixelate/
night-vision/frosted styles crop the original captured frame; this is why the
snapshot is passed to the renderer. Silhouette uses a cached mask bitmap. The
fail-closed region always renders as an opaque full-display cover.

Overlay updates are coalesced on the UI dispatcher and flushed on a ~16 ms
timer. Prediction between inferences keeps boxes moving smoothly without running
the models at display refresh rate.

Excluding the overlay from capture is essential: otherwise the next WGC frame
would contain the previous censorship, and the renderer would censor its own
output. The Debug-only opt-out is for static UI inspection, not live tests.

## Export

- Screenshots use the latest captured frame and the current regions. The same
  `CensorVisual` renderer is applied to a `RenderTargetBitmap`; the result is
  encoded as PNG off the UI thread. Optional `maxWidth`/`maxHeight` scales the
  canvas before rendering.
- Video uses `VideoRecorder`: it samples the newest frame at 15 fps, composites
  with the same renderer on the UI dispatcher, encodes JPEG (quality 82) and
  writes a Motion JPEG AVI. The writer streams to disk and patches RIFF/AVI
  headers and an `idx1` index at stop. Output is capped at 1280×800, 9000
  frames, 10 minutes and 1.9 GB. There is no audio and no ffmpeg.
- The recorder stops before the engine during shutdown to avoid reading frames
  from a disposed capture service.

## Settings, packs, sessions

- `SettingsStore` serializes JSON with enum strings, validates/clamps all
  ranges, writes via a temporary file and `File.Replace`, keeps `.bak`, and
  quarantines corrupt input instead of failing startup.
- `AppSettings` covers detection, censoring, performance, capture, creator,
  sessions and UI. Live-updatable settings are cloned into the engine;
  provider/scan-rate/capture changes require Stop/Start.
- Packs are ZIP archives with `manifest.json`; `PackValidator` caps entries and
  uncompressed size, rejects absolute/`..` paths, allows only inert extensions,
  validates IDs/colours, then extracts. Applying a pack changes only the
  sections present in `settingsPreset` (censoring and/or detection), preserving
  capture/security settings. Built-in packs are validated and copied once into
  the user pack directory.
- Session history is append-only JSONL with duration, detection count, peak
  tracks, style and monitor count. Achievements are derived locally.

## Threading and lifetime

- UI thread: WPF, settings, overlay composition, screenshot/video compositing.
- Capture thread: STA dispatcher, D3D11 and WGC callbacks.
- Analysis: one `Task` per monitor; shared ONNX sessions serialized.
- Video: background loop; compositing marshalled to the UI dispatcher.
- Stop: cancel analysis, stop capture, dispose sessions/overlays/frames,
  finalise recording, append session record.
- Startup requires a model directory containing all three ONNX files; failure
  is surfaced in the UI and prevents Start.

The overlay is deliberately fail-closed and best-effort: it cannot protect
against a compromised OS, a debugger, DRM-protected surfaces that WGC cannot
see, or a user disabling the app. It is a local privacy aid, not a security
boundary.

## Why no OpenCV / cloud / ffmpeg

- OpenCV would add a large native dependency for simple resize/decode/NMS work
  that is clearer and testable in C#.
- Cloud inference would violate the core privacy guarantee and add mandatory
  network/account dependencies.
- ffmpeg would add a large external binary; MJPEG AVI is enough for a local,
  self-contained recording path and is documented as such.
