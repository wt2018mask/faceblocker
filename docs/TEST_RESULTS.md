# 최신 사용자 안내 및 Face 국소 가림 검증 (2026-10-22)

상세 근거: [BROWSER-GUIDED-20261022.md](BROWSER-GUIDED-20261022.md).

| 항목 | 판정 |
|---|---|
| Core / Release / 기존 보호·녹화·YuNet | VERIFIED: 53/53, Release0경고/0오류(분석기 비활성화), 기존 headless probe PASS |
| 원시 기준 속도·얼굴별 dt 회귀 | VERIFIED: 수정 전3/3 실패, 수정 후PASS |
| 2초 외삽/중복 확장 회귀 | VERIFIED: 수정 전4/4 실패, 수정 후PASS; raw 전체 포함 유지 |
| 기록 기하1,068개 | VERIFIED: 관측 상자 포함 실패0; 최대 단일 면적78.4%→15.7%, 화면 증거 아님 |
| 실제 얼굴 검은 가림·저장 Face 재시작 | VERIFIED: 사용자 직접 확인 |
| Stop 버튼 | PARTIAL: 사용자 Stop 사용 확인; 모든 시나리오의 정리/영상 증거는 없음 |
| 수정 후 Instagram 스크롤 | PARTIAL / 결함 남음: 큰/엉뚱한 가림이 약2초 후 사라진다는 사용자 응답 |
| Chrome 포커스 F10 / click-through / typing / tab / resize / video | NOT RUN/확정 응답 없음 |
| 장시간 live / 움직이는 브라우저 export / 독립 패키지 | NOT RUN |
| 물리 다중 모니터 / 릴리스 | BLOCKED: 장치1개 / 브라우저 필수 게이트 미통과 |

10분·5분·3분 사용자 허용 시험은 모두 종료/소유 프로세스 정리 완료. 원시795×977px 후보가 새 빌드에서도 나타나 ROI 검증과 스크롤 추적이 남았다. 실제 픽셀 무노출·전체 안정성·M10 성공 주장 없음. 아래 기록은 이전 검증 이력이다.

# 최신 Face 검증 — 2026-10-22

[상세 증거/명령/한계](FACE-BROWSER-20261022.md).

| 항목 | 결과 |
|---|---|
| Core / Release | VERIFIED: 46/46,0경고/0오류(분석기 비활성화) |
| 실제 YuNet | VERIFIED: 제한 fixture6/6,추가검출0,빈장면0,이동20/20·주석상자95% 가림20/20 |
| 정적 영상 표본 | VERIFIED: 로컬 VP8 전체160프레임 decode,8개 표본 얼굴8/8 |
| 모델 독립성 | VERIFIED: 얼굴 모델만 있는 폴더로 Face-only; 관련 없는 모델 미실행 |
| 불확실 Face | VERIFIED headless: 확대 국소 보호의 모든 픽셀 불투명; Outline/10%/둥근모서리 적용 불가; 엄격 모드 유지 |
| 기존 보호/녹화/캐시 | VERIFIED: 전체 fail-closed·늦은 결과/Stop·AVI Stop 수명·PNG/AVI·1000캐시 회귀 |
| Face 정적 PNG/AVI | VERIFIED: 실제 YuNet 얼굴1개, PNG+디코딩25 AVI프레임 중심 검열 픽셀; uniqueSource1 |
| 좌표 재생/만료/전역 보호 | VERIFIED headless: 2109회 커버리지 부족273→0, fresh 빈 화면 만료/오래된 증거 전체 보호 유지 |
| 실제 캡처·UI | PARTIAL: WGC Face2109분석/225양성/최대5개/오류0, UI 선택/Start/Stop 접근키 |
| 브라우저 사용성·물리 마스크 | NOT RUN/미검증: URL 확인 문제로 UI 자동화 중단, 사용자도 가림을 확인하지 못함 |
| 수정 후 브라우저 재시험 | NOT RUN: 국소 불확실성/만료 수정은 비대화형 검증만 |
| 물리 다중 화면 | BLOCKED: 연결1개 |

