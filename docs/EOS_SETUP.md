# EOS 접속 준비 — 2026-10-04 갱신

**2026-10-05 후속:** 최종 WPF 패키지의 실제 인증·단일 참가자 방 검색/생성/명단·나가기·동일 기기 재접속·정상 종료를 확인했다. 종료된 worker의 창 닫기 예외를 수정했고 `--online-live-smoke`로 재실행할 수 있다. 다른 PC 준비 여부는 미확인으로 여러 PC/P2P/투표는 미실행이다. [실측·수정·명령](ONLINE_LIVE_REPORT.md), [2PC/3PC 검사 절차](MULTIPC_TEST_GUIDE.md). 아래 10월 4일의 WPF 미검증은 당시 상태다.

**2026-10-04 실제 서비스 검사 완료:** 사용자 로그인·조직 생성 후 AIrtist 제품을 만들고, 사용자 승인으로 EOS 개발자 계약 한국어 버전 4와 표준 서비스 부록을 수락했다. `AIrtist Game Client` 정책과 `AIrtist Windows` 클라이언트를 생성하고 아래 5개 값을 Git 제외 파일 `config/eos.local.json`에 저장했다. 고정 SDK 1.19.2.1의 Release 빌드(경고/오류 0)·native 해시·초기화/종료에 이어 **실제 Connect Device ID 로그인, 방 검색·생성·서비스 명단 조회·나가기와 나간 방의 검색 제외**를 통과했다. 한 PC/한 참가자의 검사이며 다른 PC 참가·P2P·이미지 전송·온라인 투표는 아직 미검증이다. 검사 도구에 `live-lobby`를 추가했으며 제품 코드와 `testbuild`는 바꾸지 않았다.

| 로컬 설정 필드 | 준비할 값 |
|---|---|
| `productId` | AIrtist용 EOS 제품의 Product ID |
| `sandboxId` | 위 제품에서 사용할 Sandbox ID |
| `deploymentId` | 위 샌드박스에서 사용할 Deployment ID |
| `clientId` | 해당 제품에 연결한 게임용 Client ID |
| `clientSecret` | 위 클라이언트의 Client Secret |

현재 환경은 AIrtist 제품의 기본 `Live` 샌드박스 / `Live Deployment`다. `sdkDirectory`는 기존 `sdks/eos`를 유지한다. 개발자 계정 로그인은 아래 **플레이어의 Connect Device ID 로그인**과 별개다. 계정 비밀번호·인증번호·Client Secret은 채팅/보고서에 기록하지 않고 비밀값은 로컬 설정에만 저장한다. 서로 다른 참가자의 실제 방 참가는 별도 PC에서 이어서 확인한다. 같은 PC/OS 사용자의 두 프로세스를 다른 기기로 가정하지 않는다.

**최신:** WPF **온라인 방**과 전용 Thread를 인증·로비·연결 합의·Core 대기방/경기에 연결했다. 모의 3인 2라운드와 실패/종료 검사에 이어 실제 서비스의 단일 참가자 인증·로비 호출까지 확인했다. 다음은 [온라인 화면](ONLINE_UI_REPORT.md)에서 다른 회선의 2PC 참가·연결 합의·실제 PNG 전송과 3인 투표 검사다. WPF 빌드에는 고정 EOS SDK가 필요하다. 아래 날짜별 미연결 설명은 이전 단계 기록이다.

## 2026-10-04 정책과 실측 범위

