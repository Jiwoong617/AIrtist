# EOS P2P 어댑터 — 2026-09-30

후속으로 [EOS 로비 연결부](EOS_LOBBY_REPORT.md)와 [연결 합의·전원 준비](EOS_CONNECTION_REPORT.md)를 추가했다. 현재 `PrepareLobbyConnection()`이 서비스 명단으로 연결을 합의하고 P2P를 내부 생성하며 `TakeConnection()`이 준비된 연결을 Core에 전달한다. 아래 명단 공급/합의 미구현 설명은 P2P 최초 단계의 기록이다. 온라인 화면·실제 서비스 검증은 여전히 남아 있다.

## 결과와 범위

`IRoundTransport`를 구현하는 `EosRoundTransport`와 EOS C# SDK 1.19.2.1 호출부 `EosSdkP2PApi`를 추가했다. 실제 SDK의 연결 알림·수락/거절·송수신·큐 제한·종료 API에 맞춰 컴파일했다. `EosDeviceSession`에서 인증 후 전송기를 만들고 같은 네트워크 스레드의 Tick과 종료 수명에 묶는다.

**실제 EOS 로그인·P2P 송수신은 미검증이다.** `config/eos.local.json`의 Product/Sandbox/Deployment/Client ID·Client Secret이 아직 비어 있다. 이번 P2P 검사는 SDK 호출 경계의 대역을 사용한 한 PC 모의 검사이며 EOS 인증·NAT·릴레이·여러 회선 검증이 아니다. 실제 로비 생성/검색/참가, 명단 공급과 온라인 화면은 아직 없다. 기존 `testbuild`는 갱신하지 않았다.

## 연결·권한

- 호스트와 각 참가자 사이에만 연결한다. 클라이언트끼리 직접 연결하지 않는다.
- `EosTransportContext`의 명단은 **EOS 로비 서비스로 확인한 명단**이어야 한다. 패킷이나 UI가 주장하는 목록으로 생성하면 안 된다. 현재는 이 목록을 가져오는 로비 연결부가 남아 있다.
- 생산용 생성은 `EosDeviceSession.CreateRoundTransport`를 통해서만 한다. 실제 인증 상태와 Local ID 일치, 기존 전송기 종료, 새 연결 범위를 검사한다. 명단 배열을 복사하고 이후 변경으로 허용 대상을 늘리지 않는다.
- 연결 범위 GUID를 32자 소켓 이름으로 쓴다. 송신자·수신 로컬 ID·소켓 이름은 SDK가 반환한 값으로 검사한다. 패킷 본문으로 송신자를 정하지 않는다.
- 명단·방장·연결 범위 변경과 인증 상실·연결 중단/종료 시 실패로 전환한다. 늦게 온 연결 알림으로 되살리지 않는다. 알림 해제·해당 소켓 큐 정리·연결 닫기를 수행한다. SDK 콜백 중 실패했을 때의 정리는 이후 Pump/Dispose에서 한다.
- 임시 연결 준비 마감은 30초, 연결 요청 패킷 간격은 500ms다. 게임 패킷은 필요한 상대 연결이 모두 준비된 뒤에만 받거나 보낸다. Core에는 그전까지 `IsConnected=false`로 전달한다.
- SDK `SendPacket`의 Success는 **큐에 접수됨**이며 상대의 수신/게임 승인 완료가 아니다. 기존 Core의 ACK·재시도·응답 확인을 유지한다.

## 채널과 상한

전송 인터페이스의 enum·수신 레코드·인터페이스를 작은 `AIrtist.Transport` 프로젝트로 옮겼다. 공개 네임스페이스와 기존 값은 유지하고 채널 숫자를 명시적으로 고정했다. Core와 EOS 모듈이 공유하며 EOS 검사 도구에 AI/WPF 의존성이 따라오지 않는다.

| 채널 | 내용 | 허용 방향 |
|---|---|---|
| 0 / 1 | 라운드 제어 | 참가자→방장 / 방장→참가자 |
| 2 / 3 | 이미지 조각 | 참가자→방장 / 방장→참가자 |
| 4 / 5 | 경기 제어 | 참가자→방장 / 방장→참가자 |
| 6 / 7 | 대기방 제어 | 참가자→방장 / 방장→참가자 |
| 8 | P2P 연결 요청용 1바이트 | 허용된 상대에게만, 게임 데이터로 전달하지 않음 |