이하 과거 결과:

# 최신 실행 결과 — M1–M2 후속 세션 (2026-10-22)

전체 근거/재현 명령: [ACCEPTANCE-20261022.md](ACCEPTANCE-20261022.md).

| 실행 | 결과 |
|---|---|
| `scripts/Test-Headless.ps1` | VERIFIED:42/42 Core, Release0오류/0경고(RunAnalyzers=false), 기존 보호/WPF 프로브 및 새 recorder/cache 프로브 PASS |
| `RecordingProbe -- models artifacts/live-test/crop3.png` | VERIFIED: 실제 모델 마스크2개, PNG+디코딩24 AVI프레임,1.600초/벽시계약1.61초, uniqueSources1/FPS고정 |
| 녹화 차단·동시 Stop 회귀 | VERIFIED:4.3초 초과 차단 중 조기 완료 없음, 동일 Stop Task, 해제 후 idx1완료 |
| 세그멘테이션 캐시 회귀 | VERIFIED: 창을 표시하지 않고1000회 마스크 교체/캐시1개 이하/Clear0 |
| `ProtectionProbe --soak models artifacts/live-test/crop3.png 300` | VERIFIED:300.1초2521회분석/252합성, 자원 증가 기준 PASS(화면/창 없음) |
| `LivePipelineProbe --interactive ... --initialization-cancel` | VERIFIED: 제어된 초기화 취소,2×8초 캡처·좌표 계측, 최대0.484px, 종료 overlay0 |
| 기존952380a 실제 UI Start/전역키 | VERIFIED:Start2회, 실제 Ctrl+Shift+F10으로Idle복귀. 다른 앱 포커스는 미실행 |
| 새 전용 뷰어 | VERIFIED: 실제 HWND/제목이 창 목록에 나타남. 기본 호출은opt-in거부 |
| 수정한 UI Cancel/Stop 및 다른 앱 포커스 비상키 | NOT RUN: 허용 시간 내 키보드 탐색 미완료. API 취소와 구분 |
| 물리 표시 픽셀/프레임별 노출, 실제 데스크톱 AVI, 장시간 live soak | PARTIAL/NOT RUN: 상세문서 참조 |
| 물리 복수 모니터 | BLOCKED: 실제 연결 화면1개, 기존 합성 테스트와 구분 |

실패도 보존: 중간 전체 회귀는 실행 중 soak 프로세스의 DLL 잠금으로 MSB3027/MSB3021 실패.
soak 정상 종료 후 재실행 PASS. 첫 UI의 resources.jsonl은 계측 도입 전이므로 존재하지 않으며 0사용량 증거가 아니다.
화면 이미지는 외부 전송하지 않았다. 새 PNG/AVI는 정적 로컬 fixture의 테스트 산출물이며 ignored artifacts에만 보관했다.

아래는 이전 세션의 실행 기록이다.

# 이전 실행 결과 — 2026-10-22 복구 세션

사용자 허용 후 `tools/LivePipelineProbe`로 실제 WGC→ONNX→추적→오버레이를 실행했다.
최종 실행 PASS(exit 0): 두 번의 8초 구간, 캡처 119/118프레임, 분석 15/16회,
실제 검출 상태에서 부분 마스크가 보장된 샘플 6개. 전체 16개 샘플은 부분 마스크 또는 fail-closed
보호 영역이 존재했다. 두 구간 모두 캡처 제외 True, 오버레이 창 1개, 처리 오류 False였다.
각 구간은 fail-closed 상태에서 Stop해 running=False/오버레이 0개가 됐다. 종료 후 관련 프로세스가 없었다.
1초 샘플링이므로 모든 표시 프레임의 무노출을 입증하지 않는다.
첫 실행에서 이동 중 마스크 0개/트랙 유지가 재현되어 FAIL로 기록하고 CoverageGuard로 보완했다.
보완 후 42/42 headless PASS. 중간 테스트의 세션간 assertion 재사용 오류도 수정했다.
최신 실제 실행의 모든 샘플은 `artifacts/live-pipeline-finalcheck.log`에 보존한다.
메인 UI 자동 클릭은 geometry 오류로 FAIL, 직접 엔진 API 실행과 구분한다.

