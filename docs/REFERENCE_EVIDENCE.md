# Reference evidence and provenance

**Evidence date:** 2026-10-22
**Purpose:** separate public observations from independent implementation choices,
and record model provenance. This project did not inspect, decompile or copy a
Beta Blocker binary.

## Evidence labels

| Label | Meaning |
|---|---|
| **VERIFIED** | Reproduced in this repository or directly observed in a public source by the author on the stated date |
| **OBSERVED** | Seen in a public page/video/claim, but not independently reproduced |
| **INFERRED** | A design conclusion drawn from public behaviour or standard engineering practice |
| **UNKNOWN** | Not observable from public material; no claim is made |

## Public references

1. **Isla2D product site** — <https://isla2d.com/> (accessed 2026-10-22).
   - OBSERVED: Beta Blocker is described as real-time detection and censorship
     across apps, browsers, videos and streams; "Fully Customizable";
     "100% Local Processing"; Windows one-time purchase; 100,000+ downloads.
   - OBSERVED: links to Windows, Android, iOS and Mac editions.

2. **itch.io product page** — <https://isla2d.itch.io/beta-blocker>
   (accessed 2026-10-22).
   - OBSERVED: Windows 10+ 64-bit, 4 GB RAM; "Internet connection not required";
     capture/recording/export processed locally; real-time blocking across all
     monitors.
   - OBSERVED feature list: body-part selection, censor styles/effects/text,
     reverse censoring, popup storm, monitor effects, baked-in photo/video
     export, OBS/virtual camera, session stats and achievements, performance
     presets with GPU acceleration, optional Discord status (off by default),
     19 languages, Pack Creator.
   - OBSERVED download list includes **Beta Blocker 4.0**, 515 MB, plus older
     versions back to 1.0.
   - OBSERVED AI disclosure: "AI Assisted", "AI graphics".
   - This page was used for feature research only. No file was purchased,
     downloaded, executed or decompiled.

3. **Beta Blocker 4.0 patch notes** —
   <https://isla2d.itch.io/beta-blocker/devlog/1720827/beta-blocker-40-patch-notes>
   (accessed 2026-10-22).
   - OBSERVED: whole-body blocking that traces the person outline; female/male
     switches; nude-only vs nude-and-clothed; motion lead; confidence and
     outline margin; works with silhouette/night-vision/marker/blur/solid and
     other styles; reverse censor; own window stays clear.
   - OBSERVED vendor performance claims: 43 ms time-to-block on a busy screen,
     1.9 s app start, 6 AI scans/minute on a still screen, 99.6% coverage while
     scrolling, faster style drawing, approximately 2× faster export, smoother
     recordings, 12 new styles, 100+ looks.
   - OBSERVED other features: new sidebar UI, ranks/XP, escalation, wallpapers,
     ambient audio, screen effects, Creator Studio (GIF/MP4 export, recording,
     OBS setup, instant replay, watermark), Workshop/Packs 2.0, locks/partner
     control, 18 languages, digitally signed releases.
   - **These vendor numbers are not VeilDesk benchmarks.** They were not
     reproduced and must not be quoted as this project's results.

4. **YouTube video** — <https://www.youtube.com/watch?v=iPYXu01G3pI>
   (page title observed 2026-10-22: "Beta Blocker 4 0 Just Changed Everything").
   - OBSERVED: public demonstration video exists.
   - UNKNOWN: exact implementation details; frames were not copied into this
     project.

## What VeilDesk implemented independently

| Public idea | VeilDesk implementation | Status |
|---|---|---|
| Real-time local censorship across apps | WGC + D3D11 capture, local ONNX, transparent overlays | VERIFIED |
| Multiple monitors | Per-monitor capture, tracker, overlay, physical coordinates | Implemented; one physical display tested |
| Select body parts | NudeNet 18-class decoder + per-class enable/confidence | VERIFIED (static) |
| Censor styles | 10 styles implemented in WPF | Implemented; live visual check pending |
| Whole-body silhouette | YOLO person + MediaPipe selfie segmentation + masks | Implemented |
| Female/male and nude-only switches | `DetectionSettings` + composer rules | Unit-tested |
| Motion lead / prediction | Tracker velocity + `PredictionHorizonMs` / `MotionPadding` | Unit-tested |
| Confidence and outline margin | Clamped settings used by composer | Unit-tested |
| Photo/video export with baked-in censor | PNG compositor + MJPEG AVI recorder | PNG verified; AVI writer verified; in-app recorder pending |
| Packs | Validated inert `.veilpack` ZIPs + four built-ins | Unit-tested |
| Session stats/achievements | Local JSONL + derived achievements | Verified |
| Performance presets / GPU option | Scan-rate presets + DirectML/CPU provider | CPU verified; DirectML measured but not recommended |
| Fail-closed | Full-display cover when analysis is stale/unavailable | Unit-tested; live session observed |
| Reverse censor, popup storm, monitor effects, OBS virtual camera, Discord status, ranks/XP, escalation, wallpapers, 19 languages | Not implemented; intentionally out of scope for this vertical slice | Documented limitation |

