"""Calibrate landmark-based candidate validation on a synthetic corpus.

Generates desktop-sized canvases with known faces (true positives) and
photo-like canvases without faces (false positives), then measures how well the
YuNet landmark geometry separates the two groups.

Development-only. No screen capture, no network, no window.
"""
import math
import sys

import cv2
import numpy as np
from PIL import Image

sys.path.insert(0, "tools")
from yunet_reference import YuNet  # noqa: E402

W, H = 2560, 1600
MODEL = "models/face_detection_yunet_2023mar.onnx"
NET = YuNet()


def render(img_bgr):
    """C#-equivalent centered letterbox into 640x640, BGR 0..255."""
    h, w = img_bgr.shape[:2]
    scale = min(640 / w, 640 / h)
    new_w, new_h = int(round(w * scale)), int(round(h * scale))
    pad_x = (640 - w * scale) / 2.0
    pad_y = (640 - h * scale) / 2.0
    resized = cv2.resize(img_bgr, (new_w, new_h), interpolation=cv2.INTER_LINEAR)
    canvas = np.zeros((640, 640, 3), np.uint8)
    x0, y0 = int(round(pad_x)), int(round(pad_y))
    canvas[y0:y0 + new_h, x0:x0 + new_w] = resized
    return canvas, (scale, pad_x, pad_y)


def detect_with_landmarks(img_bgr, threshold=0.3):
    """Replicates the official decode plus the official landmark formula."""
    resized, (scale, pad_x, pad_y) = render(img_bgr)
    tensor = np.ascontiguousarray(resized.transpose(2, 0, 1)[None]).astype(np.float32)
    outputs = NET.run(tensor)
    sx = scale
    sy = scale
    results = []
    for stride in (8, 16, 32):
        cells = 640 // stride
        cls = outputs[f"cls_{stride}"].reshape(-1)
        obj = outputs[f"obj_{stride}"].reshape(-1)
        bbox = outputs[f"bbox_{stride}"].reshape(-1, 4)
        kps = outputs[f"kps_{stride}"].reshape(-1, 10)
        for i in range(cls.shape[0]):
            score = float(np.sqrt(max(cls[i], 0) * max(obj[i], 0)))
            if score < threshold:
                continue
            c, r = i % cells, i // cells
            cx = (c + bbox[i][0]) * stride
            cy = (r + bbox[i][1]) * stride
            bw = float(np.exp(bbox[i][2])) * stride
            bh = float(np.exp(bbox[i][3])) * stride
            x = (cx - pad_x) / sx - (bw / sx) / 2
            y = (cy - pad_y) / sy - (bh / sy) / 2
            w = bw / sx
            h = bh / sy
            pts = []
            for n in range(5):
                px = (kps[i][2 * n] + c) * stride
                py = (kps[i][2 * n + 1] + r) * stride
                pts.append(((px - pad_x) / sx, (py - pad_y) / sy))
            results.append({"score": score, "box": (x, y, w, h), "kps": pts, "stride": stride})
    return results


def iou(a, b):
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    ix = max(0, min(ax + aw, bx + bw) - max(ax, bx))
    iy = max(0, min(ay + ah, by + bh) - max(ay, by))
    inter = ix * iy
    if inter <= 0:
        return 0.0
    return inter / (aw * ah + bw * bh - inter)


def metrics(box, pts):
    x, y, w, h = box
    re_p, le_p, nt_p, rcm_p, lcm_p = pts
    inside = 0
    for p in pts:
        if x - 0.05 * w <= p[0] <= x + 1.05 * w and y - 0.05 * h <= p[1] <= y + 1.05 * h:
            inside += 1
    eye_mid_x = (re_p[0] + le_p[0]) / 2
    eye_mid_y = (re_p[1] + le_p[1]) / 2
    mouth_mid_x = (rcm_p[0] + lcm_p[0]) / 2
    mouth_mid_y = (rcm_p[1] + lcm_p[1]) / 2
    return {
        "inside": inside / 5.0,
        "eyeSpan": abs(le_p[0] - re_p[0]) / w if w else 0,
        "eyeY": (eye_mid_y - y) / h if h else 0,
        "noseY": (nt_p[1] - y) / h if h else 0,
        "mouthY": (mouth_mid_y - y) / h if h else 0,
        "eyeSym": abs(eye_mid_x - (x + w / 2)) / w if w else 0,
        "mouthSym": abs(mouth_mid_x - (x + w / 2)) / w if w else 0,
        "noseAlign": abs(nt_p[0] - eye_mid_x) / w if w else 0,
        "mouthSpan": abs(lcm_p[0] - rcm_p[0]) / w if w else 0,
        "order": 1.0 if (eye_mid_y <= nt_p[1] + 0.05 * h and nt_p[1] <= mouth_mid_y + 0.05 * h) else 0.0,
        "lmBoxW": (max(p[0] for p in pts) - min(p[0] for p in pts)) / w if w else 0,
        "lmBoxH": (max(p[1] for p in pts) - min(p[1] for p in pts)) / h if h else 0,
    }


