# 인스타그램 과잉 가림·스크롤 잔상 수정 — 2026-10-22

기준 커밋 `1b488be` 이후, 브랜치 `codex/protection-recovery`.
**브라우저 인수는 아직 PARTIAL이며 릴리스/M10을 진행하지 않는다.**
화면 이미지를 저장·전송하지 않았고 입력 자동화도 사용하지 않았다.

---

## 1. 확인된 근본 원인

근거: `artifacts/browser-acceptance-feb7f0a36b4d4c1db2111068d0244ba7` (기록 로그 895분석).
좌표만 읽었고 픽셀은 보지 않았다(`tools/analyze_face_log.py`, `tools/dump_face_log.py`).

### 1.1 기록된 795×977 후보의 정체

| 프레임 | 시각 | 후보 | 신뢰도 | 비고 |
|---|---|---|---|---|
| 1483 | 21:05:54.238 | 622×708 | 0.382 | 단독 후보. track 29(188×238 실얼굴)를 **탈취** |
| 1493 | 21:05:55.077 | 795×977 | 0.361 | track 27(186×238 실얼굴)을 **탈취** |
| 1494~1496 | 21:05:55.15~.37 | 795×977 | 0.361 | 동일 후보 반복 |

- 후보 신뢰도는 **0.361/0.382**로, 사용자 설정 face 임계값 0.6보다 낮다.
- 같은 프레임의 실얼굴은 123×170~195×241, 신뢰도 0.405~0.916이었다.
  즉 큰 후보는 **주변의 실얼굴보다 4~6배 크고 점수는 가장 낮았다**.
- 795×977 후보는 같은 프레임의 실얼굴 상자(142×178)를 **완전히 포함**했다.
  → 이것은 같은 피Subjects를 더 거친 축척으로 한 번 더 검출한 **coarse duplicate**다.

### 1.2 3단으로 이어진 실패 (단일 원인이 아님)

1. **검출**: YuNet이 저신뢰·화면 크기 후보를 실제로 냈다. (모델 문제는 아니다.)
2. **추적 연관 오류**: 트래커의 연관 규칙(IoU 또는 중심거리) 때문에 그 후보가
   **이미 확립된 실얼굴 트랙을 탈취**했다. 그 결과 track 27의 원시 상자가
   186×238 → 795×977로 바뀌었다. (E: tracker association error)
3. **기하 확대**: 불확실 Face는 base 12% + 불확실 보정 12% = 1.48배로 확대되고
   예측 sweep이 더해져 **1199×1242 = 화면의 36.4%**짜리 검은 사각형이 되었다.

기하만 고쳐도 1단계의 잘못된 상자는 그대로 남는다. **상자 자체를 막아야 한다.**

### 1.3 스크롤 잔상 (약 2초)

기록 로그상 스크롤 중 미검출 face 트랙은 **마지막 관측 위치에 고정**되고
2초 유지窗口 동안 마스크가 남았다. 내용 이동을 측정하는 수단이 전혀 없었다.
사용자 관찰("스크롤 정지 후 약2초 안에 사라짐")과 일치한다.

### 1.4 공식 구현과 비교한 디코딩 감사

OpenCV 4.x/5.x `face_detect.cpp` 원문과 대조했다.

| 항목 | 공식 구현 | VeilDesk(수정 전) | 판정 |
|---|---|---|---|
| 점수 | `sqrt(cls*obj)` | 동일 | 일치 |
| 중심 | `(cell + bbox[0]) * stride` | 동일 | 일치 |
| 크기 | `exp(bbox[2]) * stride` | 동일 | 일치 |
| NMS | IoU | IoU | 일치 |
| **랜드마크** | `(kps + cell) * stride` (앵커 셀 기준, 상자 기준 아님) | **미구현** | **누락** |
| 전처리 | `blobFromImage(pad)` → swapRB=true(RGB), 리사이즈 없음 | 중앙 레터박스 + BGR | 차이 존재 |

수치 대조(licensed NASA fixture, 512×512):

```
OpenCV FaceDetectorYN : conf=0.9323 box=(178.2,62.9,89.9,113.3)
                        lms=(200,102) (247,103) (220,125) (199,143) (240,144)
VeilDesk 디코딩        : conf=0.9435 box=(178.8,63.4,90.9,110.5)   → 상자 일치
```

