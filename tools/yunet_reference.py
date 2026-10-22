"""Reference decoder for the bundled YuNet 2023mar export.

Mirrors src/VeilDesk.Inference/YuNetDetector.cs exactly so that preprocessing
variants can be compared against the licensed ground-truth fixture without
changing the production code. Development-only tool: the application itself has
no Python or OpenCV dependency.
"""
import math

import numpy as np
import onnxruntime as ort

MODEL = "models/face_detection_yunet_2023mar.onnx"
SIZE = 640
STRIDES = (8, 16, 32)


def render(img_bgr, mode):
    """Returns (canvas_bgr, (scale_x, scale_y, pad_x, pad_y)).

    scale is source->model; pad is in model pixels. The sampling rule matches
    FramePreprocessor.BgraToRgbNchw: model pixel (mx,my) samples source
    ((mx-pad_x)/scale_x, (my-pad_y)/scale_y) with bilinear interpolation and a
    fill outside the source rectangle.
    """
    h, w = img_bgr.shape[:2]
    if mode.startswith("letterbox"):
        scale = min(SIZE / w, SIZE / h)
        sx = sy = scale
        pad_x = (SIZE - w * scale) / 2.0
        pad_y = (SIZE - h * scale) / 2.0
    else:
        sx = SIZE / w
        sy = SIZE / h
        pad_x = 0.0
        pad_y = 0.0
    src = img_bgr.astype(np.float32)
    xs = (np.arange(SIZE) - pad_x) / sx
    ys = (np.arange(SIZE) - pad_y) / sy
    inside_x = (xs >= 0) & (xs <= w - 1)
    inside_y = (ys >= 0) & (ys <= h - 1)
    x0 = np.clip(np.floor(xs).astype(int), 0, w - 1)
    y0 = np.clip(np.floor(ys).astype(int), 0, h - 1)
    x1 = np.minimum(x0 + 1, w - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    fx = np.where(inside_x, xs - x0, 0.0).astype(np.float32)
    fy = np.where(inside_y, ys - y0, 0.0).astype(np.float32)
    fx = fx[None, :, None]
    fy = fy[:, None, None]
    valid = (inside_x[None, :, None] & inside_y[:, None, None]).astype(np.float32)
    top = src[np.ix_(y0, x0)] * (1 - fx) + src[np.ix_(y0, x1)] * fx
    bot = src[np.ix_(y1, x0)] * (1 - fx) + src[np.ix_(y1, x1)] * fx
    canvas = (top * (1 - fy) + bot * fy) * valid
    if mode.endswith("rgb"):
        canvas = canvas[:, :, ::-1]
    return np.ascontiguousarray(canvas.transpose(2, 0, 1)[None]), (sx, sy, pad_x, pad_y)


class YuNet:
    def __init__(self, path=MODEL):
        self.session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])

    def run(self, tensor):
        outs = self.session.get_outputs()
        results = self.session.run([o.name for o in outs], {"input": tensor})
        return {o.name: r for o, r in zip(outs, results)}


def decode(outputs, src_w, src_h, transform, threshold=0.35, nms=0.45):
    """transform: (scale_x, scale_y, pad_x, pad_y); scale is source->model."""
    sx, sy, pad_x, pad_y = transform
    boxes = []
    for stride in STRIDES:
        cells = SIZE // stride
        cls = outputs[f"cls_{stride}"].reshape(-1)
        obj = outputs[f"obj_{stride}"].reshape(-1)
        bbox = outputs[f"bbox_{stride}"].reshape(-1, 4)
        for i in range(cls.shape[0]):
            score = math.sqrt(float(np.clip(cls[i], 0, 1)) * float(np.clip(obj[i], 0, 1)))
            if not math.isfinite(score) or score < threshold:
                continue
            cx = (i % cells + bbox[i][0]) * stride
            cy = (i // cells + bbox[i][1]) * stride
            w = math.exp(bbox[i][2]) * stride
            h = math.exp(bbox[i][3]) * stride
            if not all(map(math.isfinite, (cx, cy, w, h))):
                continue
            x = (cx - pad_x) / sx - w / sx / 2.0
            y = (cy - pad_y) / sy - h / sy / 2.0
            bw = w / sx
            bh = h / sy
            x0 = max(0.0, min(x, src_w))
            y0 = max(0.0, min(y, src_h))
            x1 = max(0.0, min(x + bw, src_w))
            y1 = max(0.0, min(y + bh, src_h))
            if x1 - x0 <= 0.001 or y1 - y0 <= 0.001:
                continue
            boxes.append((score, x0, y0, x1 - x0, y1 - y0))
    return _nms(boxes, nms)


def _iou(a, b):
    ax0, ay0, ax1, ay1 = a[1], a[2], a[1] + a[3], a[2] + a[4]
    bx0, by0, bx1, by1 = b[1], b[2], b[1] + b[3], b[2] + b[4]
    ix = max(0.0, min(ax1, bx1) - max(ax0, bx0))
    iy = max(0.0, min(ay1, by1) - max(ay0, by0))
    inter = ix * iy
    if inter <= 0:
        return 0.0
    union = a[3] * a[4] + b[3] * b[4] - inter
    return inter / union if union > 0 else 0.0


def _nms(boxes, threshold):
    ordered = sorted(boxes, key=lambda b: -b[0])
    result = []
    while ordered:
        best = ordered.pop(0)
        result.append(best)
        ordered = [b for b in ordered if _iou(best, b) < threshold]
    return result


def landmarks(outputs, box, transform):
    """Landmark boxes of the anchor with the best IoU against `box`."""
    sx, sy, pad_x, pad_y = transform
    best = None
    for stride in STRIDES:
        cells = SIZE // stride
        cls = outputs[f"cls_{stride}"].reshape(-1)
        obj = outputs[f"obj_{stride}"].reshape(-1)
        bbox = outputs[f"bbox_{stride}"].reshape(-1, 4)
        kps = outputs[f"kps_{stride}"].reshape(-1, 10)
        for i in range(cls.shape[0]):
            score = math.sqrt(float(np.clip(cls[i], 0, 1)) * float(np.clip(obj[i], 0, 1)))
            if score < 0.05:
                continue
            cx = (i % cells + bbox[i][0]) * stride
            cy = (i // cells + bbox[i][1]) * stride
            w = math.exp(bbox[i][2]) * stride
            h = math.exp(bbox[i][3]) * stride
            x = (cx - pad_x) / sx - w / sx / 2.0
            y = (cy - pad_y) / sy - h / sy / 2.0
            cand = (score, x, y, w / sx, h / sy)
            if best is None or _iou(cand, box) > _iou(best, box):
                best = cand
                pts = []
                for k in range(5):
                    px = (kps[i][2 * k] * stride + cx) / sx
                    py = (kps[i][2 * k + 1] * stride + cy) / sy
                    pts.append((px, py))
    return pts if best else None
