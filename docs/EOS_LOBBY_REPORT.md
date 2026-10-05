# EOS 로비 서비스 연결부 — 2026-09-30

후속으로 [연결 합의·전원 준비·Core 전달](EOS_CONNECTION_REPORT.md)을 추가하고 2026-10-01 문서를 정리했다. 현재 `PrepareLobbyConnection`이 서비스 속성을 통해 합의하며 P2P 생성은 내부 전용이다. 아래 후속 미구현 항목은 로비 최초 단계의 기록이다. 실제 서비스 검증과 온라인 화면은 여전히 남아 있다.

## 결과와 범위

`EosLobbyClient`와 `EosSdkLobbyApi`를 추가했다. EOS C# SDK 1.19.2.1의 방 생성·검색·참가·나가기, 서비스 참가자 명단 복사와 변경 알림을 연결했다. `EosDeviceSession`의 인증·Tick·종료 수명에 포함하고, P2P 생성 시 외부에서 전달한 명단 대신 현재 서비스 로비 명단을 사용하도록 변경했다.

**SDK 호출부 컴파일과 SDK 경계 대역 검사를 완료했으며 실제 온라인 로비는 미검증이다.** 준비 검사에서 Product/Sandbox/Deployment/Client ID·Client Secret 5개가 아직 누락/형식 오류로 표시됐다. 로그인·실제 로비 생성/검색/참가·P2P 송수신 성공으로 설명하면 안 된다. WPF 앱과 `testbuild`에는 이번 모듈을 아직 연결하지 않았다.

## 구현 파일

- `src/AIrtist.Eos/EosLobbyBoundary.cs`: SDK 타입을 노출하지 않는 검색 결과·서비스 명단·상태와 내부 SDK 호출 경계.
- `src/AIrtist.Eos/EosLobbyClient.cs`: 작업 직렬화, 30초 제한, 검색 결과 검증, 명단 복사·변경 감지와 늦은 응답 처리.
- `src/AIrtist.Eos/EosSdkLobbyApi.cs`: 고정 SDK API 호출, 검색/명단 핸들 해제, 알림 등록/해제와 서비스 나가기.
- `src/AIrtist.Eos/EosDeviceSession.cs`: 인증 이후 로비 생성, Tick/인증 상실/종료 연결, 서비스 명단에 근거한 P2P 생성.
- `tests/AIrtist.EosChecks/LobbyChecks.cs`, `Program.cs`: `lobby` 검사 명령과 경계 대역 검사.

## 현재 정책과 보장 범위

- 인증 세션당 로비 연결부 하나, 활성 방 하나, 동시에 작업 하나다. 기존 `EosLibrary`는 재사용하고 SDK를 Shutdown한 뒤 다시 초기화하지 않는다.
- 방 정원은 2~16명, 검색은 최대 20개다. 검색 가능한 공개 방을 사용하고 초대·Presence·RTC·자동 방장 이전·직접 ID 참가를 끈다. 최종 공개/비공개 방 정책과 최대 제품 인원을 확정한 것은 아니다.
- 검색 버킷은 호출자가 제공한 호환성 SHA256 전체를 URL 안전 Base64로 변환하고 `AIrtist-v1-`을 붙인다. 다른 버킷·잘못된 인원·만석 방은 표시하지 않는다. **해시 계산과 Core의 모델·단어·규칙·통신 호환성 확인은 후속 연결부에서 연결해야 한다.** 검색 필터가 실제 파일 검증을 대신하지 않는다.
- 현재 검색 결과의 SDK Details 핸들로만 참가한다. 검색 결과와 실제 참가 완료 시 서비스 명단을 다시 검사한다. 검색에 보였던 방도 참가 시에는 만석·닫힘 등으로 실패할 수 있다.
- 실제 명단은 `CopyLobbyDetailsHandle`/`GetMemberByIndex`에서 가져온다. 소유자와 자신 포함 여부, 중복·ID 형식·인원 상한을 확인하고 읽기 전용 복사본을 보관한다. 패킷 본문이나 UI가 전달한 명단을 받지 않는다.
- 로비 변경 번호는 **각 PC의 로컬 변경 번호**다. 여러 PC가 합의한 연결 ID나 참가 세션 ID가 아니다. 동일 Product ID의 나가기/재입장도 별도 변경으로 처리한다.
- 참가자 변경·나가기·인증 상실은 기존 P2P를 중단한다. 방장 변경·자신의 강퇴/연결 상실·방 닫힘·명단 확인 실패에는 로비도 실패 상태로 전환한다. 늦은 알림으로 복구하지 않는다. 자동 재접속·경기 상태 이전은 없다.
- Create/Search/Join/Leave는 30초 제한이다. 취소/시간 초과/실패 후 같은 연결부에서 새 요청을 시작하지 않는다. 인증 세션을 종료하고 같은 SDK 수명 안에서 새 인증 세션으로 재시도한다. 이는 이전 native 요청의 늦은 성공이 새 방을 오염시키지 않도록 하는 임시 정책이다.
- native 생성/참가 요청 자체는 취소하지 못한다. 살아 있는 연결부에 늦은 성공이 도착하면 나가기를 요청하고, Dispose 후 늦은 성공의 정리는 SDK 호출부가 맡는다. Dispose 시 알려진 방에서도 나가기를 시도한다. **확인된 나가기는 `Leave` 완료 응답을 받은 경우뿐이다.** 응답을 기다리지 않고 Platform을 즉시 닫은 경우 원격 정리 완료를 보장하거나 검증하지 않았다.
- 모든 조작은 인증 세션의 네트워크 스레드에서 수행한다. 검색·Details 핸들 및 알림을 해제하고, 비동기 콜백 소유자는 DeviceSession으로 통일해 Platform 해제 뒤 남은 C# 참조도 정리한다.

