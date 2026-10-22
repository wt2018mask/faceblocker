"""Offline analysis of VeilDesk [acceptance-analysis] records.

Reads the local application log only (no screen content, no network) and reports
face candidate geometry, confidence, track state and censor mask geometry.
"""
import json
import sys
from collections import defaultdict

MARKER = " [acceptance-analysis] "


def load(paths):
    records = []
    for path in paths:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                idx = line.find(MARKER)
                if idx < 0:
                    continue
                try:
                    payload = json.loads(line[idx + len(MARKER):])
                except Exception:
                    continue
                payload["_time"] = line[:idx].strip()
                records.append(payload)
    return records


def main():
    records = load(sys.argv[1:])
    print(f"analyses={len(records)}")

    faces = []
    for r in records:
        for f in r.get("faces") or []:
            b = f["Box"]
            faces.append((r["_time"], r.get("frame"), b["Width"], b["Height"],
                          b["X"], b["Y"], f["Confidence"]))

    faces.sort(key=lambda t: -(t[2] * t[3]))
    print(f"\n== largest {min(12,len(faces))} raw face candidates ==")
    print(f"{'time':34} {'frame':>7} {'w':>7} {'h':>7} {'x':>9} {'y':>9} {'conf':>7}")
    for t in faces[:12]:
        print(f"{t[0][:33]:34} {str(t[1]):>7} {t[2]:7.0f} {t[3]:7.0f} {t[4]:9.1f} {t[5]:9.1f} {t[6]:7.3f}")

    # How often does a very large candidate appear, and does it persist?
    big = [f for f in faces if f[2] * f[3] > 250000]
    print(f"\ncandidates with area > 250000 px^2: {len(big)} of {len(faces)}")
    if big:
        print(f"  max w={max(f[2] for f in big):.0f} max h={max(f[3] for f in big):.0f}")

    # Track lifetime of the biggest candidates: are they seen in consecutive frames?
    byframe = defaultdict(list)
    for r in records:
        for f in r.get("faces") or []:
            b = f["Box"]
            byframe[r.get("frame")].append((b["X"], b["Y"], b["Width"], b["Height"], f["Confidence"]))

    # Link masks to tracks and report mask vs raw geometry
    masks = []
    for r in records:
        tracks = {t["Id"]: t for t in r.get("tracks") or []}
        for m in r.get("masks") or []:
            tid = m.get("TrackId")
            if tid is None:
                continue
            t = tracks.get(tid)
            if t is None:
                continue
            bb = m["Box"]
            rb = t["Box"]
            masks.append((r["_time"], tid, m["Kind"], m["Source"], bb["Width"], bb["Height"],
                          rb["Width"], rb["Height"], t.get("Missed", 0),
                          t.get("VelocityX", 0.0), t.get("VelocityY", 0.0),
                          t.get("LastSeenMs", 0), r.get("frame")))
    masks.sort(key=lambda m: -(m[4] * m[5]))
    print(f"\n== largest {min(12,len(masks))} censor masks ==")
    print(f"{'time':30} {'tid':>5} {'kind':>4} {'maskW':>7} {'maskH':>7} {'rawW':>7} {'rawH':>7} {'miss':>4} {'vx':>8} {'vy':>8}")
    for m in masks[:12]:
        print(f"{m[0][:29]:30} {m[1]:5} {m[2]:4} {m[4]:7.0f} {m[5]:7.0f} {m[6]:7.0f} {m[7]:7.0f} {m[8]:4} {m[9]:8.1f} {m[10]:8.1f}")

    # Ghost analysis: for each track id, the time between last observation and the
    # last frame where a mask still referenced it.
    print("\n== ghost mask duration per track (ms) ==")
    last_obs = {}
    last_mask = {}
    first_mask = {}
    for r in records:
        frame = r.get("frame")
        ts = r.get("_time")
        for t in r.get("tracks") or []:
            if t.get("Region") == 101:
                last_obs[t["Id"]] = ts
        for m in r.get("masks") or []:
            if m.get("TrackId") is not None:
                last_mask[m["TrackId"]] = ts
                first_mask.setdefault(m["TrackId"], ts)
    from datetime import datetime

    def parse(s):
        return datetime.fromisoformat(s)

    ghosts = []
    for tid, lm in last_mask.items():
        if tid in last_obs:
            ghosts.append((parse(lm) - parse(last_obs[tid])).total_seconds() * 1000.0)
    ghosts.sort(reverse=True)
    if ghosts:
        print(f"  tracks with masks={len(ghosts)} ghostMs max={ghosts[0]:.0f} "
              f"p50={ghosts[len(ghosts)//2]:.0f} over500={sum(1 for g in ghosts if g > 500)}")


if __name__ == "__main__":
    main()
