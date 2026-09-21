# 개발 이력

## 1. 초기 Mission Planner 내부 GStreamer 방식

초기에는 Mission Planner 프로세스 안에서 직접 GStreamer 영상을 처리했습니다.

문제점:
- `GStreamer Stop` 또는 영상창 종료 시 Mission Planner 전체가 종료될 수 있음
- native cleanup/EOS가 Mission Planner 안정성에 영향을 줌
- bitmap/event type 충돌 문제도 발생

## 2. V2 ~ V5: Popout/HUD 실험

Mission Planner의 Popout/HUD 기반으로 영상을 띄웠습니다.

- 영상 출력은 가능
- HUD overlay가 함께 보임
- 영상 전용 창 요구사항과 맞지 않음

## 3. V6: `autovideosink` 영상 전용 창

영상 전용 창 자체는 구현됐지만 창 종료 시 Mission Planner까지 종료되는 문제가 남았습니다.

이 시점에서 GStreamer를 Mission Planner 프로세스 밖으로 분리하는 방향으로 전환했습니다.

## 4. 외부 `gst-launch` 구조

핵심 전환점:

```text
Mission Planner Plugin
  → 외부 gst-launch-1.0.exe 실행
  → PID 기준 영상창 탐색
  → SetParent()로 Mission Planner Panel에 임베드
```

효과:
- GStreamer 종료가 Mission Planner 종료로 전파되지 않음
- GStreamer만 재시작 가능
- 고정 팝업 창 유지 가능

## 5. V8 — Persistent Reconnect

- 고정 WinForms Host Form
- 외부 gst-launch
- 영상창 임베드
- process exit 감지
- 자동 재시작
- host window 유지

## 6. V9 — 실시간 설정 UI

Mission Planner 내부에서 다음 값을 수정/저장:

- RTSP URL
- UDP/TCP
- latency
- queue buffers
- leaky
- TCP timeout
- UDP retransmission
- auto reconnect
- reconnect delay

## 7. V10 — H.264 손실 복구 실험

블록 깨짐/녹색 화면이 keyframe까지 유지되는 문제를 줄이기 위해 아래를 실험:

```text
rtph264depay
wait-for-keyframe
request-keyframe
h264parse
avdec_h264
```

하지만 장비/버전별 element/property 호환성과 연결 문제가 생겼습니다.

## 8. V10.1 ~ V10.3.2 — GStreamer 호환성

개선:
- `avdec_h264` 강제 지정 제거
- `decodebin3` 복귀
- `gst-inspect-1.0` property 검사
- GStreamer 실행파일 직접 선택
- UI thread blocking 제거
- timeout 적용
- 선택 GStreamer PATH 우선 처리

V10.3에서는 `Timer` 이름 충돌로 CS0104가 발생했고 V10.3.1에서 수정했습니다.

## 9. V10.4 — 진단 기능

추가:
- stdout/stderr 수집
- Mission Planner 내 로그 Viewer
- element 검사
- GStreamer 진단 로그

이 기능으로 실제 문제를 추측이 아닌 로그 기반으로 분석할 수 있게 되었습니다.

## 10. V10.5 — RTSP 호환성 옵션

추가:
- AUTO / UDP / TCP
- `do-rtcp`
- `do-rtsp-keep-alive`
- UDP retransmission

## 11. V10.6 — Proxy bypass

MinGW GStreamer 환경에서 `libgiolibproxy.dll` 로딩 관련 문제가 관찰됐습니다.

옵션 ON 시:

```text
GIO_USE_PROXY_RESOLVER=dummy
NO_PROXY=*
no_proxy=*
```

를 외부 GStreamer에 적용했습니다.

## 12. V10.7 — Safe Decode

손실복구 ON에서 다음 오류를 로그로 확인:

```text
Delayed linking failed
failed delayed linking ... rtspsrc ... rtph264depay
streaming stopped, reason not-linked (-1)
```

그래서 `rtph264depay` 강제 경로를 제거하고 안정적인 `decodebin3` 경로를 사용했습니다.

## 13. V10.8 — Connect Watchdog

재연결 때 gst-launch가 종료되지 않고 `Connecting...` 상태로 살아있는 경우 process-exit 기반 watchdog이 동작하지 않는 문제를 보완했습니다.

PLAY 단계까지 일정 시간 내 도달하지 못하면 gst-launch를 강제 재시작하도록 변경했습니다.

## 14. V10.9 — Smart Reconnect

- reconnect backoff
- PLAY 성공 시 상태 reset
- verbose RTP stats 제거
- 로그량 감소
- 안정적인 외부 프로세스 재연결 유지

## 15. 최종 원인 규명 — HDMI 분배기

SIYI Air Unit HDMI Input to Ethernet Output Converter 앞단에 있던 HDMI 분배기를 제거하자 문제 해결:

- 영상 안정화
- 끊었다 다시 붙여도 RTSP 복구
- Mission Planner 자동재연결 정상

최종 권장 연결:

```text
HDMI Source
  → SIYI HDMI Input to Ethernet Output Converter
  → Ethernet / RTSP
  → Mission Planner V10.9
```