패키징 PASS: x64 self-contained publish, 4개 모델 SHA-256 확인, 내부 SHA256SUMS와 ZIP 외부 해시 생성.
첫 preview 폴더는 약 238.2 MB / ZIP 110.5 MB였으며 이후 F12→F10 수정으로 대체된다.
최종 사용자 전달 경로는 `artifacts/VeilDesk-recovery-20261022-stabilized/` 및 동일 이름의 `.zip`, `.zip.sha256`다.
패키지 앱 실행/설치와 실제 비상키는 NOT RUN이며 final ZIP의 무결성 검증 결과는 외부 verification 파일로 남긴다.
전체 분석기를 켠 publish는 기존 CA 스타일/성능/PInvoke 경고를 출력한다. 0경고 기록은 분석기를 끈 headless 빌드에 한정한다.

패키징 준비 중 첫 실행은 PowerShell 단일 문자열 splat 오류(MSB1001)로 실패해 배열 타입으로 수정했다.
그 다음 실행은 x64 런타임 assets 미복원(NETSDK1047)으로 실패했다.
`dotnet restore src/VeilDesk.App/VeilDesk.App.csproj -r win-x64 --source "$env:USERPROFILE\.nuget\packages"`
로 **로컬 캐시만 사용한 복원 PASS**. 실패한 출력 폴더와 이전 배포본은 삭제하지 않았다.

아래 최신 절만 이번 세션의 실행 증거다. 세부 절차는 RECOVERY-20261022.md 참조.

| 실행 | 결과 |
|---|---|
| 최초 격리 환경 dotnet test | FAIL: testhost IPC 연결이 90초 후 중단. 테스트 assertion 실패와 구분한다. |
| 로컬 기존 테스트 재실행 | PASS 26/26 |
| `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Headless.ps1` | PASS: 42/42, Release 0 오류/0 경고(`RunAnalyzers=false`), 보호 프로브, WPF Image 초기화 |
| `dotnet run --project tools/ProtectionProbe/ProtectionProbe.csproj -c Release --no-build -- models artifacts/live-test/david.jpg` | PASS: 실제 CPU ONNX 검출→추적→해부학적 마스크→픽셀 합성. 사람 1, 해부학적 검출 9, 검열 영역 2, 변경 픽셀 128,082 |
| 추론 정지 중 보호 판정 및 Stop | PASS: 생산 VisionPipeline에 제어 가능한 모델 주입. 판정 4.54 ms, 전체 보호 영역 존재, Stop 후 영역 0, 늦은 완료 후에도 영역 0 |
| 전체 보호 렌더러 | PASS: 음수 모니터 원점, 150% DPI 기술값, CornerRadius=100, Opacity=0.1에서 모서리 불투명 픽셀 확인. 실제 창 검증과 구분 |
| 옵션 없는 로컬 legacy viewer/run-live 호출 | PASS: 명시적 opt-in 요구 오류로 종료, 창 생성 경로에 진입하지 않음 |
| 실제 Ctrl+C, 전역 비상키, 메인 UI 버튼 수명 | NOT RUN |
| 직접 엔진 API Start/Stop 및 fail-closed 중 Stop | PASS: 실제 오버레이 창 1→0 두 번, 프로세스 잔존 없음 |
| 현재 데스크톱 검출→오버레이 창 및 캡처 제외 | PASS: 숫자·상태·창 수명. 물리 픽셀 정렬 PARTIAL |
| 실제 녹화·복수 물리 모니터 | NOT RUN |

새 회귀 16개는 복구 dwell, 중복 성공, 지연 추론, 캡처 누락, 결과 순서, 실패 교대,
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
