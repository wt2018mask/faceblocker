# Bundled model provenance

Model weights are **not** committed to Git (they are large binaries). They are
downloaded and verified by `scripts/fetch-models.ps1`, or placed manually as
described below. The application never downloads models at runtime.

| Runtime file | Classifier | License | SHA-256 |
|---|---|---|---|
| `nudenet-320n.onnx` | NudeNet 320n (18 anatomical classes) | AGPL-3.0 | `C15D8273ADAD2D0A92F014CC69AB2D6C311A06777A55545F2C4EB46F51911F0F` |
| `yolov8n-320.onnx` | Ultralytics YOLOv8n, person class, 320×320 | AGPL-3.0 | `995548C2D6DB15CC9418D037BA8EBABD46E029201B9C35E49CB92D8C825A8004` |
| `yolov8n-480.onnx` | Ultralytics YOLOv8n, person class, 480×480 | AGPL-3.0 | `D030CCDE37BC8B757299733DE24C3AAF74A1FBDA00277FF3EB48533B25E4C9D3` |
| `selfie_segmentation.onnx` | MediaPipe Selfie Segmentation (ONNX community export) | Apache-2.0 | `3241AC4AD8AA35BDAF33946776DB29F7C283A413AA0B0DACB9483594B4531AAD` |

Source weights:

- NudeNet: `notAI-tech/NudeNet` release asset `320n.onnx`
  (`https://api.github.com/repos/notAI-tech/NudeNet/releases/assets/176831997`).
- YOLOv8n: official `ultralytics/assets` release `v8.1.0`, `yolov8n.pt`
  (6,534,387 bytes, SHA-256 `31E20DDE3DEF09E2CF938C7BE6FE23D9150BBBE503982AF13345706515F2EF95`),
  exported by `tools/export_models.py` with `opset=12`, `imgsz=320/480`,
  `dynamic=False`, `simplify=False`, CPU, FP32.
- Selfie segmentation: `onnx-community/mediapipe_selfie_segmentation`
  `onnx/model.onnx`, Apache-2.0. Preprocessing is a direct bilinear resize to
  256×256 and rescale by 1/255 (per the model's `preprocessor_config.json`);
  the graph already ends in Sigmoid and outputs `alphas` probabilities.

## Why AGPL matters

NudeNet and Ultralytics YOLOv8 weights are AGPL-3.0. VeilDesk is distributed
under AGPL-3.0-compatible terms; see `LICENSE` and `docs/REFERENCE_EVIDENCE.md`.
Do not replace these weights with proprietary models unless the license is
compatible and the change is documented.

## Reproduce

```powershell
powershell -ExecutionPolicy Bypass -File scripts/fetch-models.ps1
```

The script verifies every SHA-256 above and refuses to overwrite a mismatching
file. Exporting the YOLO ONNX files requires Python 3.10+ with
`ultralytics`/`torch`/`onnx`; the app itself never runs Python.
