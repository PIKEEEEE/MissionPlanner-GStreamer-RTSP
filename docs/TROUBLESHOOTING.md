# Troubleshooting

## Mission Planner 전체가 영상 종료와 함께 종료됨

초기 in-process GStreamer 구조에서 발생.

해결:
- GStreamer pipeline을 외부 `gst-launch-1.0.exe` 프로세스로 분리

## CS0104 Timer ambiguous reference

```text
'Timer' is an ambiguous reference between
System.Windows.Forms.Timer and System.Threading.Timer
```

해결:
- WinForms Timer 명시
- ThreadPool 완전 수식

## 설정창에서 응답 없음

원인:
- `gst-inspect`를 UI thread에서 동기 실행
- `ReadToEnd()`가 실제 timeout보다 먼저 block될 수 있었음

해결:
- 백그라운드 ThreadPool
- async stdout/stderr
- process timeout

## `libgiolibproxy.dll` 오류

```text
Failed to load module:
...\lib\gio\modules\libgiolibproxy.dll
```

대응:
- Proxy bypass ON
- `GIO_USE_PROXY_RESOLVER=dummy`
- `NO_PROXY=*`

## H.264 손실복구 ON에서 영상이 안 나옴

로그:

```text
Delayed linking failed
failed delayed linking ... GstRTSPSrc ... GstRtpH264Depay
streaming stopped, reason not-linked (-1)
```

해결:
- `rtph264depay` 강제 삽입 제거
- `application/x-rtp ! decodebin3` 사용

## RTSP 재접속 불가

진단:

```powershell
Test-NetConnection <device-ip> -Port 8554
```

`TcpTestSucceeded : False`면 그 시점에는 RTSP 서버가 포트를 열고 있지 않은 상태입니다.

## 최종 하드웨어 원인: HDMI splitter

SIYI converter 앞단의 HDMI splitter가 있을 때:

- 일정 시간 후 RTSP control connection 종료
- 8554 포트 미응답
- 자동/수동 재접속 실패

splitter 제거 후:
- 정상 영상
- 끊었다 다시 연결 가능
- 자동 재연결 정상

## 영상 블록 깨짐

queue buffer 증가는 packet loss 자체를 고치지 못합니다.

확인:
- 네트워크 packet loss
- bitrate
- GOP/keyframe 간격
- UDP/TCP 차이
- HDMI 입력 안정성
- splitter/EDID/Hot-Plug
- retransmission 지원 여부
