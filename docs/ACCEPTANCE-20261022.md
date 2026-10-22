# M1–M2 후속 인수 검증 — 2026-10-22 (Asia/Seoul)

기준: `codex/protection-recovery`, `952380a`. 기존 검증을 초기화하지 않고 후속 작업을 추가했다.
외부 화면 전송 없음. 기록 파일/PNG/AVI는 저장소의 ignored `artifacts/` 아래에만 남겼다.
새 릴리스 패키지는 만들지 않았다. M1–M2의 미완료 게이트 때문에 M10을 진행하지 않는다.

## 인수 상태

| 항목 | 판정과 증거 |
|---|---|
| 실제 검출 영역의 물리 좌표 | VERIFIED: 연결 화면 2560×1600, 150% DPI. 실제 HWND bounds와 모니터 bounds 일치. 실제 WPF `PointToScreen` 모서리와 검출에서 생성한 예상 영역의 최대 오차 0.484 px. 1px 기준 통과. |
| 실제 화면에서 해부학적 위치/가림의 육안 확인 | PARTIAL: 좌표 계측과 별개다. 외부 카메라/표시 프레임 증거와 사용자 관찰 응답은 확보하지 못했다. |
| 실제 UI Start | VERIFIED: 기존 `952380a` 패키지의 Hero 버튼을 키보드로 두 번 실행, 실제 WGC/CPU/캡처 제외 상태 확인. |
| Ctrl+Shift+F10 | PARTIAL: 기존 패키지의 실제 키 입력 후 Protecting→Stopped 및 Idle 복귀 확인. 다른 앱 포커스 상태와 수정 후 전체 UI 경로는 NOT RUN. |
| 초기화 중 취소 | VERIFIED(실제 엔진 API): 초기 모델 로더를 제어해 로드 완료 전 Stop, 덮개 즉시 제거, 시작 작업 취소, 캡처 시작 전 정리. 로더는 이후 실제 로컬 모델을 로드/해제했다. UI 취소 버튼 클릭은 NOT RUN. |
| 실제 UI Stop 버튼 | NOT RUN: 시간 제한 내 키보드 탐색이 완료되지 않았다. 창 닫기/엔진 API Stop과 구분한다. |
| 상태/창 수명 | VERIFIED(제한된 실행): 2×8초 실제 캡처, 캡처 제외 유지, 각 Stop 후 overlay 0개. 1초 상태 표본은 프레임별 무노출 증거가 아니다. |
| 지속/자원 시험 | VERIFIED(비대화형 한정): 실제 모델·추적 300.1초/2521회, 합성 252회. 화면 캡처/오버레이 장시간 시험은 NOT RUN. |
| 실제 모델 마스크 PNG·AVI | VERIFIED(정적 로컬 이미지): 실제 ONNX 해부학적 검출 2개로 생성한 마스크가 PNG와 AVI 디코딩 24프레임에 포함됨. 실제 데스크톱의 움직이는 검출 녹화는 NOT RUN. |
| 복수 모니터 | BLOCKED(물리): Windows 연결 표시 장치 1개. 기존 합성 음수 원점/다른 DPI 회귀와 구분한다. |

## 실제 대화형 실행

사용자에게 먼저 2분 허용을 받고 실행했다. 스크린샷을 전송하지 않는 접근성 텍스트와 키보드만 사용했다.
포인터 입력의 이전 geometry 한계는 키보드로 우회해 기존 UI의 실제 Start를 실행했다.

- 첫 UI 로그: `artifacts/interactive-5804d1afe8a34af1825898983f75b45c/logs/app.log`.
- UI Start 19:01:23/19:02:10, 실제 시작 19:01:24/19:02:11.
- Ctrl+Shift+F10 입력 후 19:01:37 중지 요청과 Protecting→Stopped, 접근성 텍스트에서 Idle 확인.
- 19:02:26 시간 제한으로 창 닫기/중지. 관련 사용자/Herdr 프로세스는 종료하지 않았다.
- 첫 뷰어는 창 목록에 보이지 않았고 검출도 없었다. 이미지 로드 초기화는 PASS/오류 로그는 비어 있었다.
  숨김 PowerShell의 초기 창 표시 설정 영향을 피하도록 console 없는 `tools/SubjectViewer` WinExe로 교체했다.
  원래 표시 실패의 OS 내부 원인은 확정하지 않는다. 새 실행에서 뷰어 HWND/제목이 실제 창 목록에 나타났다.