- 정책: Custom, **User required 활성화**, `lobbies:connect`, `lobbies:readLobby`, `lobbies:findLobbies`만 허용. 현재 포털의 Connect 항목 두 개는 다른 사용자의 계정 조회용 서버 권한이므로 활성화하지 않았다. 이 정책으로 실제 Device ID 로그인이 통과했다. P2P 연결은 아직 실측 전이다.
- 클라이언트: `AIrtist Windows`, 위 정책 연결. 별도 EAS 애플리케이션·리디렉션 URL·서버 IP 허용 목록은 사용하지 않는다. 개발자 계정/조직 권한을 게임 클라이언트에 부여하지 않았다.
- 네트워크 제한 환경의 첫 로그인은 `NoConnection`이었다. 승인된 네트워크 실행에서는 실제 로그인 성공, 이어 별도 프로세스의 `live-lobby`에서도 로그인 성공을 확인했다. 원시 SDK 로그·토큰·참가자 ID·비밀키를 검사 출력에 남기지 않는다.
- `live-lobby`는 실행마다 임의 호환성 범위를 사용해 게임 방과 분리한다. 빈 범위 검색 → 정원 2명 방 하나 생성 → 서비스가 반환한 방장/명단 1명·빈자리 1개 확인 → 나가기 응답 → 나간 방이 없는 검색 결과까지 통과했다. **다른 참가자가 존재하는 방 검색/참가나 P2P 성공의 근거는 아니다.**
- 기록: `outputs/eos-setup-20261004/`의 `readiness.json`, `native.log`, `login-network.log`, `live-lobby-build.log`, `live-lobby.log`. 로컬 설정·검사 출력의 Git 제외를 확인했다. 테스트 방은 나가기 완료, 로컬 기기 인증은 다음 실행에 재사용한다.
- 다음 검사는 인증 취소/갱신·서비스 속성 전파·다른 PC 참가/이탈·전원 P2P 연결 준비·실제 그림 전송·3인 투표/여러 라운드다. 이 단계에서 WPF 실제 온라인 화면 검사를 완료했다고 보고하지 않는다.

2026-10-01 연결 합의 단계 정리: 호환성 해시·공통 연결 ID·참가 접속 ID·전원 P2P 준비 확인과 Core 대기방 전달을 추가했다. 9월 30일 모의 3인 합의부터 Core 경기 시작까지, 명단 변경/지연/오래된 합의 거절과 전체 unit·Release 빌드를 검사했다. 실제 서비스와 온라인 화면은 아직 미검증/미연결이다. [연결 합의·검사·임시 제한](EOS_CONNECTION_REPORT.md)

2026-09-30 후속으로 EOS 로비 생성/검색/참가/나가기와 서비스 명단 조회·알림을 구현했다. 대역 검사는 통과했으며 실제 서비스 호출은 아직 미검증이다. P2P는 현재 서비스 명단에서만 생성한다. 공통 연결 범위·참가 세션 합의, 전원 준비 확인과 온라인 화면 연결이 남아 있다. [로비 연결부·검사·후속 순서](EOS_LOBBY_REPORT.md)

2026-09-30 P2P 전송 어댑터를 SDK 호출부까지 구현하고 경계 대역 검사·기존 전체 규칙·대기방 화면 검사를 통과했다. 포털 설정 5개는 여전히 비어 있어 실제 EOS 접속은 미검증이다. 로비에서 명단을 가져오고 전원 연결 준비 후 게임 실행기를 여는 연결부가 다음 구현 대상이다. [P2P 구현·검사·연결 순서](EOS_P2P_REPORT.md)

2026-09-29 공식 C# SDK 1.19.2.1을 확보하고 실제 DLL 초기화·종료를 검사했다. 기기 ID 인증 모듈과 검사 명령은 빌드됐으나 포털 설정이 없어 온라인 인증은 미검증이다. `config/eos.local.json`은 빈 예제로 준비했다. 앱은 아직 온라인 방에 연결하지 않는다. [SDK·인증 구현/검증](EOS_AUTH_REPORT.md)

SDK 독립적인 여러 라운드 진행·로컬 AI·기록 보존은 `IRoundTransport` 구현으로 실제 송수신에 연결할 수 있다. 현재 `LocalRoundNetwork`는 메모리 내 모의 전송이고 인증 서비스가 아니다. [최신 여러 라운드 검사](MATCH_RUNTIME_REPORT.md), [한 라운드 실행기](ROUND_RUNTIME_REPORT.md), [제어 전송·로컬 UDP 검사](CONTROL_TRANSPORT_REPORT.md)

## 준비 순서