## 검증 결과

| 검사 | 결과 |
|---|---|
| EOS/검사 도구 Release 빌드 | 경고 0 / 오류 0 |
| `lobby` SDK 경계 대역 | 검색 필터·중복 제거·정원·동시 요청 거절·검색 결과로만 참가·명단 복사·나가기 응답 대기 통과 |
| 명단/알림 | 다른 방 알림 무시·갱신 알림 합치기·동일 ID 재입장·명단 추가·방장 변경·강퇴/끊김/닫힘 중단 통과 |
| 비동기 경계 | 30초 경계의 늦은 생성/참가·취소 후 검색 결과 정리·늦은 나가기·Dispose 후 상태 오염 방지 통과 |
| 잘못된 서비스 응답 | 다른 방 ID·다른 생성자·자신 누락·중복/잘못된 ID·버킷/정원 불일치·명단 조회 실패 거절 통과 |
| 수명 | 초기화 실패 정리·다른 스레드 요청 거절·중복 Dispose 통과 |
| 기존 `p2p` 대역 검사 | 통과 |
| 실제 `native` 초기화/종료 검사 | 1.19.2.1-58105819, 통과. 서비스 인증 검사는 아님 |
| 로컬 설정 준비 검사 | 설정 5개 미준비 확인. 비밀값 출력 없음 |

대역 검사는 실제 SDK의 검색 핸들 수명·통지 순서·서비스 나가기 완료를 검증하지 않는다. 특히 Dispose 뒤 native 성공의 원격 정리는 구현·컴파일 단계이며 실제 서비스 검증이 남아 있다. Core/WPF 동작은 이번 단계에서 변경하지 않아 전체 AI 생성·화면 검사를 반복하지 않았다. 설치 용량·16인 성능·다른 회선 테스트도 수행하지 않았다.

```powershell
dotnet build tests/AIrtist.EosChecks -c Release --no-restore
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- lobby
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- p2p
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- native
.\scripts\check-eos-setup.ps1
```

SDK 설치/잠금 복원은 [EOS_SETUP](EOS_SETUP.md)을 따른다. API 서명과 소유권은 로컬 고정 SDK `SDK/Source/Generated/Lobby`의 소스로 확인했다. 서비스 개요는 [Epic Lobby 문서](https://dev.epicgames.com/docs/epic-online-services/multiplayer/lobbies-and-sessions/lobby-interface)를 참고한다.

## 다음 연결 단계

1. Core 호환성으로 검색 해시를 계산하고, 방의 명단에 맞춰 공통 application LobbyId와 참가자 SessionId를 합의한다. 변경 시 새 연결 범위를 배포하고 기존 경기/대기방은 중단한다.
2. `CreateRoundTransport(sharedScopeId, expectedMembershipRevision)`로 현재 서비스 명단에 대한 P2P를 준비한다. 각 PC의 준비뿐 아니라 **전원 동일 명단/연결 범위 준비**가 확인된 뒤 Core `MultiplayerLobbySession`을 생성한다. 현재 로비 Joined나 P2P Ready만으로 시작하지 않는다.
3. 온라인 방 검색·생성·참가·준비/시작 화면을 WPF의 네트워크 작업 스레드에 연결한다. DeviceSession Tick → Core Tick 순서를 유지한다.
4. 포털 설정 후 실제 인증·로비/취소/갱신·나가기와 2대 PC 이미지 송수신, 3명 투표를 확인한다. 새 패키지의 전체 용량도 다시 측정한다.
