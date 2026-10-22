"""Measure YuNet behaviour on a 2560x1600 desktop-sized canvas.

Purpose: decide whether the centered letterbox used by the app (with black
bars) is a plausible cause of the recorded 795x977 low-confidence candidate,
and whether reference preprocessing (direct resize, as OpenCV FaceDetectorYN
does) or tiled inference behaves better.

Synthetic canvases only. No screen capture, no network, no window.
"""
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, "tools")
from yunet_reference import YuNet, decode, render  # noqa: E402

W, H = 2560, 1600


def make_canvas(face_boxes):
    """A webpage-like canvas: light background, grey blocks, one pasted face."""
    canvas = np.full((H, W, 3), 245, np.uint8)
    rng = np.random.default_rng(7)
    for i in range(40):
        x = int(rng.integers(0, W - 400))
        y = int(rng.integers(0, H - 300))
        w = int(rng.integers(120, 500))
        h = int(rng.integers(80, 320))
        shade = int(rng.integers(170, 240))
        canvas[y:y + h, x:x + w] = shade
    astronaut = np.array(Image.open("assets/face-fixtures/astronaut.png").convert("RGB"))[:, :, ::-1]
    truths = []
    for (x, y, size) in face_boxes:
        sub = Image.fromarray(astronaut[:, :, ::-1]).resize((size, size), Image.BILINEAR)
        canvas[y:y + size, x:x + size] = np.array(sub)[:, :, ::-1]
        scale = size / 512.0
        truths.append((x + 178 * scale, y + 64 * scale, 94 * scale, 112 * scale))
    return canvas, truths


def iou(a, b):
    ax0, ay0, ax1, ay1 = a
    bx0, by0, bx1, by1 = b
    ix = max(0.0, min(ax1, bx1) - max(ax0, bx0))
    iy = max(0.0, min(ay1, by1) - max(ay0, by0))
    inter = ix * iy
    if inter <= 0:
        return 0.0
    union = (ax1 - ax0) * (ay1 - ay0) + (bx1 - bx0) * (by1 - by0) - inter
    return inter / union if union > 0 else 0.0


def run(net, canvas, mode, threshold):
    tensor, transform = render(canvas, mode)
    outputs = net.run(tensor)
    boxes = decode(outputs, W, H, transform, threshold=threshold)
    return boxes


def tiled(net, canvas, threshold, cols=2, rows=2, overlap=0.0):
    """Run the detector on overlapping tiles and map boxes back to the screen."""
    tw = W / cols
    th = H / rows
    ox = tw * overlap
    oy = th * overlap
    results = []
    for r in range(rows):
        for c in range(cols):
            x0 = max(0, int(c * tw - ox))
            y0 = max(0, int(r * th - oy))
            x1 = min(W, int((c + 1) * tw + ox))
            y1 = min(H, int((r + 1) * th + oy))
            sub = canvas[y0:y1, x0:x1]
            tensor, transform = render(sub, "squash_rgb")
            outputs = net.run(tensor)
            for b in decode(outputs, x1 - x0, y1 - y0, transform, threshold=threshold):
                results.append((b[0], b[1] + x0, b[2] + y0, b[3], b[4]))
    # NMS across tiles
    ordered = sorted(results, key=lambda b: -b[0])
    kept = []
    while ordered:
        best = ordered.pop(0)
        kept.append(best)
        ordered = [b for b in ordered if iou((b[1], b[2], b[1] + b[3], b[2] + b[4]),
                                              (best[1], best[2], best[1] + best[3], best[2] + best[4])) < 0.45]
    return kept


def report(name, boxes, truths, threshold):
    lines = []
    matched = set()
    for t in truths:
        tr = (t[0], t[1], t[0] + t[2], t[1] + t[3])
        best = -1
        best_iou = 0.0
        for i, b in enumerate(boxes):
            v = iou((b[1], b[2], b[1] + b[3], b[2] + b[4]), tr)
            if v > best_iou:
                best_iou = v
                best = i
        if best >= 0 and best_iou >= 0.4:
            matched.add(best)
            lines.append(f"    truth hit IoU={best_iou:.3f} conf={boxes[best][0]:.3f} "
                         f"box=({boxes[best][1]:.0f},{boxes[best][2]:.0f},{boxes[best][3]:.0f},{boxes[best][4]:.0f})")
        else:
            lines.append(f"    truth MISSED (bestIoU={best_iou:.3f})")
    fps = [b for i, b in enumerate(boxes) if i not in matched]
    fp_desc = ", ".join(f"conf={b[0]:.3f} ({b[1]:.0f},{b[2]:.0f},{b[3]:.0f},{b[4]:.0f})" for b in fps)
    print(f"  {name:22} thr={threshold:.2f} det={len(boxes)} matched={len(matched)}/{len(truths)} fp={len(fps)}")
    for line in lines:
        print(line)
    if fp_desc:
        print(f"    false positives: {fp_desc}")
    return len(matched), fps


def main():
    net = YuNet()
    scenarios = [
        ("no-face desktop", []),
        ("one small face 180px", [(1200, 500, 180)]),
        ("one large face 520px", [(900, 400, 520)]),
        ("two faces", [(300, 200, 220), (1700, 900, 260)]),
    ]
    for title, faces in scenarios:
        canvas, truths = make_canvas(faces)
        print(f"\n== {title} ==")
        for mode in ("letterbox_bgr", "letterbox_rgb", "squash_bgr", "squash_rgb"):
            for threshold in (0.35, 0.6):
                boxes = run(net, canvas, mode, threshold)
                report(f"{mode}", boxes, truths, threshold)
        for threshold in (0.35, 0.6):
            boxes = tiled(net, canvas, threshold)
            report("tiled 2x2 squash_rgb", boxes, truths, threshold)


if __name__ == "__main__":
    main()