def photo_like(rng, size=(W, H)):
    w, h = size
    canvas = np.full((h, w, 3), 235, np.uint8)
    # Gradients
    gx = np.linspace(0, 255, w, dtype=np.float32)[None, :]
    gy = np.linspace(0, 255, h, dtype=np.float32)[:, None]
    grad = (gx * 0.6 + gy * 0.4).astype(np.uint8)
    canvas[:, :, 0] = grad
    canvas[:, :, 1] = grad // 2
    canvas[:, :, 2] = (255 - grad)
    # Random rectangles
    for _ in range(120):
        x, y = int(rng.integers(0, w - 10)), int(rng.integers(0, h - 10))
        bw, bh = int(rng.integers(20, 600)), int(rng.integers(20, 400))
        shade = int(rng.integers(0, 255))
        canvas[y:min(h, y + bh), x:min(w, x + bw)] = shade
    # Text-like bars
    for _ in range(200):
        x, y = int(rng.integers(0, w - 10)), int(rng.integers(0, h - 10))
        bw, bh = int(rng.integers(30, 400)), int(rng.integers(4, 30))
        canvas[y:min(h, y + bh), x:min(w, x + bw)] = int(rng.integers(0, 255))
    # Blobs
    for _ in range(15):
        cx, cy = int(rng.integers(0, w)), int(rng.integers(0, h))
        radius = int(rng.integers(30, 260))
        color = tuple(int(v) for v in rng.integers(0, 255, 3))
        cv2.circle(canvas, (cx, cy), radius, color, -1)
    return canvas


def main():
    rng = np.random.default_rng(11)
    astronaut = np.array(Image.open("assets/face-fixtures/astronaut.png").convert("RGB"))[:, :, ::-1]

    tp_rows = []
    fp_rows = []

    # --- true positives: the licensed face pasted at many scales/positions ---
    for trial in range(14):
        canvas = photo_like(rng)
        scale = float(rng.uniform(0.25, 1.1))
        size = int(512 * scale)
        x = int(rng.integers(0, max(1, W - size)))
        y = int(rng.integers(0, max(1, H - size)))
        patch = cv2.resize(astronaut, (size, size))
        canvas[y:y + size, x:x + size] = patch
        truth = (x + 178 * scale, y + 64 * scale, 94 * scale, 112 * scale)
        for det in detect_with_landmarks(canvas):
            tp_rows.append((metrics(det["box"], det["kps"]), det["score"], det["box"], truth,
                            iou(det["box"], truth)))

    # --- false positives: photo-like content without any face ---
    for trial in range(26):
        canvas = photo_like(rng)
        for det in detect_with_landmarks(canvas):
            fp_rows.append((metrics(det["box"], det["kps"]), det["score"], det["box"], None, 0.0))

    print(f"raw candidate counts: tp-ish={len(tp_rows)} fp-ish={len(fp_rows)}")

    # Split by IoU with the ground truth.
    true_pos = [r for r in tp_rows if r[4] >= 0.4]
    false_pos = [r for r in tp_rows if r[4] < 0.4] + fp_rows
    print(f"true positives (IoU>=0.4): {len(true_pos)}   false positives: {len(false_pos)}")
    print(f"false positive scores: " +
          ", ".join(f"{r[1]:.2f}" for r in sorted(false_pos, key=lambda r: -r[1])[:25]))

    keys = ["inside", "eyeSpan", "eyeY", "noseY", "mouthY", "eyeSym", "mouthSym",
            "noseAlign", "mouthSpan", "order", "lmBoxW", "lmBoxH"]
    print(f"\n{'metric':10} {'TP p05':>8} {'TP p50':>8} {'TP p95':>8} | "
          f"{'FP p05':>8} {'FP p50':>8} {'FP p95':>8}")
    for key in keys:
        t = sorted(r[0][key] for r in true_pos)
        f = sorted(r[0][key] for r in false_pos)
        if not t or not f:
            continue
        def q(a, p):
            return a[min(len(a) - 1, int(p * len(a)))]
        print(f"{key:10} {q(t,0.05):8.3f} {q(t,0.5):8.3f} {q(t,0.95):8.3f} | "
              f"{q(f,0.05):8.3f} {q(f,0.5):8.3f} {q(f,0.95):8.3f}")

    # Proposed rule: critical checks.
    def critical(m):
        return (m["inside"] >= 0.8 and m["order"] >= 1.0 and
                0.12 <= m["eyeSpan"] <= 0.75 and m["eyeSym"] <= 0.28 and
                m["noseAlign"] <= 0.25 and 0.15 <= m["eyeY"] <= 0.62 and
                0.45 <= m["mouthY"] <= 0.98)
    tp_ok = sum(1 for r in true_pos if critical(r[0]))
    fp_ok = sum(1 for r in false_pos if critical(r[0]))
    print(f"\ncritical rule: true positives kept {tp_ok}/{len(true_pos)}  "
          f"false positives kept {fp_ok}/{len(false_pos)}")
    tp_low = [r for r in true_pos if r[1] < 0.6]
    print(f"  true positives below 0.6 confidence: {len(tp_low)}, "
          f"kept {sum(1 for r in tp_low if critical(r[0]))}")


if __name__ == "__main__":
    main()
