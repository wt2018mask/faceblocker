# 최신 Face 회귀 감사 — 2026-10-23

[FACE-RECOVERY-20261023.md](FACE-RECOVERY-20261023.md) 참조. Face preset 이름·build identity·명확한 실행 스크립트 추가. 실제 사용자 EXE 및 zero censorship 원인 미확정, 브라우저 회복 미검증. 아래는 이전 이력.

# 최신 — 스크롤 추적·과잉 가림 수정 (2026-10-22)

자세한 근거: [FACE-SCROLL-FIX-20261022.md](FACE-SCROLL-FIX-20261022.md). 기준 `1b488be` 이후, 브랜치 `codex/protection-recovery`.

- **VERIFIED(기록 로그):** 인스타그램 기록 로그에서 795×977px 저신뢰 후보(0.361)가
  **확립된 186×238px 실얼굴 트랙을 탈취**해 화면 36.4%짜리 가림을 만든 것이
  확인됐다. 같은 형태의 622×708px 후보도 있었다. 단순히 "크기 때문에 무효"로
  판정하지 않고, 랜드마크 모순·포함 중복·연관 비율이라는 **구체적 증거**로만 거부한다.
- **VERIFIED(공식 구현 대조):** YuNet 상자 디코딩은 OpenCV `FaceDetectorYN`과
  수치까지 일치했다(오류 아님). **누락됐던 랜드마크**를 공식 수식
  `(kps+cell)*stride`로 복구해 후보 검증에 사용한다.
- **FIXED:** 후보 게이트(랜드마크 모순·coarse duplicate 거부, 신뢰도 상위 후보는 보호),
  트래커 연관 면적 보호, 여백/예측 sweep의 이중 확대 제거, 2단계 크기 초과 가림 차단.
- **FIXED:** 내용 이동 측정(3단 피라미드 매칭, 4×3 영역별) 도입. 미검출 얼굴이
  내용과 함께 움직이고, 화면을 벗어난 얼굴 트랙은 즉시 정리된다.
  **스크롤 잔상 415~1328ms → 83ms(합성 fixture), 2초 유지 정책 자체는 유지.**
- **VERIFIED(측정):** 실제 기록 로그 재생에서 최대 마스크 **78.4% → 9.8%**,
  평균 **3.52% → 1.25%**, coarse duplicate 25건 거부.
  합성 스크롤 12시퀀스에서 얼굴 커버리지 11/12 = 1.00(fast-scroll만 0.86).
- **VERIFIED(회귀):** Core **70/70 PASS**(기존 53 + 신규 17), Release 0경고/0오류,
  기존 headless probe 전체 PASS. 내용 이동 추정 비용 2560×1600당 P50 3.7ms.
- **PARTIAL / 미해결:** 실제 인스타그램 브라우저 인수는 **아직 PARTIAL**이다.
  기록 로그에 랜드마크가 없어 재생으로 판정할 수 없었던 잔여 최대 마스크 9.8%가
  남아 있다. 실제 화면 관찰은 새 사용자 승인과 재시험이 필요하다.
- **NOT RUN/미확인:** 클릭 스루·타이핑·탭/창 크기·영상·Chrome 포커스 F10·Stop 정리,
  실제 초기화 취소, 장시간 live, 움직이는 브라우저 export, 물리 다중 모니터.
- **BLOCKED:** 릴리스/M10. 브라우저 인수 완료 전에는 새 패키지를 만들지 않는다.
  기존 ZIP은 Face 이전 그대로다.

다음은 새 허용 하의 실제 브라우저 재시험(스크롤·큰 얼굴·잔상·click-through·F10)이다.

---

# 최신 — 실제 브라우저 인수 미완료 (2026-10-22)

최신 상세: [BROWSER-GUIDED-20261022.md](BROWSER-GUIDED-20261022.md). 기준 `bee3a85` 이후 Face 속도·예측·국소 여백과 선택 표시를 개선했다. 아래 이전 기록보다 이 절을 우선한다.

