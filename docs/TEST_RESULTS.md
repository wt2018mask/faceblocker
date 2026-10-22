# 최신 실행 결과 — 2026-10-22 복구 세션

패키징 준비 중 첫 실행은 PowerShell 단일 문자열 splat 오류(MSB1001)로 실패해 배열 타입으로 수정했다.
그 다음 실행은 x64 런타임 assets 미복원(NETSDK1047)으로 실패했다.
`dotnet restore src/VeilDesk.App/VeilDesk.App.csproj -r win-x64 --source "$env:USERPROFILE\.nuget\packages"`
로 **로컬 캐시만 사용한 복원 PASS**. 실패한 출력 폴더와 이전 배포본은 삭제하지 않았다.

아래 최신 절만 이번 세션의 실행 증거다. 세부 절차는 RECOVERY-20261022.md 참조.

| 실행 | 결과 |
|---|---|
| 최초 격리 환경 dotnet test | FAIL: testhost IPC 연결이 90초 후 중단. 테스트 assertion 실패와 구분한다. |
| 로컬 기존 테스트 재실행 | PASS 26/26 |
| `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Headless.ps1` | PASS: 39/39, Release 0 오류/0 경고(`RunAnalyzers=false`), 보호 프로브, WPF Image 초기화 |
| `dotnet run --project tools/ProtectionProbe/ProtectionProbe.csproj -c Release --no-build -- models artifacts/live-test/david.jpg` | PASS: 실제 CPU ONNX 검출→추적→해부학적 마스크→픽셀 합성. 사람 1, 해부학적 검출 9, 검열 영역 2, 변경 픽셀 128,082 |
| 추론 정지 중 보호 판정 및 Stop | PASS: 생산 VisionPipeline에 제어 가능한 모델 주입. 판정 4.54 ms, 전체 보호 영역 존재, Stop 후 영역 0, 늦은 완료 후에도 영역 0 |
| 전체 보호 렌더러 | PASS: 음수 모니터 원점, 150% DPI 기술값, CornerRadius=100, Opacity=0.1에서 모서리 불투명 픽셀 확인. 실제 창 검증과 구분 |
| 옵션 없는 로컬 legacy viewer/run-live 호출 | PASS: 명시적 opt-in 요구 오류로 종료, 창 생성 경로에 진입하지 않음 |
| 실제 Ctrl+C, 전역 비상키, 엔진 Start/Stop 창 수명 | NOT RUN: 사용자 참여 대화형 검증 필요 |
| 현재 데스크톱 검출→실제 오버레이·녹화·복수 모니터 | NOT RUN |

새 회귀 13개는 복구 dwell, 중복 성공, 지연 추론, 캡처 누락, 결과 순서, 실패 교대,
캡처 복구, 100회 상태 Start/Stop, confidence hysteresis, 불변 스냅샷,
트랙 누락/회복, 두 합성 디스플레이 좌표 경우를 검사한다. 상태 Start/Stop 테스트는 실제 창 테스트가 아니다.
화면 캡처나 테스트 미디어를 외부로 전송하지 않았다.

# 이전 에이전트의 역사적 Test results

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
