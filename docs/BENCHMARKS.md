# Face 측정 추가 — 2026-10-22

근거 [FACE-BROWSER-20261022.md](FACE-BROWSER-20261022.md). 숫자를 다른 경로의 지연으로 대체하지 않는다.

- CPU2스레드/고정1280×720/warmup5+50: 전처리+YuNet+decode P50/P95/P99=28.3/36.3/42.2ms. 자동스레드0=30.4/43.1/45.4ms.
- 실제 Face WGC2109회 TotalMs=48.6/86.1/133.0ms. 캡처 timestamp→분석 로그=94/156/188ms. 실제 표시/scanout/노출 간격 미측정.
- 약204초 Face 처리량10.3분석/s, frame번호 증가율13.5/s(전용 capture통계와 구분).
- 5분 혼합 시험 CPU774.16process초/8logicalCPU, 대략33% 정규화. private최대636.3MiB/마지막491.0MiB. 기존 해부학적 세션/모델교체/WGC/진단로그 포함. 누수나 장기 안정성 PASS 아님.
- fixture6/6와 영상표본8/8은 동일 NASA 인물의 변형이며 일반 얼굴 recall 수치가 아니다. 실세계 recall/부분가림/다양성 및 실제 브라우저 시각 검증 미측정.

이하 과거 측정:

# 최신 측정 — M1–M2 후속 세션 (2026-10-22)

화면 없는 고정 이미지 실제 CPU 모델/추적/합성 soak:300.1초2521회분석/252합성,
준비20회 이후 full-GC 전후 private delta−2.7MiB, handles−14, threads−5.
표본 private 약116.6–125.1MiB. 측정 기준과 한계는 ACCEPTANCE-20261022.md 참조.
CPU 정규화33.4%, P50 95.7/P95 153.1/P99 196.3ms. 빌드가 병행되어 성능 일반화 금지.
no WGC/no overlay window이므로 장시간 실제 보호 검증과 구분한다.

단일 실제 화면150% DPI에서 실제 검출에서 생성된 마스크의 PointToScreen 모서리 오차 최대0.484px.
해부학적 검출 정확도나 end-to-end 노출 시간 측정은 아니다.
실제 모델 정적 AVI24프레임 선언1.600초/벽시계약1.61초. 고유 입력1개를 시간축에 반복했다.

새 UI75초(보호 미시작)에서 private11.4→71.6MiB, handles204→449, CPU time74.47초.
초기화와 입력 자동화가 포함되어 독립적인 idle/누수/장기 안정성 판정으로 사용하지 않는다.
재현 가능한 별도 idle CPU 프로파일링은 남아 있다.

아래는 이전 세션의 측정이다.

# 이전 측정 — 2026-10-22 복구 세션

사용자 허용 후 실제 엔진 프로브: 두 번의 8초 구간 끝에서 캡처 119/118프레임, 분석 15/16회.
캡처는 대략 15 fps이며 적응형 분석 빈도는 이 정적/느린 이동 시험에서 약 2회/초였다.
16개 1초 상태 샘플에서 영역 0개 없음, 부분 마스크 상태 6개와 나머지 fail-closed 상태.
이는 픽셀 노출 시간이나 end-to-end 지연 측정이 아니다.

CPU/GPU를 CIM으로 확인: Intel Core i7-1065G7 / Intel Iris Plus.
이번에는 CPU만 실행했다. DirectML 성능 개선이나 현재 GPU 사용률을 주장하지 않는다.

실행: `dotnet run --project tools/ProtectionProbe/ProtectionProbe.csproj -c Release --no-build -- models artifacts/live-test/david.jpg`.
기존 로컬 조각상 이미지 한 장을 10회 분석, 회차 사이 140 ms 대기, 분석 내부 시간만 수집했다.
모델 로드 489.2 ms. 전체 분석 nearest-rank P50 67.5 ms / P95 108.4 ms / P99 108.4 ms.
표본 수 10이므로 P95/P99는 모두 최댓값이며 일반화할 수 없다.

제어된 추론 정지 시험의 보호 영역 판정 4.54 ms. 이는 스케줄러와 판정 작업을 포함한 headless 측정이며
실제 WPF 렌더링/합성기/화면 표시 지연이 아니다.
정적 실제 검출 9개에서 검열 영역 2개를 합성해 128,082픽셀이 변경됐다. 정답 라벨 기반 정확도는 측정하지 않았다.

정밀한 현재 capture FPS 분포, 데스크톱 end-to-end P50/P95/P99, 무검열 노출 시간, GPU 사용률,
이동 대상/스크롤 정확도와 장기 메모리 사용량은 NOT RUN.
아래는 이전 에이전트의 과거 측정이며 이번 세션에서 다시 측정한 값이 아니다.

# Historical benchmarks and measurements

All numbers below were measured on the stated machine on 2026-10-22 unless
explicitly marked otherwise. They are development measurements, not marketing
claims. Vendor claims from Beta Blocker's public pages are **not** reproduced
and are not listed as VeilDesk benchmarks.

## Reference machine

| Item | Value |
|---|---|
| CPU | Intel Core i7-1065G7 |
| GPU | Intel Iris Plus (integrated) |
| RAM | 16 GB |
| OS | Windows 11, build 26200 |
| Display | 2560×1600 at 150% DPI, single physical monitor |
| Runtime | .NET 8.0.32, x64 |
| Session state | Unlocked for capture/inference measurements; locked during the final overlay/recording attempt |

## Capture (Windows Graphics Capture + D3D11)

Measured with `tools/CaptureProbe` and `tools/WgcProbe`.