→ **상자 디코딩 자체는 오류가 아니었다.** 원인은 1.1~1.3의 연동 오류였다.
랜드마크는 공식 수식으로 복구했고(1.3의 근거), 상자 좌표는 그대로 유지했다.

전처리는 2560×1600 캔버스에서 비교했다. 레터박스(현재) IoU 0.905,
squash(리사이즈만) IoU 0.766, OpenCV 4.x 참조는 레터박스와 같은 축비 보존 결과를 냈다.
**어떤 근거로도 "큰 후보는 크기 때문에 무효"라는 판정은 내리지 않았다.**

---

## 2. 구현한 수정

### 2.1 후보 검증 (Phase 2)

- `src/VeilDesk.Core/Detection/FaceCandidateEvidence.cs`
  `FaceCandidateQuality { Valid, Suspicious, Invalid }`, 랜드마크 5점과 근거 문자열.
- `src/VeilDesk.Core/Detection/FaceCandidateValidator.cs`
  공식 랜드마크로 얼굴 배열이 일관적인지 판정한다.
  눈 간격/대칭, 세로 순서(눈→코→입), 코 정렬, 눈·입 높이 비율, 종횡비.
  기준은 **"얼굴이 아니다"의 증명**을 목표로 하며 자세·외모·신원을 판단하지 않는다.
  큰 얼굴도 작은 얼굴과 똑같이 유효하다.
- `src/VeilDesk.Inference/YuNetDetector.cs`
  `DecodeCandidates()`가 랜드마크를 복원하고 후보별 근거를 붙인다.
  상자 디코딩과 NMS는 공식 구현 그대로다.

### 2.2 후보 게이트 (Phase 2)

`src/VeilDesk.Core/Censoring/FaceCandidateGate.cs`.

거부 조건은 **특정 증거가 있을 때만** 성립한다.

- (a) 신뢰도가 face 임계값보다 낮고 **랜드마크가 얼굴과 모순** → 거부.
- (b) 신뢰도가 낮고 **이미 추적 중이거나 더 높은 점수의 얼굴을 포함**(coarse duplicate) → 거부.
- **face 임계값 이상의 신뢰도는 기하만으로 거부하지 않는다.**
  → 정짜 큰 얼굴은 ��인 크기로 계속 가려진다.

### 2.3 트래커 연관 보호 (Phase 1/2)

`src/VeilDesk.Core/Tracking/SubjectTracker.cs`

- 면적비가 4배를 넘고 새 후보의 신뢰도가 **그 트랙을 세운 최댓값 신뢰도보다 높지 않으면**
  트랙을 탈취할 수 없다. (기록된 795×977은 0.361로, 실얼굴 트랙의 0.909에 못 미친다.)
- `PeakConfidence`, `CompensatedBox`, `ContentVelocityX/Y`, `FaceEvidence`, `AgeMs` 추가.

### 2.4 스크롤 인지 추적 (Phase 3)

`src/VeilDesk.Core/Vision/SceneMotion.cs`, `SceneMotionEstimator.cs`

- 160×100 회색 썸네일 기반 **3단 피라미드 고정 창 매칭**.
  모든 이동량을 같은 창으로 비교하므로, 창을 움직여 차이를 숨기는 이동이
  유리해질 수 없다.
- 전역 이동 + **4×3 영역별 이동**. 영역은 "가리다움"이 아니라
  "애플리케이션 영역 단위 스크롤"을 다룬다.
- **보수적 폴백**: 정지 대비 개선이 10% 미만이면 "움직이지 않음"으로 둔다.
  근거가 약한 이동은 마스크를 화면 전체로 끌지 않는다.
- 미검출 face 트랙은 **내용과 함께 이동**한다(`CompensatedBox`).
  예측 sweep은 **내용 기준 속도**를 쓴다(화면 속도 − 내용 속도).
  → 스크롤 중 얼굴은 내용에 붙어 있고, 내용 안에서만 움직이면 예측된다.