추가 2분 허용을 받은 뒤 다음 두 단계를 수행했다.

1. `LivePipelineProbe --initialization-cancel`: 강제 모델 초기화 중 취소 + 2×8초 실제 파이프라인/좌표 계측.
   로그 `artifacts/acceptance-placement-20261022.log`, 각 실행 종료 후 overlay 0.
   실제 검출 마스크 측정 0.159–0.484px, 전체 보호의 모서리 오차 0.
   이 결과는 모델이 해부학적 위치를 정확히 맞췄는지 또는 표시된 모든 픽셀을 검열했는지의 독립 증거가 아니다.
2. 새 UI/뷰어 75초: 두 창 존재 확인. 키보드 포커스 이동 후 시간 제한이 끝나 Start에 진입하지 못했다.
   이 실행의 Stop 버튼/다른 앱 포커스 비상키/사용자 정렬 확인을 성공으로 기록하지 않는다.
   로그 루트 `artifacts/interactive-0c2720da42004f25ac7a9bf87e6d222b/`.

## 구현한 수정

- UI 초기화 동안 `Cancel initialization` 버튼을 활성화하고 시작 취소를 일반 오류 MessageBox와 구분했다.
  Stop 진행 중 새 Start/상태 타이머가 버튼을 다시 활성화하는 경쟁을 막았다. 컴파일 PASS, 실제 버튼 입력 검증은 남아 있다.
- `VeilDeskEngine`에 모델 로더 주입 경계 추가. 기본 로더는 동일한 `VisionModels.Load`이며 제품 설정/지연/원격 추론 옵션을 추가하지 않았다.
- `VideoRecorder`의 writer 수명을 녹화 루프가 소유한다. Stop 4초 뒤에도 작업 중인 writer를 Dispose하지 않는다.
  동시/반복 Stop은 같은 Task를 기다린다. dispatcher 합성은 취소 가능한 요청으로 제출한다.
- `SourceFrames`는 같은 캡처 프레임의 반복 사용을 새 캡처로 세지 않는다. 종료 후 FPS 계산 시간을 고정했다.
- AVI 타임라인 filler는 취소를 확인하고 실제 WriteFrame 성공 후에만 프레임 수를 증가시킨다.
- 기본 PNG/AVI 이름에 밀리초와 GUID를 넣어 빠른 연속 내보내기가 이전 파일을 덮어쓰지 않게 했다.
- 세그멘테이션 캐시는 현재 영역이 참조하는 마스크만 보존한다. 색 변경과 Clear에서도 캐시를 지운다.
  과거 코드에는 마스크 프레임 교체마다 참조가 누적되는 구조가 있었다. 숨겨진 WPF 컴포넌트에서 1000회 교체/캐시 ≤1/Clear=0 회귀 PASS.
- 실제 좌표 계측은 테스트/진단용이며 화면 데이터나 이미지 픽셀을 로그에 넣지 않는다.

