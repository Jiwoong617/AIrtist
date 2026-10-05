# 온라인 방 화면·전용 작업 스레드

구현·검사: 2026-10-01

2026-10-05 후속: 실제 EOS로 단일 참가자 인증·방 검색/생성/명단·나가기·동일 기기 재접속·정상 종료를 확인하고, 이미 종료된 worker의 창 닫기 예외를 수정했다. 최신 `testbuild`는 **943,420,637바이트 / 444개 파일**이다. [실제 검사·수정](ONLINE_LIVE_REPORT.md), [여러 PC 검사 절차](MULTIPC_TEST_GUIDE.md). 아래 설정 미준비/서비스 미검증은 10월 1일 당시 기록이며 여러 PC/P2P/온라인 투표는 계속 미검증이다.

후속: 같은 날 최신 `testbuild`를 교체하고 실제 AI 경기·전체 용량·최종 경로 실행을 검사했다. 2026-10-02 문서 마무리 기준 **943,403,670바이트 / 444개 파일**이다. 아래 미갱신 설명은 온라인 화면 구현 단계 당시 기록이며 실제 EOS 서비스 미검증 상태는 유지한다. [최신 패키지 보고서](TESTBUILD_RELEASE_REPORT.md)

## 완료 범위

개발 앱 상단에 **온라인 방**을 추가했다. 로컬 EOS 설정 파일 선택 → 기기 인증 → 방 만들기/검색/참가 → 참가자 전원 연결 준비 → 경기 대기방 → 전원 게임 준비 → 방장 시작을 연결했다. 기존 번역 확인·수정, 실제 생성 호출, PNG 전송, 투표, 여러 라운드와 기록 화면을 재사용한다. 온라인에서는 자기 참가자만 조작하며 역할 선택·첫 제시어 선택은 숨긴다.

**실제 EOS 서비스는 아직 미검증이다.** 로컬 설정의 `productId`, `sandboxId`, `deploymentId`, `clientId`, `clientSecret` 5개가 비어 있다. 이번 통합 검사는 모의 연결과 모의 AI PNG를 사용했다. `testbuild`와 설치 용량 기록은 갱신하지 않았다.

## 변경 파일

| 파일 | 책임 |
|---|---|
| `src/AIrtist.App/OnlineConnection.cs` | 기존 EOS 인증·로비·연결 합의 API를 감싸는 경계, 프로세스 동안 유지하는 SDK 소유자 |
| `OnlineSessionWorker.cs` | 전용 Thread, Tick·명령·Core 생성·종료, 읽기용 화면 상태 게시 |
| `OnlineRoomWindow.xaml`, `.xaml.cs` | 설정 선택, 이름 입력, 검색/생성/참가/연결 준비, 대기방 열기, 비동기 나가기 |
| `RoundPreviewWindow.Online.cs`, 기존 `.xaml`/`.xaml.cs`/`.Lobby.cs` | 로컬 경기 화면 재사용, 자기 참가자 상태와 스레드 명령 연결, 방장 설정·준비·시작 권한 표시 |
| `MainWindow.xaml`, `.xaml.cs` | 온라인 진입 버튼, 창 재개 시 같은 worker 재사용, 앱 종료 시 정리 대기 |
| `OnlineSmoke.cs`, `OnlineWindows.Smoke.cs`, `App.xaml.cs` | 명시적 `--online-smoke` 진단과 화면 렌더 |
| `AIrtist.App.csproj`, `packages.lock.json` | EOS 프로젝트 참조 추가, 고정 SDK native DLL·고지 전파 |
| `scripts/check-app.ps1`, `scripts/package-app.ps1` | 온라인 모의 검사, 패키지 필수 EOS 파일 확인·로컬 설정 제외·현재 안내문 |

SDK 타입은 경기 규칙에 추가하지 않았다. 기존 Core·Transport·EOS 모듈 구현은 변경하지 않았다.

## 실행