- **VERIFIED(사용자):** 실제 Chrome 얼굴의 검은 가림, 저장된 Face 표시와 재시작 후 실제 가림, VeilDesk Stop 버튼 사용.
- **VERIFIED(자동):** Core53/53, Release0경고/0오류(분석기 비활성화), 보호·녹화·실제 YuNet 회귀. 1,068개 기록 기하의 관측 상자 포함 실패0; 최대 단일 마스크78.4%→15.7%는 고정 기록 재계산이다.
- **PARTIAL / 미해결:** 수정 후 사용자 시험에서도 인스타그램 스크롤 뒤 큰/엉뚱한 가림이 남고 약2초 안에 사라진다. 마지막 로그에는 원시795×977px 후보→1199×1242px 가림도 있다. 후처리 과도 확장은 줄였지만 실제 사이트 문제 해결을 선언하지 않는다.
- **NOT RUN/미확인:** 명시적 click-through/입력·탭·창크기·영상 결과, Chrome 포커스 F10, 실제 초기화 취소, 장시간 live, 움직이는 브라우저 export. 짧은 사용자가이드 질문만으로 통과시키지 않는다.
- **BLOCKED:** 브라우저 인수/릴리스/M10. 물리 다중 모니터는 장치1개로 미검증. 기존 ZIP은Face 이전 그대로다.
- 허용된10분·5분·3분 시험은 종료/소유 프로세스 정리 완료. 새 허용 전 대화형 실행 금지. 다음은 큰 원시 후보 재검증과 영상 근거의 스크롤 추적, licensed 회귀가 우선이다. 2초 유지·CoverageGuard·국소 불확실 가림·전역 Fail-Closed 보존. 프레임별 무노출 주장 없음.

아래는 이전 이력이다.

# 최신 — Face 브라우저 마일스톤 (2026-10-22)

실제 YuNet Face 선택과 desktop 파이프라인을 구현했다. **브라우저 사용성 인수는 PARTIAL, 출시 준비 완료 아님.** 근거: [FACE-BROWSER-20261022.md](FACE-BROWSER-20261022.md).

- VERIFIED: MIT YuNet, 공식 가중치 해시/입출력, Face-only 독립 모델 로드·실행, 기존 해부학적 기능 유지.
- VERIFIED: Core46/46, Release, 실제 모델 fixture6/6 및 이동20/20, 로컬 VP8 표본8/8, 보호·녹화·캐시 회귀.
- VERIFIED: 실제 YuNet 정적 PNG/AVI25프레임, 기존 실제 해부학적2개 보존. 좌표 재생2109회 커버리지 부족273→0.
- PARTIAL: 허용된5분 Chrome 시험, 실제 Face WGC 분석2109회/양성225회/최대5개/오류0, UI Face 선택·Start·Stop 접근키. 물리 표시를 사용자가 확인하지 못했다.
- FIXED(headless): 낮은 신뢰도 후보에 확대 불투명 국소 가림; 만료된 Face의 전역 보호 요구 제거. 엄격 모드 선택 가능, 기존 global Fail-Closed 유지. 수정 후 대화형 재시험 NOT RUN.
- BLOCKED(UI 자동화): Computer Use의 Chrome URL 확인 불확실성으로 중단. 추가 입력 없음, 소유 시험 프로세스 정리 완료.
- NOT RUN: 브라우저 스크롤·영상·click-through·탭/창 이동·브라우저 포커스 비상키·실제 Cancel·장시간 live·수정 후 얼굴 물리 정렬.
- 다음은 새 허용하의 수정 후 브라우저 시연이다. 기존 ZIP952380a는 이번 Face 기능을 포함하지 않는다. M10 진행 안 함.

아래 기록은 이전 검증 이력이다.

# 현재 상태 — M1–M2 후속 검증 (2026-10-22)

브랜치 `codex/protection-recovery`, 기준 `952380a` 이후 작업.
자세한 근거: [ACCEPTANCE-20261022.md](ACCEPTANCE-20261022.md).

- **VERIFIED:** 실제 검출 마스크의 WPF→물리 좌표 오차 최대0.484px(단일 2560×1600/150% DPI), 네이티브 창 bounds 일치.
- **VERIFIED:** 제어된 실제 엔진 초기화 중 취소, 로드 완료 전 덮개 제거, 2회 Start/Stop 후 overlay0.
- **VERIFIED:** Core42/42, Release 빌드, 1000회 세그멘테이션 캐시 교체, 녹화의 동시 Stop/4초 초과 작업 수명 회귀.
- **VERIFIED:** 실제 정적 ONNX 마스크2개가 PNG와 디코딩 AVI24프레임에 포함됨(데스크톱 녹화와 구분).
- **VERIFIED:** 화면 없는 300.1초 실제 모델/추적/합성 soak2521회/252합성. post-warmup private −2.7MiB, handles−14.
- **PARTIAL:** 기존952380a UI의 실제 Start와 Ctrl+Shift+F10은 실행. 수정된 UI의 Cancel/Stop 버튼, 다른 창 포커스 비상키는 미완료.
- **PARTIAL:** 실제 표시 픽셀의 육안/프레임별 가림, 장시간 데스크톱 보호, 움직이는 실제 캡처의 내보내기.
- **BLOCKED:** 물리 복수 모니터(연결 화면1개). 기존 합성 좌표 회귀와 구분.

