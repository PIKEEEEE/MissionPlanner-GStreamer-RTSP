Mission Planner - GStreamer Smart Reconnect V10.9.1
=======================================================

V10.9 설정 영구 저장 버그 수정판입니다.

문제
----
V10.9의 SaveCfg()는 Settings.Instance["..."] = value 형태로
Mission Planner 메모리 설정만 변경하고 Settings.Instance.Save()를
호출하지 않았습니다.

따라서 RTSP 주소를 바꾸고 적용해도 현재 실행 중에는 새 주소가
사용되지만 Mission Planner 재실행 후 config.xml에 남아 있던
이전 주소로 돌아갈 수 있었습니다.

V10.9.1 수정
------------
- 설정 적용 시 Settings.Instance.Save() 호출
- 저장 실패 시 실제 오류 메시지 표시
- 저장 실패 시 적용/재시작 중단
- 정상 종료 시 Settings.Instance.Save()를 안전망으로 한 번 더 호출
- 기존 gst_v10_* 설정 key 유지
- V10.9 Smart Reconnect/Watchdog/Proxy bypass/decodebin3 구조 유지

설치
----
1. Mission Planner 종료
2. plugins 폴더의 V10.9 .cs 삭제
3. GStreamerSmartReconnectV10_9_1.cs만 복사
4. Mission Planner 실행
5. RTSP 주소 변경 -> 적용
6. Mission Planner 완전 종료 후 재실행
7. 변경한 주소가 유지되는지 확인