## 실행한 비대화형 회귀

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Headless.ps1
dotnet run --project tools/RecordingProbe -c Release -- models artifacts/live-test/crop3.png
dotnet tools/ProtectionProbe/bin/Release/net8.0-windows10.0.19041.0/ProtectionProbe.dll --soak models artifacts/live-test/crop3.png 300
dotnet tools/LivePipelineProbe/bin/Release/net8.0-windows10.0.19041.0/LivePipelineProbe.dll --inventory
```

- Core 42/42 PASS(새 실행 이유: 수명·UI·렌더러 변경 이후 회귀).
- Release 0 오류/0 경고(`RunAnalyzers=false` 조건).
- 기존 보호 프로브 PASS. 새 녹화/캐시 프로브를 `Test-Headless.ps1`에 추가했다.
- 녹화 프로브: 4.3초 이상 입력 차단 중 동시 Stop의 조기 완료 없음, 루프 해제 후 AVI index 완료.
- 같은 소스 프레임으로 synthetic 및 실제 모델 clip 각각 24프레임/1.600초, 벽시계 약1.61초.
  각 JPEG를 디코딩해 실제 마스크 중심의 검열색을 확인했다(JPEG 허용오차18/255, PNG 정확 일치).
- uniqueSources=1, 종료 후 source FPS 불변, PNG 기본 파일명 충돌 없음.
- 대화형 도구를 옵션 없이 실행하면 종료 코드2로 끝나고 창을 열지 않는 것을 확인했다.

실제 모델 내보내기: `artifacts/recording-probe-9c7e0f4fb9f247248a7e0531375972ef/actual-model/`.
최종 기본 회귀: `artifacts/recording-probe-63c0444ed94243cbb0e09dbfa6976490/`.

## 지속 시험과 한계

5분 soak는 desktop capture 없는 고정 로컬 이미지, CPU/2 inference threads, 목표 10회/초,
약1초마다 export compositor 실행, 20회 준비 뒤 측정이다.
준비 전후 비교가 섞이지 않게 측정 시작/끝에서 full GC를 수행했다.

- 300.1초, 2521회 분석, 252회 합성, fixture 트랙 3개 유지.
- private bytes 차이 −2.7 MiB, 핸들 −14, 스레드 −5. 표본 private 대략116.6–125.1 MiB.
- 사전 판정 한계: post-warmup private 증가128 MiB 미만, 핸들 증가≤16, 스레드 증가≤4, 추론 오류 없음, 트랙≤32. PASS.
- 프로세스 CPU/벽시계/`Environment.ProcessorCount`로 정규화한 CPU 33.4%.
- 측정 분석 P50 95.7 / P95 153.1 / P99 196.3 ms. 빌드를 병행한 환경이므로 깨끗한 성능 벤치마크로 일반화하지 않는다.
- 화면 capture FPS/end-to-end 지연/노출 시간/GPU 사용률/열 안정성/시간 단위 live soak는 미측정이다.

새 UI 75초 자원 기록은 76개 표본: private11.4→71.6MiB, handles204→449, CPU time 약74.47초.
여기에는 초기 WPF 준비와 입력 자동화가 포함되며 보호가 시작되지 않았다. 메모리 누수 또는 live 안정성
통과로 해석하지 않는다. 실제 idle CPU/장기 UI 부하는 별도 프로파일링해야 한다.

중간 전체 회귀는 실행 중 soak 프로세스가 출력 DLL을 잠가 MSB3027/MSB3021로 실패했다.
관련 프로세스를 죽이지 않고 soak가 정상 끝난 후 같은 회귀를 재실행해 PASS했다.
첫 UI 실행에는 resources.jsonl 계측을 아직 추가하지 않아 해당 파일은 없다. 파일 부재를 0 사용량으로 해석하지 않는다.

## 다음 실행

M1–M2 전체 게이트는 아직 PARTIAL이다. 다음은 사용자 입회 하의 짧은 메인 UI 시험이다.
실행 전 별도 명시적 허용을 받고, 기존 앱은 사용자 UI로 닫는다.

```powershell
dotnet build tools/SubjectViewer -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-InteractiveValidation.ps1 `
  -Interactive -ImagePath "C:\explicit\test-image.png" `
  -AppPath "C:\explicit\VeilDesk.App.exe" -DurationSeconds 120
```

사용자가 Start→초기화 취소→다시 Start→Stop 버튼을 직접 누르고, 뷰어에 포커스한 상태에서
Ctrl+Shift+F10을 눌러 Idle/창 제거를 확인하는 방법이 가장 직접적이다.
원하면 좌표 측정은 `LivePipelineProbe --interactive <이미지> <새 데이터 루트> --initialization-cancel`로 재현한다.
이 프로브는 창과 실제 전체 보호를 실행하므로 headless 명령처럼 자동 실행하지 않는다.
기존 ZIP은 `952380a` 버전이며 이번 소스 변경을 포함하지 않는다.
