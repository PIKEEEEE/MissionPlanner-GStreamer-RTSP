Mission Planner - GStreamer Smart Reconnect V10.9
===================================================

V10.8 로그에서 확인된 문제:
- 자동 재연결은 실제로 동작하고 있었음
- 스트림 종료 후 카메라 RTSP 서버가 connection refused 상태
- V10.8은 약 2초 간격으로 계속 gst-launch를 새로 실행
- 임베디드 RTSP 서버가 회복할 시간을 거의 주지 못함
- gst-launch -v 때문에 RTP session stats가 대량으로 로그에 기록됨

V10.9 변경
----------
1. Smart reconnect / exponential backoff

정상 PLAY 후 스트림이 끊기면:
최소 8초 대기 후 재연결

새 연결이 실패하면:
8초 -> 16초 -> 30초 -> 30초 ...
형태로 재시도 간격 증가

정상 RTSP PLAY가 확인되면:
backoff 횟수를 즉시 0으로 리셋

Reconnect base delay 설정이 8초보다 크면 그 값을 시작값으로 사용합니다.
최대 backoff는 30초입니다.

2. 로그 폭주 감소

V10.8:
gst-launch -e -v

V10.9:
gst-launch -e

즉 -v 제거.
주기적으로 쏟아지던 RTP session stats 로그를 기본적으로 출력하지 않습니다.

3. GST_DEBUG

V10.8: 2
V10.9: 1

실제 치명 오류는 남기되 불필요한 진단량을 줄였습니다.

4. 기존 기능 유지

- Proxy bypass
- UDP/TCP/AUTO
- RTCP
- RTSP Keep Alive
- decodebin3 안전 경로
- Queue/Leaky
- Connect watchdog
- 고정 팝아웃 창
- 외부 gst-launch 프로세스
- GStreamer 로그 보기

권장 설정
---------
Proxy bypass: ON
Protocol: UDP
UDP retransmission: OFF
RTCP: ON
RTSP Keep Alive: ON

손실 대응 모드:
0 - 호환성 우선

Latency: 300
Queue: 6
Leaky: 2

Auto reconnect: ON
Reconnect base delay: 2 sec
Connect watchdog: 8 sec

참고:
Reconnect base delay를 2로 두어도 서버 복구 대기는 내부적으로 최소 8초입니다.

정상 동작 로그 예
----------------
[RECONNECT] RTSP PLAY reached. Backoff reset.
...
[RECONNECT] Stream dropped. Waiting 8 sec before retry.
...
[RECONNECT] Connection attempt failed. Retry #1 in 8 sec.
[RECONNECT] Connection attempt failed. Retry #2 in 16 sec.
[RECONNECT] Connection attempt failed. Retry #3 in 30 sec.
...
[RECONNECT] RTSP PLAY reached. Backoff reset.