원인 없는 최적화는 하지 않았다. UI75초 자원 수치에는 초기화/입력 자동화가 포함되며, 별도 idle CPU 프로파일링이 필요하다.
기존 ZIP은952380a이며 이번 소스 변경을 포함하지 않는다. M1–M2 전체 게이트는 PARTIAL, M10은 진행하지 않았다.
다음은 사용자 입회 하의 Cancel/Stop/다른 창 포커스 비상키와 실제 가림 확인이다.

아래는 이전 복구 세션의 역사 기록이다.

# 이전 상태 — 2026-10-22 복구 세션

기존 작업 체크포인트: `9f0dd2e`. **생산 준비 완료 아님.**
복구 브랜치: `codex/protection-recovery`. 패키지 전달 경로: `artifacts/VeilDesk-recovery-20261022-stabilized.zip`
(최종 해시는 같은 경로의 `.sha256`, 검증 결과는 `.verification.txt`에서 확인).
상세 근거와 재현 절차: [RECOVERY-20261022.md](RECOVERY-20261022.md).

- PASS: 기존 26개 + 새 보호 회귀 16개 = 42개 테스트.
- PASS: Release 빌드(분석기 비활성화 실행), 0 경고/0 오류.
- PASS: 실제 VisionPipeline의 추론 정지 중 전체 보호와 Stop 후 늦은 결과 무시.
- PASS: 전체 보호 불투명 모서리, 실제 로컬 ONNX→추적→해부학적 마스크 픽셀 합성(정적 이미지).
- PASS: WPF Image Source/Stretch 초기화, 옵션 없는 뷰어/라이브 진입점의 실행 거부.
- 사용자 허용 후 실제 엔진 API로 제한 시간 라이브 캡처·검출·오버레이 및 두 번의 Stop을 실행했다. 물리 정렬과 메인 UI/전역키 검증은 남아 있다.
- 최종 라이브 16개 샘플에서 영역 0개 없음. 119/118 캡처 프레임, fail-closed 중 Stop 2회 후 오버레이 0개. 모든 표시 프레임의 무노출 증거는 아니다.
- M0/M1 PARTIAL, M2 정적/실제 파이프라인 API 실행 PASS 및 물리 화면 검증 PARTIAL. M3–M10 전체 완료를 주장하지 않는다.
- 기존 자동 테스트 PID 18800만 식별 종료. Herdr/OpenCode 기록과 세션은 보존.

아래는 **이전 에이전트의 역사 기록**이다. 과거의 23개 테스트, 라이브 실행, 잠긴 세션,
배포 결과를 현재 세션의 새 검증으로 읽지 말 것. 이번 세션의 수치는 위와 TEST_RESULTS의 최신 절이 우선한다.

# Historical snapshot

**Snapshot:** 2026-10-22 (Asia/Seoul)
**Project root:** `C:\Users\<you>\source\faceblocker`
**Version:** 1.0.0
**Platform:** Windows 10/11 x64, .NET 8, WPF

This file is the short operational snapshot. Deeper records live in
`TEST_RESULTS.md`, `BENCHMARKS.md`, `ARCHITECTURE.md` and `HANDOFF.md`.

## Objective

Deliver a genuinely working vertical slice of a privacy-first, fully local
real-time censorship application: real capture, real local CV inference,
multi-subject tracking, correctly positioned censorship overlays, a usable
settings UI, tests, measured performance, and reproducible packaging.

## Implemented

### Core (`VeilDesk.Core`)

- Pixel geometry, monitor descriptors, detection records and body regions.
- NudeNet and YOLO output decoders, NMS, letterbox transforms.
- Segmentation mask decoding, thresholding and largest-component cleanup.
- Scene-change detector and adaptive inference scheduler.
- Latency tracker (P50/P95).
- Subject tracker with velocity prediction, person evidence association,
  track persistence, masks and whole-person regions.
- Censor composer: enabled regions, confidence, gender/whole-body rules,
  fail-closed, motion padding, prediction horizon.
- Settings model, clamping, atomic save with backup and corrupt-file
  quarantine.
- Session history, statistics, streaks and achievements.
- `.veilpack` validator with size/entry caps, extension allowlist and
  path-traversal rejection.
- Pooled `VideoFrame` and frame mailbox.
- Self-contained MJPEG AVI writer with RIFF headers, `idx1` index and bounded
  streaming.

### Inference (`VeilDesk.Inference`)

- Three ONNX Runtime sessions: NudeNet 320n, YOLOv8n 320/480, selfie
  segmentation.
- DirectML-first with CPU fallback; CPU is the default because DirectML cold
  start is unacceptable on the tested iGPU.
- Pure C# bilinear BGRA→RGB NCHW preprocessing (no OpenCV).
- Per-person segmentation crops, whole-person silhouettes, serialized runs.
- Per-monitor pipeline with separate person/anatomical cadences, adaptive
  scene skip, fail-closed supervision, region generation and timing.