| Metric | Result |
|---|---|
| Frame size / format | 2560×1600 BGRA8 |
| Delivered capture rate | 13–19 fps (requested 15 fps) |
| Non-black content | 100.0% sampled pixels |
| Capture start | 360–636 ms |
| Capture stop | ~1 s |
| Readback path | GPU texture → staging texture → mapped BGRA buffer |
| Screenshot output | real WGC frame, 202,240-byte PNG in `Pictures\VeilDesk` |
| Synthetic compositor probe | 640×360; 220×120 solid region; 11.46% pixels changed; center `#0B0D12`; outside unchanged |

The start figure includes WGC item creation and the first frame. The rate is
display/compositor dependent and is not a guaranteed application frame rate.

## ONNX inference (CPU)

Measured with `tools/VisionProbe` on the standard Ultralytics `bus.jpg` at the
model's native input size.

| Metric | Result |
|---|---|
| Model load (all three sessions) | ~281 ms |
| Steady full pass | ~54–67 ms |
| YOLO person component | ~25–31 ms |
| NudeNet component | ~22–27 ms |
| Preprocessing | ~5–10 ms |
| Person detections | 3 correct person boxes on `bus.jpg` |

“Full pass” means person detection + anatomical detection + decode/NMS for that
probe; it is not an end-to-end app frame latency. The app schedules person and
anatomical models at separate rates, so a live analysis interval is not the same
as this combined probe number.

### DirectML (same machine)

| Metric | Result |
|---|---|
| Provider load | **46.6 s** |
| First NudeNet run | **27.7 s** |
| Steady run | ~41 ms |

DirectML was faster in steady state but the cold-start cost made it a poor
default for this integrated GPU. The app therefore defaults to CPU and exposes
DirectML as an opt-in. These measurements are the reason, not a claim that
DirectML is universally bad.

## Live engine (unlocked session)

Observed on the Dashboard during a real Start/Stop run:

| Metric | Result |
|---|---|
| Provider | CPU |
| Capture rate | ~12–14 fps |
| Analysis P50 | ~28–43 ms |
| Overlay running | Yes |
| Overlay excluded from capture | Yes |
| Start/Stop | Completed; returned to Idle |
| Session record | 97.1 s, 103 detection events, peak 11 tracks |

The 103 events came from the application's own session log
(`%LOCALAPPDATA%\VeilDesk\sessions.jsonl`). They demonstrate that live detections
occurred; they are not an accuracy metric. A later 710.6 s session recorded zero
detections because the Windows session was locked and WGC returned black frames,
which correctly drove fail-closed behaviour.

## Video container and playback

Measured with `tools/AviProbe` using 30 real JPEG frames rendered from a captured
screenshot, then opened through Windows Media Foundation (`MediaPlayer`).

| Metric | Result |
|---|---|
| Frames written | 30 |
| File size | 884,818 bytes |
| Resolution / rate | 640×360, 15 fps |
| WMF `MediaOpened` | Yes |
| WMF duration | 00:00:02.000 |
| Decoded video size | 640×360 |
| Decoded poster non-black | 96.4% |
| Output | `artifacts/avi-probe/veildesk-avi-probe.avi` (generated; artifacts are not committed) |

The probe proves the RIFF/AVI headers, `idx1` index and JPEG stream are readable
by the OS decoder. The `movi` LIST size was corrected to include its FOURCC
during this check; `AviProbe --inspect` now reports `frames=30`, `idx1=yes` and a
2.000 s duration. The probe does **not** measure the in-app `VideoRecorder` loop;
that requires an unlocked live capture session and is still pending.

## Package build

The self-contained Release publish was executed on 2026-10-22 with
`scripts/package.ps1`; all four model hashes were re-verified before publishing.

| Metric | Result |
|---|---|
| Publish folder | 238.2 MB, 494 files |
| ZIP | 110.5 MB |
| ZIP SHA-256 | written to `PACKAGE-SHA256.txt` after packaging |
| Runtime included | .NET 8 self-contained, win-x64 |
| Models included | 4 pinned ONNX files, hash-checked |
| Built-in packs | 4, copied on first launch |
| Published launch | Started and installed all four packs into a throw-away data root |
| Clean machine / VM | **NOT RUN** |
| Installer / code signing | Not provided; portable ZIP only |

The ZIP SHA-256 is printed by the script. It changes whenever documentation or
build metadata changes, so record the hash from the final artifact rather than
copying an older value.

## Tests

`dotnet test tests/VeilDesk.Core.Tests/VeilDesk.Core.Tests.csproj`:

- 23 passed, 0 failed, 0 skipped.
- Test host duration: ~136 ms (not including restore/build).
- Core build time is not reported as a benchmark.

## Not measured

- End-to-end recording throughput, dropped frames and disk write rate.
- Detection precision, recall, mAP or false-positive rate on a labelled dataset.
- Overlay placement error in physical pixels against ground truth.
- Multi-monitor scaling with two or more physical displays.
- Long-duration (hours) thermal/memory behaviour.
- DirectML performance on other GPUs.
- Clean-machine startup time (the package was launched only on the build host).

## Reproduce

```powershell
# Capture probe (requires an unlocked desktop)
dotnet run --project tools/CaptureProbe/CaptureProbe.csproj

# Vision probe (CPU)
dotnet run --project tools/VisionProbe/VisionProbe.csproj -- `
  "C:\path\to\bus.jpg" --cpu

# AVI container + Media Foundation playback
dotnet run --project tools/AviProbe/AviProbe.csproj -- `
  "C:\Users\<you>\Pictures\VeilDesk\<screenshot>.png" `
  "artifacts\avi-probe\veildesk-avi-probe.avi"
```

The exact model files, hashes and preprocessing contracts are in
`models/manifest.json`.
