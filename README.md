# Mission Planner GStreamer RTSP Video Plugin

Mission Planner에서 RTSP 영상을 안정적으로 표시하기 위해 개발한 GStreamer 플러그인 프로젝트입니다.

최종 구조는 Mission Planner 내부에서 GStreamer를 직접 구동하지 않고, 별도 `gst-launch-1.0.exe` 프로세스를 실행한 뒤 그 영상창을 Mission Planner의 고정 팝업 안에 임베드하는 방식입니다. 이 구조를 통해 GStreamer 종료/재시작이 Mission Planner 전체 종료로 이어지는 문제를 분리했습니다.

## 현재 권장 버전

**V10.9 — GStreamer Smart Reconnect**

- 소스: `src/GStreamerSmartReconnectV10_9.cs`
- 배포 ZIP: `releases/MissionPlanner_GStreamer_SmartReconnect_V10_9.zip`

## 검증된 장비 구성

개발 환경에서는 다음 RTSP 변환 장비를 사용했습니다.

- SIYI Air Unit HDMI Input to Ethernet Output Converter
- RTSP endpoint 예시: `rtsp://192.168.50.25:8554/main.264`

최종적으로 HDMI 분배기를 제거하고 HDMI 소스를 SIYI 변환기에 직결한 뒤 영상 중단/복구 및 자동 재연결이 정상 동작했습니다.

```text
HDMI Source
   ↓
SIYI HDMI Input to Ethernet Output Converter
   ↓
Ethernet / RTSP
   ↓
Mission Planner + V10.9 Plugin
```

## 현재 권장 설정

```text
Proxy bypass: ON
Protocol: UDP
UDP retransmission: OFF
RTCP: ON
RTSP Keep Alive: ON

손실 대응 모드: 0 - 호환성 우선
Latency: 300 ms
Queue: 6
Leaky: 2

Auto reconnect: ON
Connect watchdog: 8 sec
```

## 설치

1. Mission Planner 종료
2. 기존 사용자 GStreamer 플러그인 `.cs` 파일 정리
3. `src/GStreamerSmartReconnectV10_9.cs`를 아래 폴더에 복사

```text
C:\Program Files (x86)\Mission Planner\plugins\
```

4. Mission Planner 재실행
5. HUD 우클릭 메뉴에서 GStreamer 설정 진입
6. 사용할 `gst-launch-1.0.exe` 직접 선택
7. RTSP URL 및 옵션 설정
8. 영상 시작

## 최종 아키텍처

```text
Mission Planner
 └─ Plugin Host Form
     ├─ Settings / Log / Watchdog
     └─ Panel
         ↑ Win32 SetParent
         │
 external gst-launch-1.0.exe
 └─ rtspsrc → decodebin3 → queue → videoconvert → autovideosink
```

외부 프로세스 구조의 장점:

- GStreamer crash/exit가 Mission Planner에 직접 전파되지 않음
- 영상 재시작 중 Mission Planner 유지
- 최신 GStreamer 실행파일을 직접 선택 가능
- stdout/stderr 진단 로그 수집 가능
- reconnect/watchdog를 독립적으로 구현 가능

## 왜 `decodebin3`인가?

명시적 H.264 복구를 위해 `rtph264depay`를 강제로 연결했을 때 실제 장비에서 다음 오류가 발생했습니다.

```text
Delayed linking failed
failed delayed linking ... GstRTSPSrc ... GstRtpH264Depay
streaming stopped, reason not-linked (-1)
```

따라서 현재 권장 pipeline은 codec/depayloader를 강제로 지정하지 않고 `decodebin3`가 실제 RTP stream에 맞게 선택하도록 합니다.

```text
rtspsrc
! application/x-rtp
! decodebin3
! queue
! videoconvert
! autovideosink
```

## GIO Proxy Bypass

MinGW GStreamer에서 다음과 같은 경고가 반복된 환경이 있었습니다.

```text
Failed to load module: ...\lib\gio\modules\libgiolibproxy.dll
```

플러그인의 Proxy bypass 옵션은 외부 GStreamer 프로세스에 다음 값을 적용합니다.

```text
GIO_USE_PROXY_RESOLVER=dummy
NO_PROXY=*
no_proxy=*
```

## 자동 재연결

V10.9는 다음 흐름을 사용합니다.

```text
PLAY 성공
  ↓
영상 재생
  ↓
stream / RTSP 끊김
  ↓
gst-launch 종료 또는 watchdog
  ↓
server recovery wait
  ↓
재시도
  ↓
실패 시 backoff
  ↓
PLAY 성공 시 reset
```

## 최종 하드웨어 원인

재연결 실패가 GStreamer 문제처럼 보였지만, 최종적으로 SIYI 변환기 앞의 HDMI 분배기를 제거하자 문제가 해결되었습니다.

분배기가 있을 때:

- 몇 분 후 RTSP control connection 종료
- `8554` 포트가 응답하지 않는 상태 발생
- Mission Planner/GStreamer에서 재접속 불가능

분배기 제거 후:

- 영상 정상 유지
- HDMI/영상 중단 후 복구
- V10.9 자동 재연결 정상

따라서 동일 증상에서는 HDMI splitter, EDID, Hot-Plug, 케이블 및 전원 안정성도 함께 확인해야 합니다.

## 문서

- `docs/DEVELOPMENT_HISTORY.md` — 버전별 개발 이력
- `docs/ARCHITECTURE.md` — 최종 구조와 pipeline
- `docs/TROUBLESHOOTING.md` — 오류별 진단
- `docs/logs/` — 개발 중 수집한 대표 로그
- `docs/HISTORICAL_ARTIFACTS.md` — 개발 중 생성된 버전/파일 인덱스

## 배포물 / 과거 버전

- `releases/MissionPlanner_GStreamer_SmartReconnect_V10_9.zip` — 현재 권장 배포 ZIP
- `docs/HISTORICAL_ARTIFACTS.md` — V2~V10.9 개발 산출물 목록
- 변경 배경은 `CHANGELOG.md`와 `docs/DEVELOPMENT_HISTORY.md`에 정리

## 라이선스

아직 라이선스를 선택하지 않았습니다. 공개 저장소로 배포할 경우 목적에 맞는 라이선스를 별도로 추가하세요.