준비된 SDK가 있는 프로젝트 루트에서:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet restore src/AIrtist.App --configfile NuGet.Config --locked-mode
dotnet build src/AIrtist.App -c Release --no-restore
.\src\AIrtist.App\bin\Release\net10.0-windows\win-x64\AIrtist.exe --models D:/AIrtist/models/app-stage3 --eos-config D:/AIrtist/config/eos.local.json
```

온라인 방에서 설정 파일을 직접 고를 수도 있다. `--eos-config`가 없으면 실행 작업 디렉터리의 `config/eos.local.json`이 기본값이다. 파일 내용·비밀키·인증 토큰을 화면에 출력하지 않으며 선택한 파일을 자동 복사하거나 저장하지 않는다. 표시 이름은 기기 인증용이며 경기 표시명은 아직 `참가자 1…`이다.

1. [EOS 준비](EOS_SETUP.md)에 따라 계정 소유자가 포털 값을 로컬 파일에 입력한다.
2. 각 PC에서 접속하고, 한 명이 방을 만들면 다른 참가자는 검색 후 참가한다.
3. **참가자가 모두 모인 뒤 모든 PC에서 연결 준비**를 누른다. 현재 합의는 고정 명단이므로 준비 도중/이후 입퇴장이 생기면 나간 뒤 새로 모인다.
4. 전원 P2P 준비와 공통 연결 합의가 끝나면 각자 경기 대기방을 연다. 방장은 라운드 수와 작성·생성 시간을 적용하고 모두 준비한 뒤 시작한다.
5. 그림을 생성·제출하고 다른 사람의 그림 하나에 투표한다. 전원 결과 확인 후 방장이 다음 라운드를 연다. 창을 닫거나 방 나가기를 누르면 현재 접속을 정리한다.

기존 로컬 경기 미리보기는 계속 별도 버튼으로 사용할 수 있다. 그림그리기의 제시어·채점 없는 흐름도 유지한다.

## 스레드·수명과 임시 정책

- EOS 초기화/인증/로비/P2P/연결 합의, Core Tick과 모든 경기 명령, Dispose는 **한 전용 Thread**에서 실행한다. async Task의 스레드 이동에 의존하지 않는다. 루프 간 대기는 20ms, EOS Tick 예산은 기존 5ms다. WPF는 100ms마다 게시 상태만 읽는다. AI는 기존 취소 가능한 작업 실행기를 사용한다.
- 창을 다시 열거나 인증 세션을 다시 만들어도 같은 worker와 `EosLibrary`를 유지한다. SDK Shutdown은 앱 종료 때 한 번이다. 일반 창 닫기는 최대 5초의 나가기 응답 대기를 비동기로 처리한다. 강제 프로세스 종료/OS 종료 상황의 정리는 검증하지 않았다.
- 명령 대기는 32개, 일반 문자열 4,096자·보조 문자열 320자 상한이다. 접속 세대 번호와 기존 Core 라운드/초안/대기방 revision 검사를 함께 사용한다. 이전 접속 명령은 버리고, 나가기/종료 요청은 가득 찬 명령 큐를 우회해 대기 명령을 지운다.
- 모델 검증 후 호환성 해시를 계산한다. 연결 합의가 `Ready`일 때만 `TakeConnection`으로 Core를 만든다. 전달 뒤에도 서비스 Tick을 유지해 명단 변경을 감지한다. 연결 실패는 Core를 중단하고 확정된 라운드 기록을 화면에 유지한다. 나가기 후에는 Core와 세션을 폐기한다.
- 나가기는 서비스 응답을 최대 5초 기다린다. 응답이 없으면 로컬 정리를 완료하고 **서비스 나가기를 확인하지 못했다**고 표시한다. 생성/참가/인증 진행 중 종료는 즉시 로컬 객체를 정리하므로 서비스 측 취소 성공을 보장하지 않는다. 늦은 성공은 기존 EOS 연결부의 best-effort 정리 정책을 따른다.
- 방 정원 선택은 2~16명(기본 4), Core 경기 기본은 3라운드·작성 120초·생성 180초다. 로컬 미리보기의 작성 300초와 구분한다. 최종 제품 인원·타이머·투표 정책 확정이나 16인 성능 검증은 아니다.

## 수행한 검사

| 검사 | 결과 |
|---|---|
| Release WPF 빌드 | 경고 0 / 오류 0 |
| `online-smoke` | 별도 전용 스레드 3개 → 모의 연결 전원 준비 → Core 대기방 → 3명 × 2라운드, 모의 PNG 6개, 매 라운드 1/2/0표와 기록 일치 |
| UI/권한 | 온라인 역할 변경 없음, 참가자 설정/시작 비활성화와 Core의 방장 권한 검사, 번역 수정·라운드 초기화·자기 투표 비활성화 |
| 실패/정리 | 누락/잘못된 설정의 비밀값 미노출, backend 예외 미노출, 실패 후 기록 보존, 이전 접속 명령 거절, 같은 worker 재접속·종료 |
| 명령/종료 경계 | 32개 큐 상한·초과 문자열 거절, 가득 찬 큐에서 나가기 우선 처리, 무응답 나가기 5초 후 로컬 정리 및 미확인 표시 |
| 스레드 검사 | backend/transport의 모든 호출과 Dispose를 생성 worker에서만 수행, 세션 전부 닫힌 뒤 factory 종료 |
| 기존 WPF `lobby-smoke` | 로컬 3역할 준비·설정 변경·시작·초기화 통과 |
| 그림그리기 `self-check` | 기존 모델 검증·초기 화면·추가된 온라인 진입 버튼 렌더 통과. 실제 생성은 이번 검사에서 반복하지 않음 |
| 전체 Core `unit` | 그림그리기·투표·제어/이미지 복구·라운드/경기·대기방·연결 합의 통과 |
| EOS `p2p`, `lobby`, `native` | 기존 SDK 경계 대역 검사와 실제 native 초기화/종료 통과 |
| 빌드 산출물 | native DLL 19,548,600바이트 및 고지 포함, 고정 SHA256 일치, `*.local.json` 없음 |
| 패키징 스크립트 | PowerShell 구문 검사 통과. 새 전체 패키지는 만들지 않았으므로 설치 용량/새 패키지 실행은 미측정 |

최종 온라인 검사 결과·화면: `outputs/online-ui-20261001-03/`. 로컬 대기방 검사: `outputs/lobby-ui-20261001/`. 둘 다 Git 제외 개발 산출물이다. 화면 이미지를 확인했으며 작은 창의 스크롤과 상태 표시도 확인했다. 실제 IME 조합 입력·다양한 모니터 DPI·장시간 UI 응답 검사는 별도다.

재현 시 새 출력 디렉터리를 사용한다:

```powershell
.\scripts\check-app.ps1 -PackageDirectory src/AIrtist.App/bin/Release/net10.0-windows/win-x64 -OutputDirectory outputs/online-ui-next -Mode online-smoke -ModelDirectory models/app-stage3
```

## 남은 작업

포털 5개 설정과 클라이언트 정책 준비 후 실제 기기 로그인·취소/갱신 → 서비스 방 생성/검색/참가·명단/속성 전파·이탈 → 다른 회선 2대 PC의 실제 PNG 전송 → 3인 이상 투표·여러 라운드 결과 일치를 검사한다. 현재 모의 검사는 실제 EOS 성공 근거가 아니다.

그 뒤 최신 `testbuild`를 새로 만들고 전체 필수 바이트를 재측정한다. EOS 포함 설치 크기, 깨끗한 PC의 최초/오프라인 실행, 낮은 사양·장시간 반복·실제 IME/DPI 검사는 남았다. 자동 재접속·동적 명단 재합의·호스트 이전과 최종 우승 규칙은 구현하지 않았다.
