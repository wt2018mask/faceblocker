"""Compare the VeilDesk YuNet decode against OpenCV's reference FaceDetectorYN.

OpenCV is only used here, in the development virtualenv, as an independent
reference. The shipped application has no OpenCV dependency.
"""
import sys

import cv2
import numpy as np
from PIL import Image

sys.path.insert(0, "tools")
from yunet_reference import YuNet, decode, render  # noqa: E402

MODEL = "models/face_detection_yunet_2023mar.onnx"


def reference(img_bgr, score_threshold=0.3, nms=0.45):
    det = cv2.FaceDetectorYN.create(MODEL, "", (640, 640), score_threshold, nms, 5000)
    det.setInputSize((img_bgr.shape[1], img_bgr.shape[0]))
    ok, faces = det.detect(img_bgr)
    if not ok or faces is None:
        return []
    out = []
    for f in faces:
        x, y, w, h = f[:4]
        score = f[-1]
        lms = f[4:14].reshape(5, 2)
        out.append((float(score), float(x), float(y), float(w), float(h), lms))
    return out


def main():
    astronaut = np.array(Image.open("assets/face-fixtures/astronaut.png").convert("RGB"))[:, :, ::-1]
    print("== astronaut.png 512x512 ==")
    ref = reference(astronaut)
    for r in ref:
        print(f"  OPENCV conf={r[0]:.4f} box=({r[1]:.1f},{r[2]:.1f},{r[3]:.1f},{r[4]:.1f})")
        print(f"         lms=" + " ".join(f"({p[0]:.0f},{p[1]:.0f})" for p in r[5]))

    net = YuNet()
    for mode in ("letterbox_bgr", "letterbox_rgb", "squash_bgr", "squash_rgb"):
        tensor, transform = render(astronaut, mode)
        outputs = net.run(tensor)
        boxes = decode(outputs, 512, 512, transform, threshold=0.3)
        for b in boxes:
            print(f"  {mode:14} conf={b[0]:.4f} box=({b[1]:.1f},{b[2]:.1f},{b[3]:.1f},{b[4]:.1f})")

    print("\n== landmark decode check (own decoder) ==")
    tensor, transform = render(astronaut, "letterbox_bgr")
    outputs = net.run(tensor)
    sx, sy, pad_x, pad_y = transform
    for stride in (8, 16, 32):
        cells = 640 // stride
        cls = outputs[f"cls_{stride}"].reshape(-1)
        obj = outputs[f"obj_{stride}"].reshape(-1)
        bbox = outputs[f"bbox_{stride}"].reshape(-1, 4)
        kps = outputs[f"kps_{stride}"].reshape(-1, 10)
        for i in range(cls.shape[0]):
            score = float(np.sqrt(max(cls[i], 0) * max(obj[i], 0)))
            if score < 0.3:
                continue
            cx = (i % cells + bbox[i][0]) * stride
            cy = (i // cells + bbox[i][1]) * stride
            bw = float(np.exp(bbox[i][2])) * stride
            bh = float(np.exp(bbox[i][3])) * stride
            k = kps[i]
            print(f"  stride={stride} score={score:.3f} cx={cx:.1f} cy={cy:.1f} bw={bw:.1f} bh={bh:.1f}")
            print(f"    kps={np.round(k,4).tolist()}")
            for name, fn in (
                ("k*stride+c", lambda j: ((k[2*j]*stride+cx)/sx, (k[2*j+1]*stride+cy)/sy)),
                ("k*bw+c", lambda j: ((k[2*j]*bw+cx)/sx, (k[2*j+1]*bh+cy)/sy)),
                ("k*bw+topleft", lambda j: ((k[2*j]*bw+cx-bw/2)/sx, (k[2*j+1]*bh+cy-bh/2)/sy)),
                ("k*stride+topleft", lambda j: ((k[2*j]*stride+cx-bw/2)/sx, (k[2*j+1]*stride+cy-bh/2)/sy)),
                ("k*stride+anchorcell", lambda j: ((k[2*j]*stride + (i % cells)*stride + stride/2)/sx,
                                                   (k[2*j+1]*stride + (i//cells)*stride + stride/2)/sy)),
            ):
                pts = [fn(j) for j in range(5)]
                print(f"    {name:22}: " + " ".join(f"({p[0]:.0f},{p[1]:.0f})" for p in pts))
            return


if __name__ == "__main__":
    main()
