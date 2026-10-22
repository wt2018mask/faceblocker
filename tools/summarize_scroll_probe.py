"""Summarise the FaceScrollProbe report."""
import json
import sys

path = sys.argv[1]
data = json.load(open(path, encoding="utf-8"))
print(f"{'sequence':16} {'cov':>5} {'obs':>4} {'acc':>4} {'rej':>4} "
      f"{'maxFrac':>8} {'maxMask':>11} {'ghost':>6} | "
      f"{'legMaxFrac':>10} {'legMask':>11} {'legGhost':>8} {'legUnnec':>9}")
for s in data["sequences"]:
    print(f"{s['Name']:16} {s['Coverage']:5.2f} {s['FaceObservations']:4} "
          f"{s.get('CandidatesAccepted', 0):4} {s.get('CandidatesRejected', 0):4} "
          f"{s['MaximumMaskScreenFraction']:8.4f} {s['MaximumMaskSize']:>11} "
          f"{s['GhostMaskMs']:6} | "
          f"{s.get('LegacyMaximumMaskScreenFraction', 0):10.4f} "
          f"{s.get('LegacyMaximumMaskSize', ''):>11} {s.get('LegacyGhostMaskMs', -1):8} "
          f"{s.get('LegacyUnnecessaryMaskFraction', 0):9.4f}")

if len(sys.argv) > 2:
    wanted = sys.argv[2]
    for s in data["sequences"]:
        if s["Name"] != wanted:
            continue
        for sample in s.get("Samples", []):
            print(f"\nframe {sample['Frame']} truth={sample['Truth']}")
            for c in sample["Candidates"]:
                print(f"   candidate {c['Box']:>22} conf={c['Confidence']:.3f} "
                      f"{c['Quality']} {c['Reason']}")
            for v in sample["Verdicts"]:
                print(f"   verdict  {v}")
            for m in sample["Masks"]:
                print(f"   mask     {m}")