- **퇴장 시 즉시 정리**: 보정된 상자가 화면을 완전히 벗어나면 트랙을 정리한다.
  (마스크는 즉시 사라지고, CoverageGuard가 불가능한 커버를 요구하지 않는다.)

### 2.5 가림 확대 합성 제거 (Phase 4)

`src/VeilDesk.Core/Censoring/FaceMaskGeometry.cs`

- 여백은 **관측된 얼굴에 한 번만** 적용하고, 국소 예측은 그 위치를 더한다.
  이전처럼 sweep 결과에 여백을 다시 걸면 이동량마다 여백이 두 배로 붙는다.
- 랜드마크와 모순되는 후보에는 불확실 보정 여백(+12%)을 적용하지 않는다.

### 2.6 진단 메타데이터 (Phase 1)

`src/VeilDesk.App/VeilDeskEngine.cs`의 `[acceptance-analysis]` 기록에
프레임 ID, 후보 상자·신뢰도·스트라이드, **랜드마크 5점과 일관성 근거**,
게이트 판정과 사유, 트랙 ID·추적 나이·최종 관측 이후 시간,
화면/내용 속도, 보정 상자, **최종 마스크 크기**, 측정된 내용 이동을 추가했다.
**이미지 픽셀은 기록하지 않는다.**

---

## 3. 측정 결과

### 3.1 실제 기록 로그 재생 (같은 원시 후보, 같은 설정)

`tools/FaceLogReplay` → `artifacts/face-scroll-20261022/recorded-replay.json`

| 항목 | 이전(기록된 마스크) | 이후(현재 파이프라인 재생) |
|---|---|---|
| 마스크 수 | 2062 | 2390 |
| 평균 마스크 면적 | 화면의 **3.52%** | 화면의 **1.25%** |
| 최대 마스크 면적 | 화면의 **78.4%** (2007×1600) | 화면의 **9.8%** (555×724) |
| 거부된 후보 | — | **25건**(전부 coarse duplicate) |

78.4% → 9.8%. 남은 9.8%는 기록 로그의 **0.48~0.84 신뢰도 큰 후보**에서 나온다.
이 로그에는 랜드마크 메타데이터가 없으므로(기록이 그보다 앞선 세션),
재생이 검증할 수 있는 것은 포함/중복 규칙뿐이고 랜드마크 규칙은 다음 실사용 로그에서
처음 측정된다. **남은 9.8%를 "해결됨"으로 기록하지 않는다.**

### 3.2 스크롤 시퀀스 정밀도 (실제 YuNet, 1920×1080 합성 시퀀스)

`tools/FaceScrollProbe` → `artifacts/face-scroll-20261022/after.json`

| 시퀀스 | 얼굴 커버리지 | 최대 마스크(화면 대비) | 잔상(현재) | 잔상(이전 규칙) |
|---|---|---|---|---|
| stationary | 1.00 | 0.15% | — | — |
| slow-scroll | 1.00 | 0.22% | — | — |
| fast-scroll (160px/frame) | 0.86 | 0.18% | **83ms** | 1328ms |
| scroll-stop | 1.00 | 0.21% | — | — |
| scroll-reversal | 1.00 | 0.21% | — | 996ms |
| face-enters | 1.00 | 0.21% | **83ms** | 581ms |
| face-leaves | 1.00 | 0.21% | **83ms** | 664ms |
| two-faces | 1.00 | 0.23% | **83ms** | 415ms |
| new-content | 1.00 | 0.21% | — | — |
| large-face | 1.00 | 1.09% | — | — |
| partial-face | 1.00 | 2.28% | — | — |
| video-motion | 1.00 | 0.24% | — | — |

- **잔상: 415~1328ms → 83ms(분석 1회)**. 임시 목표(<500ms)를 충족한다.
  단, 이는 통제된 합성 fixture의 수치이며 보편적 안전 보장은 아니다.
- **진짜 큰 얼굴은 그대로 가려진다**: large-face 시퀀스 커버리지 1.00,
  마스크는 검출 얼굴 크기의 1.24배다.
- fast-scroll 0.86은 **검출기가 160px/frame을 못 따라가는** 한계다(추적 문제 아님).

### 3.3 비용

