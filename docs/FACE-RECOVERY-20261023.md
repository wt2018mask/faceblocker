# 후속 사용자 확인 완료

[INSTAGRAM-OBSERVED-20261023.md](INSTAGRAM-OBSERVED-20261023.md): 최신 실행에서 버튼·얼굴 가림·스크롤·클릭/입력/탭·Stop 정리를 사용자 관찰로 확인. 아래 초기 미검증 상태를 보완하며, 이전 실패 원인은 여전히 미확정.

# Face 회귀 실행 경로 감사 — 2026-10-23

기준 HEAD205cb4a, 체크포인트 `codex/checkpoint-face-recovery-20261023`. 작업 시작 당시 clean. 현재 프로세스는 Get-Process에서 조회되지 않았고 CIM 조회는 접근 불가였다. 따라서 사용자가 이전 패키지를 실행했다고 확정하지 않는다.

VERIFIED(source): Dashboard FaceOnlyButton과 실제 FaceOnly_Click은 존재한다. 이전 코드는 선택 후 이름을 Face selected로 바꿨다. 이름을 항상 Face only · black으로 유지하고 선택 상태를 별도 안내에 표시했다. 실행 중 대상 잠금 유지. 버튼 삭제는 재현되지 않았다. 150%DPI에서의 물리 표시/마우스·키보드 검증은 NOT RUN.

VERIFIED(artifact): Face 이전 패키지 DLL(186880bytes,10/22 11:06)과 개발 DLL(231936bytes,10/22 23:27)이 공존했다. 실행 파일 혼동 가능성은 있으나 사용자 실행 경로는 미확정이다. About/시작 로그에 informational commit, 실행 경로, 설정 경로를 추가했다. `scripts/Start-LatestFace.ps1`은 canonical project만 빌드하며 -Launch일 때만 창을 연다. 이 감사에서는 실행하지 않았다.

PARTIAL: zero Instagram censorship 원인은 아직 재현/확정 못했다. 20576b0의 후보 gate·motion 변경을 확인했고 임의 revert/threshold 변경을 하지 않았다. model SHA256=8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4. 실제 모델/전체 자동 회귀 결과는 artifacts/recovery-20261023.log. 사용자 설정은 수정하지 않았다.

NOT RUN: 최신 빌드의 실제 Instagram 가림, 스크롤, 클릭, Stop, Chrome F10. 기존 oversized 문제도 해결로 승격하지 않는다. 다음은 사용자가 실행했던 경로 확인 및 새 허용하 canonical 빌드 사용자 안내 시험이다. API/모델 성공을 물리 표시 성공으로 해석하지 않는다. 릴리스/M10 BLOCKED.

실행 명령: 저장소에서 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-LatestFace.ps1 -Launch`. 보호는 자동 시작하지 않는다. private SDK 사용; 시스템 dotnet에는 SDK가 없어 최초 검증이 실패했고 private SDK PATH 지정 후 재실행했다.


자동 검증 VERIFIED: Core70/70, Release0경고/0오류, ProtectionProbe/RecordingProbe PASS, 실제 YuNet fixture6/6·이동20/20·coverage95%20/20·VP8표본8/8, Stop cleanup headless PASS. baseline preprocess+inference+decode P50/P95/P99=20.6/27.8/35.2ms(50회,CPU2threads), 일반 정확도/실제 overlay 증거 아님. UI 이름 변경은 소스 감사만 수행했으며 실제 missing preset/zero censorship의 실패-통과 회귀를 재현했다고 주장하지 않는다.
