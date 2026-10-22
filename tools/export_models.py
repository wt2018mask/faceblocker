"""Export the official Ultralytics YOLOv8n detector to ONNX for VeilDesk.

Run from the repository root with the tooling virtual environment:
    python tools/export_models.py

This script is a build-time tool only. The application never trains or invokes
Python at runtime. Ultralytics YOLOv8 weights are AGPL-3.0; see docs/REFERENCE_EVIDENCE.md.
"""
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parent.parent
MODELS = ROOT / "models"


def main() -> int:
    try:
        from ultralytics import YOLO
    except Exception as exc:
        print(f"ultralytics is required to export the detector: {exc}", file=sys.stderr)
        return 2

    weights = MODELS / "yolov8n.pt"
    if not weights.exists():
        print(f"missing official weights: {weights}", file=sys.stderr)
        return 3

    model = YOLO(str(weights))
    for size in (320, 480):
        print(f"exporting YOLOv8n at {size}x{size} ...")
        exported = model.export(
            format="onnx",
            imgsz=size,
            opset=12,
            simplify=False,
            dynamic=False,
            batch=1,
            device="cpu",
            half=False,
        )
        source = Path(exported)
        target = MODELS / f"yolov8n-{size}.onnx"
        shutil.copyfile(source, target)
        print(f"wrote {target} ({target.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
