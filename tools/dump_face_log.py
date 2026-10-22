"""Dump the recorded acceptance log around a given frame or time window."""
import json
import sys

MARKER = " [acceptance-analysis] "


def main():
    lo = int(sys.argv[1])
    hi = int(sys.argv[2])
    for path in sys.argv[3:]:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                idx = line.find(MARKER)
                if idx < 0:
                    continue
                try:
                    payload = json.loads(line[idx + len(MARKER):])
                except Exception:
                    continue
                frame = payload.get("frame")
                if frame is None or not (lo <= frame <= hi):
                    continue
                ts = line[:idx].strip()[11:23]
                print(f"--- frame {frame} t={ts} age={payload.get('frameAgeMs')}ms "
                      f"faceMs={payload.get('FaceMs'):.0f} total={payload.get('TotalMs'):.0f}")
                for f in payload.get("faces") or []:
                    b = f["Box"]
                    print(f"   FACE conf={f['Confidence']:.3f} "
                          f"({b['X']:.0f},{b['Y']:.0f}) {b['Width']:.0f}x{b['Height']:.0f}")
                tracks = {t["Id"]: t for t in payload.get("tracks") or []}
                for t in payload.get("tracks") or []:
                    if t.get("Region") != 101:
                        continue
                    b = t["Box"]
                    print(f"   TRACK {t['Id']} missed={t['Missed']} "
                          f"box=({b['X']:.0f},{b['Y']:.0f}) {b['Width']:.0f}x{b['Height']:.0f} "
                          f"v=({t['VelocityX']:.0f},{t['VelocityY']:.0f})")
                for m in payload.get("masks") or []:
                    b = m["Box"]
                    tid = m.get("TrackId")
                    tr = tracks.get(tid)
                    print(f"   MASK track={tid} kind={m['Kind']} "
                          f"({b['X']:.0f},{b['Y']:.0f}) {b['Width']:.0f}x{b['Height']:.0f} "
                          f"area%={100*b['Width']*b['Height']/(2560*1600):.1f}"
                          + (f" trackMissed={tr['Missed']}" if tr else ""))


if __name__ == "__main__":
    main()