### App (`VeilDesk.App`)

- Windows Graphics Capture with a dedicated STA dispatcher thread, persistent
  D3D11 device, per-monitor sessions and 15 fps readback.
- Transparent, click-through, topmost WPF overlays per monitor; physical
  pixels converted to DIPs using the monitor DPI; ten censor styles.
- Overlay windows excluded from WGC (`WDA_EXCLUDEFROMCAPTURE`).
- `VeilDeskEngine` orchestration: Start/Stop, shared model set, per-monitor
  runners, adaptive analysis, 30 Hz overlay refresh, fail-closed, sessions,
  screenshot API and settings updates.
- Ten-page dark UI: Dashboard, Detection, Censoring, Displays, Performance,
  Export, Packs, Sessions, Settings, About.
- Screenshot export compositing the same censor renderer; optional scaling.
- Video recorder loop and MJPEG AVI writer; capped at 1280×800, 15 fps.
- Built-in pack installation on first launch with an install marker; pack apply
  preserves unrelated settings sections.
- Start-minimized support; close-to-tray is intentionally disabled.

### Tooling / repository

- `tools/WgcProbe`, `tools/CaptureProbe`, `tools/VisionProbe`,
  `tools/AviProbe`.
- `scripts/fetch-models.ps1` with pinned SHA-256 checks.
- `scripts/package.ps1`: self-contained x64 publish, model/hash verification,
  docs/packs/models copy, `SHA256SUMS.txt`, ZIP.
- Models manifest, third-party notices, AGPL licence.
- 23 Core tests, including AVI container parsing and built-in pack validation.

## Verified evidence

- **Capture:** real 2560×1600 BGRA frames, 13–19 fps, 100% non-black on an
  unlocked desktop; start 360–636 ms, stop ~1 s.
- **Inference:** CPU model load ~281 ms; steady full pass ~54–67 ms; three
  correct person boxes on `bus.jpg`; NudeNet `FEMALE_BREAST_EXPOSED` on the
  David test image, visually aligned.
- **Live engine:** CPU provider, ~12–14 capture fps, analysis P50 ~28–43 ms;
  overlay running and excluded from capture; Start/Stop worked.
- **Live session log:** 97.1 s, 103 detection events, peak 11 tracks.
- **Screenshot export path:** real WGC frame written to
  `C:\Users\<you>\Pictures\VeilDesk\VeilDesk-20261022-101512-Generic-PnP-Monitor.png`.
  Visual inspection showed no censor region, so this file proves capture +
  PNG encoding, not compositing during a live detection.
- **Screenshot censor compositing:** `tools/ExportProbe` passed with a
  synthetic 640×360 frame and a known region: center pixel became `#0B0D12`,
  11.46% of pixels changed, and outside pixels were unchanged.
- **Video container:** 30 real JPEG frames → 884,818-byte AVI; Windows Media
  Foundation opened it as 640×360, 2.000 s, with `idx1=yes`, and decoded a
  96.4% non-black poster.
- **Tests:** 23/23 passed.
- **UI:** all ten pages rendered at 150% DPI via `PrintWindow`.
- **Package:** self-contained Release publish 238.2 MB / 494 files; ZIP 110.5 MB;
  final SHA-256 in `PACKAGE-SHA256.txt`; published app launched with a
  throw-away data root and installed all four built-in packs.

## Active / incomplete

- Live visual confirmation of censor overlay placement and tracking on an
  unlocked desktop, including a moving target.
- End-to-end `VideoRecorder` run from the app (start/stop, frame count, file
  playback, measured recording throughput).
- Multi-monitor validation with two physical displays (hardware not available).
- Clean-machine / VM install validation (only the build host has been used).
- Installer and code signing (portable ZIP only).

## Blocked

The Windows session is locked. A fresh `CaptureProbe` run on 2026-10-22
returned a 2560×1600 frame with `nonBlack=0.0%`, showing only the secure-desktop
spinner. Live anatomical detection, overlay visuals, recording and
screenshot-with-detection cannot be validated until the user unlocks.
Do **not** set `VEILDESK_DISABLE_CAPTURE_EXCLUSION=1` for live tests: with
fail-closed enabled, the engine captures its own cover and feeds it back.

## Next actions

1. Unlock the session; start protection with overlay exclusion enabled.
2. Show a controlled moving test subject; verify track IDs, motion padding and
   overlay alignment on the physical display; capture evidence.
3. Run the in-app recorder for ~20 s; verify AVI header/frame count and decode
   with `AviProbe`/Media Foundation; record measured throughput.
4. Test with a second monitor if available; verify per-monitor DPI and negative
   virtual-desktop coordinates.
5. Optionally validate the ZIP on a clean VM; no installer/signing is provided.
7. Commit the final docs and tag/record the artifact hash.