- 애플리케이션 패킷은 **1~1,024바이트**다. 고정 SDK 최대 패킷은 1,170바이트이며 수신 버퍼도 1,170바이트로 잡아 초과 패킷을 잘라 유효 데이터로 오인하지 않는다.
- EOS incoming/outgoing 큐 각각 **256KiB**를 SDK에 요청한다. 송신 전 실제 SDK 큐 정보를 조회해 outgoing **256개·256KiB**를 넘기는 요청은 거절한다. SDK 제한은 이상적인 큐 크기이며 전체 프로세스 메모리의 절대 상한을 뜻하지 않는다.
- 자기 자신에게 보내는 방장 루프백은 최대 256개·각 1,024바이트이며 데이터 복사 후 보관한다.
- 한 번의 Receive는 결과 최대 256개, SDK 큐 탐색도 최대 256개다. 잘못된 패킷도 탐색 예산을 소비한다. 별도 무제한 수신 큐는 만들지 않는다.
- 게임 패킷은 `UnreliableUnordered`, 자동 연결 수락 금지, 지연 배달 금지다. 재전송·순서·무결성은 기존 Core가 맡는다. 연결 준비용 패킷에만 지연 배달을 허용한다.
- 인증 세션당 사용한 연결 범위 기록은 256개다. 같은 범위를 재사용하지 않는다. 라운드가 바뀌어도 같은 경기 연결을 유지하며 참가자 변경/재입장 시에는 새 범위가 필요하다. 서비스에서 같은 참가자 ID의 재입장을 감지해 범위를 교체하는 작업은 로비 연결부 책임이다.

이 수치들은 임시 보호 정책이다. 16인 인터넷 성능·실제 SDK 메모리 사용량·회선별 처리량을 검증한 값이 아니다.

## 검사

| 검사 | 결과 |
|---|---|
| EOS 어댑터/검사 도구 Release 빌드 | 경고 0 / 오류 0 |
| `p2p` SDK 경계 대역 검사 | 3개 독립 전송기, 8종 채널, 송신자·방향·소켓/명단·잘못된 크기·버퍼 복사 통과 |
| 큐/처리량 경계 | 1,024바이트·256개·256KiB 경계, 혼잡 시 재시도 가능 상태, 거절 패킷 256개 탐색 상한, 루프백 소비/복구 통과 |
| 연결 수명 | 준비 전 차단, 제한된 연결 요청 반복, 늦은 알림, 이탈·시간 초과, 명단/방장/범위 변경, 초기화/송수신 실패, 다른 스레드 접근·중복 해제 통과 |
| 실제 native SDK 초기화/종료 | 기존 `native` 검사 재실행 통과. 온라인 인증 검사가 아님 |
| 기존 전체 `unit` | 그림그리기·멀티 규칙·투표·이미지/제어 복구·대기방·여러 라운드 통과. 메모리 내 연결/모의 AI |
| WPF Release 빌드·`lobby-smoke` | 경고 0 / 오류 0, 대기방 준비/설정/경기 시작 화면 검사 통과 |

대기방 화면 검사 출력: `outputs/eos/p2p-lobby-ui-20260930`. 마지막 추가 검사에서 초기화 실패 후 늦은 콜백도 거절하도록 수정했고 P2P 검사를 재실행했다. 실제 AI 그림 재생성은 이번 전송 작업의 검사에 포함하지 않았다.

## 재현

SDK 준비는 [EOS_SETUP](EOS_SETUP.md)을 따른다. `native`와 `p2p`는 별도 검사이며 `p2p` 성공을 실제 EOS 연결 성공으로 설명하면 안 된다.

```powershell
dotnet restore tests/AIrtist.EosChecks --configfile NuGet.Config --locked-mode
dotnet build tests/AIrtist.EosChecks -c Release --no-restore
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- p2p
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- native
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- unit .
```

## 후속 연결 순서

1. 포털 설정 입력 후 실제 Device ID 로그인·만료 갱신·취소/재시도 검증.
2. EOS Lobby 생성·검색/참가·이탈과 서비스 명단/방장 확인. 공통 application LobbyId, 참가자 SessionId, 호환성 정보를 합의한다.
3. 합의한 새 범위와 인증 명단으로 `CreateRoundTransport` 호출 후 `EosDeviceSession.Tick`을 계속 실행한다. **자신의 P2P 준비와 전체 방의 시작 준비는 별개다.** 후속 로비 연결부가 전원 준비를 확인한 뒤 `MultiplayerLobbySession`을 생성해야 하며, 현재 P2P Ready만으로 Core 대기방 타이머를 먼저 시작하면 안 된다.
4. 같은 스레드에서 DeviceSession Tick → Core LobbySession Tick을 실행한다. 멤버/범위 변경은 P2P와 Core 양쪽에 전달하고 기존 경기를 중단한다. Core 대기방 종료 → 전송기 종료 → 인증 세션 종료 → 앱 종료 시 SDK Shutdown 순서다.
5. 온라인 화면 연결 후 다른 회선의 2대 PC에서 실제 PNG 전송, 3명 투표/결과 일치와 2명 동점 구조를 확인한다. 전체 배포 크기와 깨끗한 PC 검증도 남아 있다.