| 항목 | 값 |
|---|---|
| 내용 이동 추정(2560×1600) | P50 3.7ms / P95 5.6ms (분석 1회당) |
| Face 분석 P50/P95 | 68~90ms / 83~163ms (기존과 같은 수준) |
| Core 회귀 | **70/70 PASS** (기존 53 + 신규 17) |
| Release 빌드 | 0경고 / 0오류 |

### 3.4 회귀로 막은 실패

`tests/VeilDesk.Core.Tests/FaceCandidateRegressionTests.cs`

- 기록된 795×977 후보는 거부되고, 확립된 186×238 얼굴 트랙을 탈취하지 못한다.
- 신뢰도 0.91인 큰 후보는 거부되지 않는다(진짜 큰 얼굴 보호).
- tracked face를 포함하는 coarse duplicate는 거부된다.
- 랜드마크가 모순되는 후보는 최소 여백으로만 가려진다.

`tests/VeilDesk.Core.Tests/ScrollTrackingRegressionTests.cs`

- 미검출 얼굴이 측정된 스크롤 이동을 따라간다.
- 화면을 벗어난 얼굴은 2초 대기 없이 정리된다.
- **불확실한 이동은 보수적 폴백을 유지한다**(위치 유지).
- 정지 영역은 다른 영역의 스크롤에 끌려가지 않는다.
- **장면 교체는 2초 유지 정책을 깨지 않는다**(탐지 공백과 구분).

---

## 4. 보존한 것

- YuNet ONNX 통합, Face-only 구성, 기존 해부학적/사람 검출, 로컬 오프라인 추론.
- ConfidenceGate, CoverageGuard, Fail-Closed, 캡처/녹화/스크린샷.
- 2초 face 유지 정책(전역적으로 줄이지 않음), 국소 불확실 가림 정책의 존재.
- 기존 53개 Core 테스트, 기존 headless probe 전체, Git 이력.

## 5. 재현 명령

```powershell
# 회귀 전체
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Headless.ps1

# 스크롤 시퀀스 정밀도(실제 YuNet) -> artifacts/face-scroll-20261022/after.json
dotnet run --project tools/FaceScrollProbe/FaceScrollProbe.csproj -c Release -- . artifacts/face-scroll-20261022/after.json
python tools/summarize_scroll_probe.py artifacts/face-scroll-20261022/after.json

# 실제 기록 로그 재생(수정 전/후) -> artifacts/face-scroll-20261022/recorded-replay.json
dotnet run --project tools/FaceLogReplay/FaceLogReplay.csproj -c Release -- `
  artifacts/browser-acceptance-feb7f0a36b4d4c1db2111068d0244ba7 `
  artifacts/browser-acceptance-8d5d3e2d0f6942b38fdae5723d29337a/settings.json `
  artifacts/face-scroll-20261022/recorded-replay.json

# 기록 로그 좌표 분석(원시 후보·트랙·마스크)
python tools/analyze_face_log.py artifacts/browser-acceptance-feb7f0a36b4d4c1db2111068d0244ba7/logs-after/app.log
python tools/dump_face_log.py 1475 1500 artifacts/browser-acceptance-feb7f0a36b4d4c1db2111068d0244ba7/logs-after/app.log
```

`tools/yunet_reference.py`·`tools/compare_opencv_reference.py`는 공식 구현 대조용
개발 도구다(개발 가상환경의 numpy/onnxruntime/opencv 필요).
**애플리케이션은 Python·OpenCV 런타임 의존성이 없다.**

## 6. 남은 미검증 항목
- **실제 인스타그램 브라우저 인수는 아직 PARTIAL**이다. 위 수치는 합성 fixture와
  기록 로그 재생에 근거하며, 실제 화면 관찰은 사용자 승인과 재시험이 필요하다.
- 기록 로그에 랜드마크가 없어 3.1의 잔여 9.8%를 판정하지 못했다.
- 클릭 스루·타이핑·탭/창 크기·영상·Chrome 포커스 F10·Stop 정리 등
  대화형 확인 항목은 이전과 동일하게 미확인이다.
- 실제 다중 모니터는 장치 1개로 미검증이다.
- **릴리스/M10은 진행하지 않는다.**