The implementation does not use the Beta Blocker name as its product name, does
not copy its logo/icon/layout/assets, does not implement its licence or purchase
mechanism, and does not include any proprietary binary.

## Independent design decisions

- **C#/.NET 8 + WPF** instead of an unknown vendor stack.
- **Windows Graphics Capture** through direct COM/P/Invoke interop rather than a
  large third-party capture framework.
- **NudeNet + YOLOv8n + MediaPipe Selfie Segmentation** chosen for open licences
  and local ONNX execution.
- **CPU by default**, DirectML optional, because the measured DirectML cold
  start on the reference iGPU was unacceptable.
- **Pooled frames + newest-frame mailbox** to bound latency instead of a queue.
- **Per-monitor transparent WPF overlay** with `WDA_EXCLUDEFROMCAPTURE` rather
  than drawing into a captured mirror.
- **MJPEG AVI** as a self-contained recorder, avoiding an ffmpeg dependency.
- **Fail-closed** as the default safety posture.
- **Inert packs** with an extension allowlist and traversal rejection; no
  scripts or executables.

## Model provenance

### NudeNet 320n

- File: `nudenet-320n.onnx`
- Bytes: **12,150,158**
- SHA-256: `C15D8273ADAD2D0A92F014CC69AB2D6C311A06777A55545F2C4EB46F51911F0F`
- Source: `notAI-tech/NudeNet`, GitHub release asset `176831997`
  (`https://api.github.com/repos/notAI-tech/NudeNet/releases/assets/176831997`)
- Licence: **AGPL-3.0**
- Contract used: RGB float 0..1, 320×320, right/bottom square padding; output
  `[1,22,2100]` in 320×320 coordinates (cx, cy, w, h + 18 class scores).
- Verified by loading and running it locally, decoding 18 classes.

### Ultralytics YOLOv8n

- Source weights: official `ultralytics/assets` release `v8.1.0`, `yolov8n.pt`
  (`https://github.com/ultralytics/assets/releases/download/v8.1.0/yolov8n.pt`)
- Source bytes: **6,534,387**
- Source SHA-256: `31E20DDE3DEF09E2CF938C7BE6FE23D9150BBBE503982AF13345706515F2EF95`
- Exported locally with `tools/export_models.py`: opset 12, FP32, static shape,
  CPU, `imgsz=320` and `480`.
- Runtime files:
  - `yolov8n-320.onnx`: **12,698,057** bytes,
    `995548C2D6DB15CC9418D037BA8EBABD46E029201B9C35E49CB92D8C825A8004`
  - `yolov8n-480.onnx`: **12,750,561** bytes,
    `D030CCDE37BC8B757299733DE24C3AAF74A1FBDA00277FF3EB48533B25E4C9D3`
- Licence: **AGPL-3.0**
- Contract used: RGB float 0..1, centered letterbox fill 114; person class only;
  output `[1,84,2100]` / `[1,84,4725]`.

### MediaPipe Selfie Segmentation

- File: `selfie_segmentation.onnx`
- Bytes: **462,352**
- SHA-256: `3241AC4AD8AA35BDAF33946776DB29F7C283A413AA0B0DACB9483594B4531AAD`
- Source: `https://huggingface.co/onnx-community/mediapipe_selfie_segmentation/resolve/main/onnx/model.onnx`
- Licence: **Apache-2.0**
- Contract used: direct bilinear resize to 256×256, RGB float 0..1; output
  `alphas [1,1,256,256]`, already sigmoid probabilities.

The model files are not committed to Git. `scripts/fetch-models.ps1` downloads
the pinned files and verifies every SHA-256. `scripts/package.ps1` refuses to
package if a hash differs.

## Licence conclusion

NudeNet and Ultralytics YOLO are AGPL-3.0. VeilDesk therefore uses AGPL-3.0 and
must be distributed with corresponding source. MediaPipe segmentation is
Apache-2.0 and compatible. No proprietary weights are used. See `LICENSE`,
`THIRD-PARTY-NOTICES.md` and `models/README.md`.

## Unknowns / non-claims

- Beta Blocker's internal architecture, models, tracking method, overlay
  mechanism and licence code are **UNKNOWN** and were not examined.
- The vendor's performance and accuracy numbers are **OBSERVED CLAIMS**, not
  reproduced benchmarks.
- VeilDesk has no accuracy/precision/recall benchmark on a labelled dataset.
- No claim is made that VeilDesk is equivalent to, compatible with, or a
  replacement for Beta Blocker.
