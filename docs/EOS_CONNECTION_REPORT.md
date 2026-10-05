# EOS 연결 합의·전원 준비·Core 전달

구현·검사: 2026-09-30 / 중단 후 문서 마무리: 2026-10-01

후속: 2026-10-01 [온라인 방과 전용 작업 스레드](ONLINE_UI_REPORT.md)에 연결했다. 아래 화면 미연결 설명은 합의 단계 당시 상태다. 실제 EOS 서비스와 여러 PC 검사는 아직 미검증이다.

## 완료 범위

서비스 로비의 명단을 기준으로 공통 연결 ID와 참가자별 접속 ID를 합의하고, **전원 P2P 준비 확인 후** Core 대기방에 연결을 전달하는 단계를 추가했다. 검색용 호환성 해시 계산과 EOS 로비 속성 읽기/쓰기도 연결했다.

모의 서비스·메모리 내 전송으로 3명의 합의 → Core 대기방 → 역할별 준비 → 방장의 경기 시작을 검사했다. 서비스 명단 변경 후 전달된 연결이 닫혀 Core 대기방/경기가 중단되는 것도 확인했다. **실제 EOS 인증·로비 속성 전파·P2P·인터넷 접속은 미검증**이다. 포털 설정은 준비되지 않았으며 WPF 온라인 화면과 `testbuild`에는 연결하지 않았다.

## 변경 파일과 책임

| 파일 | 변경 |
|---|---|
| `src/AIrtist.Transport/LobbyConnectionBootstrap.cs` | SDK와 AI 의존성이 없는 연결 합의 상태, 공통 계약, 속성 검증·마감·전원 준비·한 번만 전달 |
| `src/AIrtist.Eos/EosLobbyBoundary.cs` | 서비스 명단에 연결 속성 포함, 내부 속성 저장 경계 |
| `src/AIrtist.Eos/EosLobbyClient.cs` | 속성 크기/명단 검증·복사, 방장/자기 멤버 저장 권한, 저장 중복·오래된 명단 응답 거절 |
| `src/AIrtist.Eos/EosSdkLobbyApi.cs` | 고정 SDK의 Lobby/Member attribute 조회와 Modification/Update 호출·해제 |
| `src/AIrtist.Eos/EosDeviceSession.cs` | `PrepareLobbyConnection`, Tick/수명 연결, 내부 전용 P2P 생성 |
| `src/AIrtist.Eos/EosRoundTransport.cs` | 공유 연결 준비 상태 계약 구현 |
| `src/AIrtist.Core/MultiplayerProtocol.cs` | 검증된 모델·단어·규칙·통신 정보의 결정적인 `ConnectionHash` |
| `src/AIrtist.Core/MultiplayerLobbySession.cs` | `FromPreparedConnection`의 호환성 재확인·Core 멤버 변환 |
| `tests/AIrtist.Checks/ConnectionBootstrapChecks.cs`, `Program.cs` | 합의/준비/실패와 Core 연결 검사, 전체 unit에 포함 |
| `tests/AIrtist.EosChecks/LobbyChecks.cs` | 속성 저장 권한·직렬화·복사·명단 변경 후 응답·초과 크기 검사 |

EOS 모듈은 Core의 AI/WPF 의존성을 참조하지 않는다. 공유 계약과 합의 로직만 `AIrtist.Transport`에 있고, 실제 경기 상태는 기존 Core에 유지한다.

## 합의 순서

1. 모델 파일 검증 후 `MultiplayerCompatibility.FromVerifiedAssets(...).ConnectionHash()`를 사용해 검색 버킷과 연결 호환성을 맞춘다. 통신 버전·규칙 버전·모델 manifest SHA256·단어 SHA256을 포함하며 해시 대소문자는 정규화한다. 검색 해시가 실제 파일 검증을 대신하지 않는다.
2. 현재 서비스 명단 2~16명이 각자 새 접속 GUID를 발급하고 자신의 `ART_MEMBER_V1` 속성에 접속 ID·호환성 해시·초기 준비 상태를 저장한다. `MembershipRevision`은 각 PC의 로컬 변경 감지에만 쓰며 여러 PC끼리 비교하지 않는다.
3. 방장이 서비스에서 읽은 정렬된 명단·접속 ID·호환성·서비스 Lobby ID·방장 ID로 명단 해시를 만들고, 새 공통 GUID와 `prepare` 상태를 `ART_CONNECT_V1`에 저장한다. 이 GUID가 P2P Socket 이름과 Core application LobbyId의 기준이다.
4. 참가자도 같은 서비스 명단에서 직접 해시를 계산해 제안을 확인한다. 자기 PC의 P2P가 준비되면 **합의한 공통 GUID에 대한 준비**를 자신의 멤버 속성에 기록한다.
5. 방장이 전원의 같은 GUID 준비를 확인하면 제안을 `run`으로 바꾼다. 각 PC는 현재 명단·해시·전원 준비·자신의 P2P 상태와 `run`을 모두 확인한 뒤 `TakeConnection()`을 한 번만 호출할 수 있다.
6. 반환된 `PreparedLobbyConnection`을 `MultiplayerLobbySession.FromPreparedConnection(...)`에 전달한다. 호환성을 다시 확인하고 참가자별 접속 GUID를 Core `LobbyMember.SessionId`로 사용한다. 표시 이름은 임시 `참가자 1…`이다. **연결 준비는 게임 준비 버튼이나 경기 시작을 대신하지 않는다.** 이후 기존 Core의 전원 준비·방장 시작·시작 계획 확인을 거친다.