같은 날짜의 후속 작업으로 SDK 독립적인 대기방 규칙과 로컬 준비/시작 화면을 추가했다. `MultiplayerLobbyHost`에 인증된 멤버 목록과 준비 요청을 공급하고, 실제 서비스에 상태를 배포하는 연결부는 아직 없다. [대기방 경계·검사](LOBBY_REPORT.md)를 함께 따른다.

1. [Epic 개발자 포털](https://dev.epicgames.com/portal/)에 개발자용 Epic 계정으로 로그인하고 이 게임용 제품을 만든다. SDK 다운로드·서비스 사용에 필요한 개발자 계약은 계정 소유자가 확인한다. **플레이어에게 Epic/Steam 로그인을 요구한다는 뜻은 아니다.**
2. 제품의 Product ID, 사용할 Sandbox ID와 Deployment ID를 확인한다. 게임 클라이언트를 생성하고 Client ID·Client Secret을 확인한다. 이 값은 채팅에 올리지 않고 아래 로컬 설정 파일에 직접 입력한다.
3. 클라이언트 정책에서 **Connect 기기 ID 생성/로그인, Lobbies, P2P**에 필요한 권한을 설정한다. 개발자 포털의 현재 정책 항목과 선택한 SDK 문서를 함께 확인한다. 플레이어용 클라이언트에 서버 관리 권한을 부여하지 않는다. Epic 친구 목록·소셜 오버레이·스토어 연동은 이번 연결 범위에 포함하지 않는다.
4. 이 PC에는 `D:\AIrtist\sdks\eos`에 필요한 SDK가 준비되어 있다. 다른 PC에서는 [공식 SDK 페이지](https://onlineservices.epicgames.com/sdk?lang=ko)의 **C# 언어 SDK**를 받는다. 현재 검증 버전은 1.19.2.1이며 `config/eos-sdk-lock.json`의 URL/해시로 식별한다. 아래 명령은 전체 압축의 해시를 확인하고 C# 소스·Win64 DLL·Common 참조 예제·고지만 추출한다. 기존 `sdks/eos`가 있으면 덮어쓰지 않는다. 전체 SDK 압축을 제품에 넣지 않는다.

   ```powershell
   .\scripts\import-eos-sdk.ps1 -ArchivePath 'C:\다운로드경로\EOSSDK.zip'
   ```
5. 프로젝트 루트에서 설정 예제를 복사하고 빈 값을 채운다. 기존 파일이 있으면 덮어쓰지 않는다.

   ```powershell
   if (-not (Test-Path -LiteralPath config/eos.local.json)) {
       Copy-Item -LiteralPath config/eos.example.json -Destination config/eos.local.json
   }
   .\scripts\check-eos-setup.ps1
   ```

   `sdkDirectory`의 상대 경로 기준은 프로젝트 루트다. `productId`, `sandboxId`, `deploymentId`, `clientId`, `clientSecret`은 포털의 값을 입력한다. 스크립트는 값 자체를 출력하지 않고 누락된 항목 이름과 SDK 파일 존재 여부만 확인한다. 종료 코드 2는 준비 전, 0은 로컬 준비 항목 존재를 뜻한다. **코드 0도 온라인 접속 성공이나 SDK 무결성 검증은 아니다.** 아래 `login` 도구와 WPF 온라인 방에서 이 파일을 사용한다. 앱 실행의 `--eos-config` 또는 설정 파일 선택으로 경로를 지정한다.

6. 준비 후 아래 로그인 검사로 실제 인증을 확인한다. 비밀번호·비밀키는 메시지로 전달하지 않는다.

## SDK 빌드·인증 검사

프로젝트 루트에서 실행한다. 2026-10-01 이후 WPF 빌드에도 EOS SDK가 필요하다. 아래 명령은 인증/연결 모듈을 별도로 검사한다.

```powershell
dotnet restore tests/AIrtist.EosChecks --configfile NuGet.Config --locked-mode
dotnet build tests/AIrtist.EosChecks -c Release --no-restore
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- native
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- p2p
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- lobby
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- settings outputs/eos/settings-check
# 포털의 실제 값을 로컬 파일에 입력한 뒤 실행한다. 기기 자격 증명/게임 참가자가 생성될 수 있다.
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- login config/eos.local.json
# 실제 서비스의 독립 검사 범위에서 임시 2인 방 생성·명단 조회·나가기까지 수행한다.
dotnet run --project tests/AIrtist.EosChecks -c Release --no-build -- live-lobby config/eos.local.json
```

`native`는 인증 없이 실제 DLL을 초기화·종료하며, `login`/`live-lobby`의 로그인 성공만 실제 기기 인증을 뜻한다. `lobby`와 `p2p`는 SDK 경계의 대역 검사다. `login`/`live-lobby`는 원시 SDK 로그·키·참가자 ID를 출력하지 않는다. Ctrl+C로 취소할 수 있으며 `live-lobby`는 진행 중인 native 작업을 마친 후 확인된 참가 방에서 나간다. 네트워크 실패 시 나가기 완료를 보장할 수 없으므로 종료 결과를 확인한다. SDK는 프로세스당 한 번 초기화하고 앱 종료 때 한 번 Shutdown한다. 인증 세션을 닫거나 재시도할 때는 같은 `EosLibrary`를 유지한다. 인증 취소/갱신·실제 서비스 오류 처리는 추가 검사해야 한다.

빌드 입력 SDK 경로 기본값은 `sdks/eos`다. 다른 위치를 쓸 때는 restore/build에 `-p:EosSdkDirectory=D:/SDK경로`를 함께 지정한다. 설정의 `sdkDirectory`는 준비 검사 경로이며 실행 중 DLL을 임의 경로에서 불러오는 기능이 아니다. 빌드는 Win64 DLL SHA256을 검사하고 실행은 native/managed 버전을 검사한다.

SDK 폴더, SDK 압축 파일과 `eos.local.json`은 Git에서 제외했다. 저장소에는 빈 예제만 보관한다. 최신 `testbuild`에는 EOS managed/native DLL과 고지를 포함하지만 로컬 설정 파일은 넣지 않았다. 2026-10-04 전체 실행 폴더는 943,404,694바이트다. [패키지 검사](GPU_RESOURCE_FIX_REPORT.md), [EOS 구성·외부 배포 전 확인 사항](EOS_DISTRIBUTION_REPORT.md)

## 연결할 동작

최신 Core 연결 경계는 `MultiplayerLobbySession`이다. 대기방 2종 + 경기 6종의 패킷을 분리하고 경기 시작 후에도 대기방 ACK를 처리한다. 이 세션은 명단에 고정되므로 입퇴장 시 기존 경기를 중단하고 새 명단과 연결 범위로 재구성해야 한다. [동기화·수명·한계](LOBBY_SYNC_REPORT.md)를 따른다. 공통 연결 범위/참가 세션 합의와 전원 준비 확인, `FromPreparedConnection`을 통한 Core 생성까지 연결했다. 실제 서비스 검증과 이를 사용하는 온라인 화면/작업 스레드는 남아 있다.

인증 방식은 **Connect Device ID**다. 로컬 기기 자격 증명 생성/재사용과 게임 Product User ID 취득을 `EosDeviceSession`에 구현했으며 실제 서비스 검증은 남아 있다. 기기 ID만으로 다른 PC에서 같은 계정이나 진행 데이터를 복구하지 않는다. [Connect API](https://dev.epicgames.com/docs/api-ref/interfaces/connect), [구현·제약](EOS_AUTH_REPORT.md)

연결 단계별 구현·검사 순서 (아래 이전 단계의 WPF 연결 예정 항목은 2026-10-01 [온라인 화면](ONLINE_UI_REPORT.md)에서 연결 완료, 실제 서비스 검증은 계속 필요):

1. SDK 초기화·주기적 Tick·기기 ID 인증·만료 처리·종료 시 객체 해제 코드는 추가했다. 초기화/종료는 확인했고 나머지 실제 서비스 검증과 WPF 백그라운드 연결이 남았다.
2. 방 생성/검색/참가/나가기와 서비스 멤버 목록 조회는 `EosLobbyClient`에 추가했다. 합의된 명단/접속 세션 ID를 Core로 전달하는 연결부도 추가했고 실제 호출 검증이 남아 있다. 로컬 MembershipRevision은 여러 PC가 합의한 번호가 아니다. Core가 중단 상태를 알리면 진행 중 경기를 이어가지 않는다. 현재 라운드 처리 상한 16명은 실제 EOS 16인 경기 검증 결과가 아니다.
3. 통신/규칙 버전, 검증된 모델 manifest와 단어 목록 해시 일치 확인 후 경기 시작.
4. 같은 로비의 허용한 상대만 P2P 연결 수락. SDK가 반환한 원격 사용자 ID를 Core의 `authenticatedPlayerId`로 전달한다. 메시지 안의 PlayerId로 대체하지 않는다. `PrepareLobbyConnection()`이 공통 범위와 전원 준비를 확인하며 현재 서비스 명단으로 P2P를 내부 생성한다. `TakeConnection()`은 준비된 연결을 한 번만 반환한다. 온라인 실측이 남아 있다.
5. 제어·이미지 패킷은 현재 1,024바이트 이하이며 고정한 SDK의 상한은 1,170바이트다. P2P 어댑터에 SDK 송수신과 큐 제한 호출을 연결했다. Core의 시작/제출/이미지 전송 허가·응답 대기와 누락 조각 재시도·완료 확인·실패 통지를 재사용한다. 호스트 갤러리는 상대마다 새 예산을 만들지 않고 하나의 `ReliableImageSendQueue`를 공유한다. 전송 시작 승인 전에 이미지 바이트를 보내지 않는다. 실제 EOS 큐 동작과 이미지 송수신은 설정 준비 후 검사한다.
6. 호스트가 수집한 유효 그림을 참가자들에게 배포하고 수신 완료를 확인한다. Core의 목록·PNG 배포·전원 확인 후 투표 시작을 구현했고 2026-09-26 제어 패킷 누락 복구와 3인 결과 일치도 메모리 내에서 검사했다. 실제 EOS 송수신·어댑터와 온라인 화면에 연결해야 한다.
7. `MultiplayerMatchRuntime`에 실제 인증된 ID·P2P 큐/연결 상태를 공급하고 로비/온라인 화면을 연결한다. 경기 전체 제어와 라운드 제어/이미지의 6종 패킷을 구분하며 라운드 교체 때 실제 연결을 닫지 않는다. `MultiplayerRoundRuntime`은 한 라운드의 타이머·AI·갤러리/투표를 맡는다. 여러 라운드·전원 결과 수신 확인·기록 보존과 임시 이탈 중단은 로컬에서 연결했다. 실제 EOS 이탈 감지·재접속·호스트 이전은 아직 없으며 로비 소유자 변경을 게임 상태 이전으로 취급하지 않는다.
8. 다른 회선의 2대 PC에서 접속과 실제 PNG 전송을 확인하고, 3명 이상으로 투표·결과 일치를 검사한다.

동일 PC·동일 OS 사용자로 실행한 여러 인스턴스가 서로 다른 Device ID 참가자가 된다고 가정하지 않는다. 현재 검사는 모의 참가자를 사용하며 실제 EOS 인증과 구분한다.

## 배포와 서비스 확인

2026-09-24 공식 안내에서 EOS의 게임용 무료 이용과 PC C#/C SDK 제공을 확인했다. 개발자 계약은 배포 가능한 SDK 구성요소를 게임에 포함하는 조건을 명시하므로, 전체 SDK를 배포 폴더에 복사하지 않고 **선택한 SDK의 재배포 구성요소와 고지**를 확인한다. [라이선스 안내](https://onlineservices.epicgames.com/licensing), [개발자 계약](https://onlineservices.epicgames.com/services/terms/agreements)

소셜 오버레이 사용 시 별도 EOS 재배포 설치 요구가 있으므로 현재의 Connect/Lobby/P2P 범위와 혼동하지 않는다. 실제 SDK 구성으로 결정한 뒤 필요한 DLL·설치 항목을 모두 합쳐 1,000,000,000바이트 목표를 다시 측정한다. [공식 SDK 1.18 안내](https://onlineservices.epicgames.com/news/epic-online-services-sdk-1-18-is-live-here-s-what-you-need-to-know)
