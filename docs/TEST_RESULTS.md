# Test results

**Date:** 2026-10-22 (Asia/Seoul)
**Machine:** Intel i7-1065G7, 16 GB RAM, Iris Plus, Windows 11 build 26200,
2560×1600 @ 150% DPI
**Build:** Debug (tests and live UI), .NET SDK 8.0.426, runtime .NET 8.0.32

Only tests actually executed are listed. Anything not executed is explicitly
marked **NOT RUN**.

## 1. Automated tests

Command:

```powershell
$env:DOTNET_CLI_UI_LANGUAGE="en"
dotnet test tests\VeilDesk.Core.Tests\VeilDesk.Core.Tests.csproj --nologo
```

Result:

```
Passed!  - Failed: 0, Passed: 23, Skipped: 0, Total: 23, Duration: 136 ms
```

Coverage includes:

- rectangle clip/IoU and letterbox coordinate mapping
- NudeNet and YOLO decoding/NMS
- segmentation largest-component cleanup
- tracker prediction, persistence, ID retention and person association
- composer whole-body rules, gender rules, confidence and fail-closed
- settings round-trip, clamping, corrupt-file quarantine, hex colour validation
- scene-change detection and scheduler safety scans
- pack validation, traversal/executable rejection, wrong format
- built-in pack extraction and censoring-preset parsing
- MJPEG AVI RIFF headers, `movi` sizes, `idx1` offsets and odd-byte padding
- AVI argument validation
- session summarization and achievements

The AVI test writes a real container and parses every index entry; it is not a
mock-only test.

## 2. Application build

Command:

```powershell
dotnet build src\VeilDesk.App\VeilDesk.App.csproj -c Debug --nologo
```

Result: **Build succeeded, 0 errors** after the built-in pack and AVI changes.
(The build emits analyzer warnings elsewhere; they are not test failures.)

## 3. Real capture

Probes: `tools/WgcProbe`, `tools/CaptureProbe`.

Observed:

- 1 display, 2560×1600, 150% DPI
- WGC + D3D11 initialization succeeded
- ~13–19 fps delivered at the requested 15 fps
- frame readback BGRA8, 100.0% sampled non-black
- capture start 360–636 ms; stop ~1 s
- BMP/PNG written and inspected

This was executed while the desktop was unlocked. On the later locked secure
desktop, frames were black, as expected.

## 4. Real local inference

Probe: `tools/VisionProbe` with `--cpu`.

Observed on `bus.jpg`:

- provider CPU; model load ~281 ms
- steady full pass ~54–67 ms
- 3 correct person boxes
- no network access

Observed on the Michelangelo *David* test image:

- YOLO person box
- NudeNet `FEMALE_BREAST_EXPOSED`
- decoded regions visually aligned with the source image in the annotated probe
  output

This is a static-image capability check, not an accuracy benchmark.

## 5. Live Start/Stop and session log

Observed in the WPF app:

- Start protection → “Protecting”
- provider CPU
- capture ~12–14 fps
- analysis P50 ~28–43 ms
- overlay running = Yes
- overlay excluded from capture = Yes
- Stop → Idle
- session log: 97.124 s, 103 detection events, peak 11 tracks

A later session lasted 710.6 s with 0 detections because the Windows session
was locked; this is consistent with WGC black frames and fail-closed, not a
passing detection test.

## 6. Screenshot export

Executed from the app with censorship compositing enabled.

Output:

```
C:\Users\wt201\OneDrive\Pictures\VeilDesk\VeilDesk-20261022-101512-Generic-PnP-Monitor.png
```

The file is 202,240 bytes, is a real WGC frame of the desktop, and was written
by the app's PNG encoder through `ExportService`. Visual inspection found no
censor region in the image, so this run proves capture + PNG encoding only; it
is not proof of compositing during a live detection, and it is not a screen grab
of the overlay.

Deterministic compositor check (executed):

```powershell
dotnet run --project tools\ExportProbe\ExportProbe.csproj -c Debug
```

Output:

```
source center bgr=200,120,40
composed center bgr=18,13,11
composed corner bgr=200,120,40
control outside bgr=200,120,40
changed pixels within region box: 11.46%
png=...\tools\ExportProbe\bin\Debug\net8.0-windows10.0.19041.0\export-probe.png
inside-ok=True outside-ok=True control-unchanged=True coverage-ok=True
PASS screenshot compositor
```

The probe feeds a synthetic 640×360 frame and a 220×120 region through the
shipped `ExportService.Composite`. The region center becomes `#0B0D12`, the
outside stays unchanged, and only the expected 11.46% of pixels differ from an
empty-region control. This verifies the renderer and coordinate placement in
the export path; it does not verify a live detection or live overlay placement.

## 7. AVI writer and OS playback

Command:

```powershell
dotnet run --project tools/AviProbe/AviProbe.csproj -- `
  "C:\Users\wt201\OneDrive\Pictures\VeilDesk\VeilDesk-20261022-101512-Generic-PnP-Monitor.png" `
  "artifacts\avi-probe\veildesk-avi-probe.avi"
```

Output:

```
source: ...\VeilDesk-20261022-101512-Generic-PnP-Monitor.png
output: ...\artifacts\avi-probe\veildesk-avi-probe.avi
written: 30 frames, 884,818 bytes
playback: MEDIA_OPENED duration=00:00:02.000 decoder=640x360
poster: ...\veildesk-avi-probe.decoded.png nonBlack=96.4%
exit=0
```

Inspector output for the same file:

```
bytes=884,818 frames=30 moviBytes=884,110 idx1=yes
playback: MEDIA_OPENED duration=00:00:02.000 decoder=640x360
poster: ...\veildesk-avi-probe.decoded.png nonBlack=96.4%
```

The file was opened by Windows Media Foundation and a decoded poster was saved.
This verifies the container and JPEG stream. The in-app `VideoRecorder` loop is
**NOT RUN** end-to-end yet.

A correctness bug was found and fixed during this check: the `movi` LIST size
field excluded the four-byte `movi` FOURCC, so the file's declared list ended
four bytes before `idx1`. Media Foundation tolerated it, but the index was not
strictly addressable and `AviProbe --inspect` could not see it. The writer now
uses the full LIST content size, the unit test asserts that size explicitly, and
the regenerated probe reports `idx1=yes` with a 2.000 s duration.

## 8. UI rendering

All ten pages were rendered with a DPI-aware `PrintWindow` at 150% DPI and
inspected:

- Dashboard
- Detection
- Censoring
- Displays
- Performance
- Export
- Packs
- Sessions
- Settings
- About & diagnostics

Startup crash, page-overlap, clipped-label, text-thickness and close-crash
issues found during this work were fixed and re-checked. Page screenshots live
under `%TEMP%\vd-pages` on the test machine and are not committed.

## 9. Built-in packs

The four `.veilpack` files under `builtin-packs/` were built as ZIP archives
with `manifest.json` and (for Frost Byte) a generated PNG. The automated test
extracts every pack, checks the manifest format/id, parses its `Censoring`
preset into `CensorSettings`, and verifies referenced assets exist.

The first-launch copy into a throw-away `VEILDESK_HOME` root was executed on
2026-10-22. The app started successfully and copied all four packs (521, 520,
537 and 6,196 bytes) into `<root>\packs`, then wrote
`.builtin-packs-installed`. Deleting one pack and relaunching left it deleted;
deleting the marker and relaunching restored it. The same behavior was observed
with the published Release build. The visible Packs page was inspected in the
earlier PrintWindow pass; applying a pack from the live UI was not re-run after
the marker change.

## 10. Release package

Executed:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
```

Observed:

- all four ONNX hashes re-verified by the script;
- `artifacts\VeilDesk-win-x64` — 238.2 MB, 494 files;
- `artifacts\VeilDesk-1.0.0-win-x64.zip` — 110.5 MB;
- final ZIP SHA-256 is recorded in `PACKAGE-SHA256.txt` at the repository root,
  outside the archive;
- ZIP contains `VeilDesk.App.exe`, `coreclr.dll`, all four models, all four
  built-in packs, `docs/`, `LICENSE`, `BUILD-INFO.txt` and `SHA256SUMS.txt`;
- the published `VeilDesk.App.exe` was launched with a temporary
  `VEILDESK_HOME`, started successfully, and installed all four packs;
- the final ZIP SHA-256 is written to `PACKAGE-SHA256.txt`;
- the repackaged build was launched again with a fresh `VEILDESK_HOME` after the
  documentation/XAML edits and installed all four packs (marker present).

This is a build-host launch, not a clean-machine/VM install. No installer or
code signing is provided.

## 11. Not run / blocked

| Item | Reason |
|---|---|
| Live visual overlay placement and moving-target tracking | Windows session locked; WGC black frames |
| Screenshot containing a live detection | Exported frame had no censor region; locked session prevents a live detection |
| In-app video recording start/stop and recorded frame count | Blocked by locked session; requires live capture |
| Two physical monitors / mixed DPI | Only one display available |
| Clean-machine / VM package install | Package launched only on the build host |
| Detection accuracy on a labelled dataset | No labelled dataset; not claimed |
| 24-hour soak / thermal test | Not executed |
| Installer / code signing | Portable ZIP only |
| DirectML live run after cold start | Measured but not selected; startup cost unacceptable |

## Reproduce the automated portion

```powershell
dotnet test tests\VeilDesk.Core.Tests\VeilDesk.Core.Tests.csproj
dotnet build src\VeilDesk.App\VeilDesk.App.csproj -c Debug
powershell -ExecutionPolicy Bypass -File scripts/package.ps1
```

See `docs/HANDOFF.md` for the live verification checklist.
