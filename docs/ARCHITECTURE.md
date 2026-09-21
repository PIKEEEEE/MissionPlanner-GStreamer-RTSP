# 아키텍처

## 구조

```text
┌──────────────────────────────────────────────┐
│ Mission Planner                              │
│                                              │
│ GStreamerSmartReconnectV10_9.cs              │
│  ├─ Settings UI                              │
│  ├─ Persistent Host Form                     │
│  ├─ Process Watchdog                         │
│  ├─ Reconnect / Backoff                      │
│  ├─ Runtime Log Viewer                       │
│  └─ Win32 Window Embedding                   │
└─────────────────────┬────────────────────────┘
                      │ Process.Start
                      ▼
┌──────────────────────────────────────────────┐
│ gst-launch-1.0.exe                           │
│                                              │
│ rtspsrc → decodebin3 → queue                 │
│         → videoconvert → autovideosink       │
└─────────────────────┬────────────────────────┘
                      │ Native video window
                      ▼
             SetParent / MoveWindow
                      │
                      ▼
             Mission Planner Panel
```

## 외부 프로세스 사용 이유

Mission Planner 내부에서 native GStreamer pipeline을 직접 생성/해제하면 영상 종료가 Mission Planner 안정성에 영향을 줄 수 있었습니다.

외부 프로세스로 분리하면 GStreamer가 죽거나 재시작되어도 Mission Planner와 Host Form은 유지됩니다.

## Window Embedding

외부 gst-launch가 만든 top-level 영상창을 PID로 찾은 뒤 아래 Win32 API로 임베드합니다.

```text
EnumWindows
GetWindowThreadProcessId
SetParent
GetWindowLong
SetWindowLong
MoveWindow
```

## 권장 pipeline

```text
rtspsrc location=<URL>
    protocols=udp
    latency=300
    do-rtcp=true
    do-rtsp-keep-alive=true
    udp-reconnect=1
    timeout=<configured>
    do-retransmission=false
! application/x-rtp
! decodebin3
! queue max-size-buffers=<N> max-size-bytes=0 max-size-time=0 leaky=2
! videoconvert
! autovideosink sync=false
```

## Queue

`leaky=2`는 queue가 찼을 때 오래된 buffer를 버려 지연 증가를 방지합니다.

```text
max-size-buffers=N
max-size-bytes=0
max-size-time=0
```

로 buffer count를 주 제한으로 사용합니다.

주의: decoder 이후 queue를 늘리는 것은 이미 손실된 압축 RTP/H.264 packet을 복구하지 못합니다.

## Proxy bypass

필요 시:

```text
GIO_USE_PROXY_RESOLVER=dummy
NO_PROXY=*
no_proxy=*
```

를 자식 gst-launch 환경에 적용합니다.

## Reconnect

V10.9:

```text
PLAY
 ↓
정상 영상
 ↓
RTSP/gst-launch 종료 또는 watchdog
 ↓
server recovery wait
 ↓
retry
 ↓
실패 시 backoff
 ↓
PLAY 성공 → reset
```
