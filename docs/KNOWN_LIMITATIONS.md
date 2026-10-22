# 최신 검증 한계 — M1–M2 후속 세션

- 실제 단일 화면의 WPF→물리 좌표는 최대0.484px로 계측했다. 사용자 육안 확인/모든 표시 프레임의 가림 증거는 별개이며 미확보다.
- 실제 엔진 초기화 취소는 실행했다. 수정된 UI Cancel/Stop 버튼과 다른 창 포커스에서의 비상키는 미실행이다.
- 기존952380a UI Start와 Ctrl+Shift+F10 입력은 실행됐다. 새 UI 경로 전체를 자동으로 검증한 것으로 간주하지 않는다.
- 5분 지속 시험은 고정 이미지 모델·추적·합성이며 WGC/실제 오버레이 장시간 시험이 아니다.
- 실제 검출 마스크의 PNG/AVI는 정적 로컬 이미지로 검증했다. 움직이는 데스크톱 검출 녹화는 미실행이다.
- 별도 idle UI CPU 프로파일링, 프레임별 노출 시간, 두 번째 물리 모니터는 남아 있다.
- 세그멘테이션 캐시 보존/recorder writer 수명은 보완했으나 모든 장시간/드라이버 장애를 해결했다고 주장하지 않는다.
- 기존 배포 ZIP은952380a다. 이번 source 수정의 배포/설치 검증은 진행하지 않았다. M1–M2 전체가 PARTIAL이므로 M10을 진행하지 않는다.

아래는 이전 복구 세션의 검증 한계다. 최신 상태와 충돌하면 위 절이 우선한다.

# 이전 검증 한계 — 2026-10-22

- 사용자 허용 후 실제 엔진을 두 번 Start/Stop하는 제한 시간 프로브를 실행했다. 캡처·검출·제외된 오버레이 창과 종료 후 창 0개를 확인했으나 물리 표시 픽셀의 정렬은 확인하지 못했다.
- 이동 중 실제 마스크 소실을 재현해, 선택된 낮은 신뢰도 후보/이미 보호한 트랙의 누락 시 전체 보호로 전환하도록 보완했다. 이로 인해 오검출이나 남아 있는 트랙 때문에 보수적인 가림이 길어질 수 있다.
- 초기 포인터 기반 GUI 조작은 좌표 geometry 오류로 실패했다. 메인 UI 버튼과 전역 비상키는 여전히 NOT RUN이다. 직접 API Start/Stop과 구분한다.

- 수정 후 물리 마스크 정렬·전역 비상 중지·초기화 중 Stop은 NOT RUN이다. 라이브 샘플의 보호 유지/일반 및 fail-closed 중 직접 API Stop은 실행했으나 시각적 깜박임이 전혀 없다는 판정은 하지 않는다.
- 정적 ONNX/합성과 headless 추론 정지 회귀가 통과해도 라이브 노출이 없다는 증거는 아니다.
- 캡처가 새 프레임을 내지 않으면 보수적으로 전체 보호한다. 정지한 화면에서 WGC가 갱신을 멈추는 경우도 보호 상태로 남을 수 있다.
- 네이티브 추론이 영구 정지하면 Stop은 창을 먼저 제거하나 자원 정리/앱 닫기 완료는 지연될 수 있다. 프로세스 격리 watchdog은 없다.
- UI 스레드 정지, 렌더러/드라이버 장애를 별도 프로세스가 감시하지 않는다. 보호 제외 API 실패는 시작을 거부한다.
- 실제 해상도/디스플레이 구성 변경의 자동 재열거·재시작은 미완성이다. 보호를 멈추고 표시 장치를 새로고침한 뒤 다시 시작해야 한다.
- 물리 복수 모니터, 실제 Ctrl+C 정리, 실제 앱 녹화와 검출 포함 데스크톱 내보내기, clean VM 설치는 NOT RUN이다.
- Herdr 자체 장애와 과거 `$image` 객체의 실제 타입은 미확정이다. 테스트 스크립트의 직접 뷰어 실행·터미널 반복 최소화는 확인했다.
- Windows 캡처→추론→합성기 경로의 후행 처리 때문에 새 콘텐츠의 선행 차단이나 0 노출은 보장하지 않는다.
- 기존/신규 패키지는 이 제한을 가진 엔지니어링 미리보기이며 정식 배포 완료가 아니다.

# Historical known limitations

VeilDesk is a local privacy aid, not a security boundary or a guarantee that no
sensitive content can ever appear. The following limits are known and are not
hidden behind marketing language.

## Detection and tracking

- **Model accuracy is limited.** NudeNet 320n is a small detector; it can
  confuse gender/classes, miss unusual poses, occluded bodies, extreme lighting,
  very small subjects or stylised imagery. YOLOv8n can miss people or merge
  nearby people.
- **No accuracy benchmark.** Precision, recall, mAP and false-positive rate have
  not been measured on a labelled dataset. The static-image capability checks
  and live session counts are not accuracy metrics.
- **18 classes are not exhaustive.** Explicit content that the model does not
  represent, or that falls below the confidence threshold, can be missed.
- **Whole-body mode depends on person detection and segmentation.** Masks can
  be coarse at low resolution or when the subject is partially out of frame.
- **Tracking can lag or drift.** Velocity prediction covers short motion only.
  Fast motion, cuts, occlusion and camera motion can produce a late, offset or
  briefly stale region. Persistence intentionally keeps a region after a miss;
  it can therefore over-cover briefly.