속성은 EOS가 인증한 방장/멤버의 영역에서만 읽는다. 패킷 본문으로 명단이나 속성 작성자를 정하지 않는다. 친구 간 클라이언트 검증 수준이며 수정된 클라이언트가 거짓 준비를 표시하지 못한다는 보장은 아니다.

## 마감·변경·수명

- 속성 문자열 최대 256자, 준비 인원 2~16명이다. 준비 과정은 ASCII 형식을 쓰며 속성 하나에 전체 명단을 넣지 않는다.
- 서비스 저장은 한 번에 하나, 최소 500ms 간격이다. 저장 응답은 30초, 전체 연결 합의는 60초 제한인 임시 정책이다. 성공 콜백보다 서비스 캐시 반영이 늦어도 이전 상태를 새 합의로 오인하거나 같은 내용을 계속 재작성하지 않는다.
- 한 합의 객체는 생성 당시의 **고정 명단/방장/로컬 변경 번호**에 묶인다. 준비 중 또는 Core 전달 뒤 입퇴장·같은 ID 재입장·접속 ID 변경·방장 변경·서비스 상실이 발생하면 중단하고 연결을 닫는다. 준비 철회·`run`을 옛 `prepare`로 변경·합의 정보 삭제도 전달된 연결을 유지하지 않는다.
- 동적 명단 재합의나 자동 재접속은 구현하지 않았다. 변경 후에는 기존 Core/합의/인증 세션을 종료하고 같은 `EosLibrary` 안에서 새 인증 세션·로비 합의를 만든다. P2P 소켓만 같은 GUID로 다시 만들지 않는다. 온라인 화면에서 이 절차를 안내/연결하는 작업이 남아 있다.
- `EosDeviceSession.Tick`은 인증/서비스 알림 → 로비 갱신 → P2P Pump → 연결 합의 Tick 순서다. Core가 생성된 후 호출자는 같은 네트워크 스레드에서 Core Tick을 이어 실행한다. Core로 전달한 뒤에도 DeviceSession Tick을 계속 실행해야 이탈/합의 변경을 감지한다.
- 종료는 Core → 연결 합의 → P2P/로비 → 인증 Platform → 앱 종료 시 SDK Shutdown 순서다. 연결 합의/전송기는 중복 Dispose를 허용하며 늦은 저장 응답이 종료 상태를 덮어쓰지 않는다.
- `run` 속성이 모든 PC에 동시에 보인다는 보장은 없다. 전원이 연결 준비를 저장한 뒤 각자 `run`을 확인하며, 이후 Core의 기존 응답 마감이 적용된다. 실제 서비스의 지연/통지 순서는 후속 온라인 검사에서 확인해야 한다.

## 검증

| 검사 | 결과 |
|---|---|
| 새 `connection-bootstrap` | 3개 독립 서비스 보기·서로 다른 로컬 revision에서 공통 GUID/접속 ID 일치, 한 번만 전달 통과 |
| Core 연결 | 준비 전 전달 거절, 호환성 재검사, 3인 대기방/준비/경기 시작, 전달 후 명단 변경 중단 통과 |
| 인원/연결 지연 | 2명 및 16명 데이터 처리, 일부 참가자만 P2P 준비됐을 때 시작 금지 통과 |
| 저장/전파 지연 | 콜백 먼저/캐시 나중, 특정 참가자에게 `run` 미전달, 500ms 쓰기 간격·중복 쓰기 방지 통과 |
| 실패/오래된 정보 | 재입장·접속 GUID 교체·방장 변경·준비 철회·합의 삭제·옛 prepare·저장 실패·30/60초 마감·늦은 완료·다른 스레드·Dispose 통과 |
| EOS 로비 대역 | 방장 속성 권한, 자기 속성 저장 직렬화, 서비스 속성 복사, 예전 명단 응답·초과 크기 거절 통과 |
| 기존 P2P 대역 | 통과 |
| 전체 Core `unit` | 그림그리기·투표·갤러리·제어/이미지 복구·한/여러 라운드·대기방·새 연결 합의 통과 |
| EOS/검사 도구와 WPF Release 빌드 | 경고 0 / 오류 0 |

전체 unit 통과 뒤 합의 속성 삭제 처리도 보완하고 해당 연결 검사와 최신 빌드를 다시 통과했다. 이 검사는 9월 30일에 실행했으며 10월 1일 재개 시에는 저장 상태 확인과 문서 정리만 수행했다. 실제 AI 이미지를 새로 생성하거나 UI 자동 검사를 반복하지 않았다. 기존 화면은 이번 합의 객체를 아직 사용하지 않는다. 16명 검사는 데이터/모의 준비 검사이며 인터넷 처리량·최대 RAM/VRAM·최소 사양 검증이 아니다.

```powershell
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- connection-bootstrap .
dotnet run --project tests/AIrtist.Checks -c Release --no-build -- unit .
dotnet build tests/AIrtist.EosChecks -c Release --no-restore
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- lobby
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- p2p
dotnet build src/AIrtist.App -c Release --no-restore
```

## 다음 작업

온라인 방 화면과 네트워크 작업 스레드를 추가해 실제 인증 → 로비 생성/검색/참가 → 연결 준비 → Core 대기방/경기 → 종료를 연결한다. 포털 설정 후 실제 인증·갱신·로비 속성 전파·P2P를 확인하고, 다른 회선의 두 PC 이미지 전송·3명 투표·새 패키지 용량/깨끗한 PC 검증을 진행한다. [설정 준비](EOS_SETUP.md), [로비 구현 기록](EOS_LOBBY_REPORT.md).
