# Instagram 사용자 인수 관찰 — 2026-10-23

실행 빌드 `b9d5b07` / canonical `src/VeilDesk.App/bin/Release/net8.0-windows10.0.19041.0/VeilDesk.App.exe`. 사용자가 직접 실행한 PID16240을 읽기 전용으로 확인했다. 시작 로그의 informational version=1.0.0+b9d5b07cb3e1019b2e39adafcfd0e2b0d89a8a5f, settings=LocalAppData/VeilDesk/settings.json. 이전 실패 때 사용한 EXE는 여전히 미확정이며 구형 EXE를 원인으로 단정하지 않는다.

## VERIFIED — 사용자 직접 관찰

- Face only · black 버튼과 실제 Instagram 얼굴 가림 모두 정상.
- Instagram을 조금 스크롤한 뒤 새 얼굴도 가려지고 과대 가림 없음.
- 보호 중 Chrome 클릭·짧은 텍스트 입력·탭 전환 모두 정상.
- Stop 후 모든 가림이 사라짐.

위 판정은 이번 짧은 시험 범위의 사용자 관찰이다. 픽셀/표시 프레임을 저장·전송하지 않았으며 검출 recall, 정량 잔상 시간, 모든 게시물에서의 과대 가림 부재, 무노출을 증명하지 않는다. 과거 과대 가림 문제는 이번에는 관찰되지 않았지만 보편적 해결이라고 주장하지 않는다.

## VERIFIED — 자동 로그

07:40:49 build identity; 07:40:59 targets-selected Face-only/Solid/#000000/opacity1; 07:41:02 protection-started monitors1/providerCPU; 07:41:03 FailClosed→Protecting; 07:43:32.168 Stop 요청, .189 Stopped 전이. 사용자의 실제 화면 정리 관찰과 일치한다. 실행 중 환경에 acceptance diagnostics를 주입하지 않아 프레임별 검출·추적 숫자는 수집하지 않았다.

앱은 사용자가 먼저 실행한 인스턴스여서 자동 종료하지 않았다. 중복 실행 시도는 기존 인스턴스 보존 검사에서 중단돼 새 창은 생성되지 않았다. 브라우저 조작/보호 자동 시작/화면 저장/업로드 없음.

## PARTIAL / NOT RUN / BLOCKED

전체 브라우저 인수는 PARTIAL. 영상 재생, Chrome 포커스 Ctrl+Shift+F10, resize, 실제 초기화 취소, 장시간 안정성, 움직이는 브라우저 export, 물리 다중 모니터 미검증. 정량 ghost duration·불필요 면적·scanout latency 미측정. 릴리스/M10 BLOCKED, 패키지 미생성. 기존70/70·Release·실제YuNet headless 검증은 앞선 보고서 참조; 이번에는 코드 변경이 없어 반복하지 않았다.

현재 회귀 보고의 핵심 성공 기준인 버튼→Face 선택→Start→실제Instagram 얼굴 가림→스크롤 새 얼굴→Stop은 사용자 관찰로 확인됐다. 이전 zero-censorship의 정확한 실패 단계는 재현되지 않았으므로 원인 확정이나 특정 알고리즘 수정의 효과로 귀속하지 않는다.