- **Fail-closed is best effort.** If the process is suspended, the overlay is
  covered by another topmost window, or the desktop is on a secure screen, the
  cover may not be visible even though the engine intends to fail closed.

## Capture

- **Secure desktop / lock screen / UAC.** Windows Graphics Capture returns black
  frames on the locked secure desktop. VeilDesk cannot see or overlay the lock
  screen or UAC prompt. On unlock, capture rebuilds/recovers; until a successful
  analysis arrives, fail-closed covers the display.
- **DRM/protected surfaces.** Protected video may be black in WGC. VeilDesk
  will fail closed rather than show an unprocessed frame, but it cannot censor
  content it cannot capture.
- **Exclusive fullscreen games** may cover click-through overlays even when
  topmost. Borderless windowed mode is recommended.
- **Display changes** (resolution, HDR, monitor hot-plug) require Stop/Start;
  capture sessions are not rebuilt live.
- **Cursor capture** is off by default and, when enabled, the cursor can be
  inside a censored region.
- **Multi-monitor mixed DPI** is implemented with per-monitor DPI conversion,
  but only a single 150% display was available for physical verification.
  Negative virtual-desktop coordinates and two physical monitors remain
  unverified.
- **WGC availability** requires Windows 10 2004 (build 19041) or newer. Older
  Windows versions are unsupported.

## Overlay and rendering

- **Live visual placement is only partially verified.** Geometry and DPI tests
  pass, and the renderer is shared with screenshot export, but a final visual
  check of a moving detected subject on the physical display was blocked by the
  locked session. Treat placement on unusual DPI/multi-monitor layouts as
  needing verification.
- **Effects are WPF-rendered.** Blur, pixelate, frosted glass, night vision and
  marker styles can differ slightly between the live overlay and the screenshot
  compositor due to render-target vs screen composition, DPI rounding and
  effect sampling.
- **Topmost reassertion is periodic, not absolute.** A new topmost window can
  cover the overlay until the next reassert.
- **Click-through means no interaction.** You cannot click the overlay to pause
  it; use the app window or Stop.
- **Overlay exclusion must stay enabled.** The Debug-only opt-out can create a
  feedback loop where the engine captures its own fail-closed cover. Release
  builds always exclude overlays.

## Performance

- **CPU inference is the default.** On the reference iGPU, DirectML cold start
  was 46.6 s (first NudeNet 27.7 s), so CPU is used despite slower steady state.
  On other GPUs DirectML may be preferable; this is a user choice.
- **Shared model runs are serialized.** Multiple monitors do not run inference
  in parallel; each monitor's cadence competes for the same CPU/iGPU. High
  scan rates and many monitors can cause lag.
- **Adaptive scanning can miss brief events.** Static-scene skipping plus the
  750 ms safety scan bounds work, but a very brief exposure between scans may
  not be detected. Lower scan rates increase this risk.
- **Recording is capped** at 1280×800, 15 fps, 9,000 frames / 10 minutes and
  1.9 GB. There is no hardware encoder, audio, MP4/H.264 or trimming.
- **In-app recording performance is unmeasured.** The AVI writer and OS
  playback are verified; the full recorder loop has not been run on a live,
  unlocked desktop.
- **No long-duration soak test.** Memory/thermal behaviour over hours is
  unknown.

## Packaging and platform

- **Portable ZIP only.** No MSI/installer, no auto-update, no code signing.
  Windows SmartScreen may warn about an unsigned executable.
- **Models are not in Git.** A build requires `scripts/fetch-models.ps1` or
  manually placing the pinned files. The app itself never downloads them.
- **AGPL obligations.** Redistributing the app or a modified version requires
  corresponding source and compliance with AGPL-3.0, including the AGPL model
  weights' terms.
- **x64 Windows only.** No ARM64 build, no Linux/macOS support.
- **English UI only.** Settings contain a language control, but only English is
  implemented; the Korean option is not a translated UI.
- **No tray integration.** “Close to tray” is disabled; closing exits after
  stopping capture. “Start minimized” minimizes the window, not to a tray icon.
- **No Discord presence, cloud sync, accounts, ranks/XP, escalation, wallpaper,
  ambient audio, popup storm, reverse censor, virtual camera/OBS output, instant
  replay or watermark.** These were observed as Beta Blocker features but are
  out of scope for this vertical slice.
- **No accessibility audit.** Keyboard navigation and screen-reader behaviour
  have not been tested.
- **No localization, installer, or crash reporting.**

## Packs

- Packs are inert ZIPs. Only censoring (and optionally detection) settings,
  text presets and a single image asset are applied. Theme metadata is parsed
  but not applied. GIF/video assets, scripts, executables and arbitrary fonts
  are not executed or installed.
- Applying a pack extracts it to a new local folder; old applied folders are not
  automatically deleted.
- Built-in packs are copied once. If a user deletes one, it is not recreated on
  the next launch.
- Pack import performs structural validation, not content review. A pack can
  contain offensive images or text chosen by its author.

## Privacy and threat model

- **No network code** is present in the application, but the OS, display driver,
  WGC, ONNX Runtime and any third-party screen recorder can still observe the
  screen outside this app's control.
- **The local data directory is not encrypted.** Settings and session history
  are plain files. Screenshots/recordings are plain files chosen by the user.
- **A compromised machine, debugger, injected DLL or malicious pack** is outside
  the threat model.
- **No telemetry** means no automatic crash reports; diagnostics are copied
  manually by the user.
