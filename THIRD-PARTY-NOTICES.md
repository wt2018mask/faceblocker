# Third-party notices

VeilDesk is an independent implementation. It does not include code, assets,
logos, names, or licence mechanisms from Beta Blocker or any other proprietary
product.

## Application code

VeilDesk source is distributed under the GNU Affero General Public License,
version 3 (see `LICENSE`). The project is AGPL-3.0 because it incorporates
AGPL-3.0 model weights (NudeNet and Ultralytics YOLOv8) and is intended to be
distributed with corresponding source.

If you redistribute a packaged build, you must also make the corresponding
source available under the AGPL. The repository containing this file is that
source.

## Model weights

| File | Project | Licence | Source |
|---|---|---|---|
| `nudenet-320n.onnx` | NudeNet 320n | AGPL-3.0 | `notAI-tech/NudeNet`, GitHub release asset 176831997 |
| `yolov8n-320.onnx` | Ultralytics YOLOv8n, person class | AGPL-3.0 | exported from official `ultralytics/assets` v8.1.0 `yolov8n.pt` |
| `yolov8n-480.onnx` | Ultralytics YOLOv8n, person class | AGPL-3.0 | same export, 480×480 |
| `selfie_segmentation.onnx` | MediaPipe Selfie Segmentation (ONNX community export) | Apache-2.0 | `onnx-community/mediapipe_selfie_segmentation` on Hugging Face |

Exact sizes, SHA-256 hashes and preprocessing contracts are recorded in
`models/manifest.json` and `docs/REFERENCE_EVIDENCE.md`.

The weights are not committed to Git. `scripts/fetch-models.ps1` downloads the
pinned files and verifies their hashes. The application itself never downloads
models or any other content at runtime.

## Runtime and libraries

| Component | Licence |
|---|---|
| .NET 8 / WPF / Windows Desktop Runtime | MIT |
| Microsoft.ML.OnnxRuntime.DirectML 1.24.4 | MIT |
| ONNX Runtime | MIT |
| DirectML | MIT |
| xUnit and test tooling | Apache-2.0 / MIT (test-only, not shipped) |

Windows Graphics Capture, Direct3D 11 and Windows Media Foundation are operating
system APIs and are used under the Windows SDK terms.

## Fonts and UI assets

The UI uses Segoe UI, supplied with Windows. The veiled-shield mark is an
original geometric drawing created for this project; no third-party logo or
icon set is bundled. Built-in pack textures are generated procedurally for this
project.

## Reference material

Public product pages, patch notes and videos were consulted for feature
research only. No proprietary binary was decompiled, no proprietary asset was
copied, and no licence bypass was implemented. See
`docs/REFERENCE_EVIDENCE.md`.

## YuNet face detector

OpenCV Zoo `face_detection_yunet_2023mar.onnx` (232,589 bytes). MIT, copyright (c) 2020 Shiqi Yu. Full required notice: `models/YUNET-LICENSE.txt`. Authoritative source and SHA-256: `models/manifest.json`. No biometric identity inference or landmark persistence.
