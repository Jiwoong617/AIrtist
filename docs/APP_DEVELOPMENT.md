# 그림그리기 앱 개발·실행

**2026-10-17 투표 화면 종료 수정:** 투표 카드의 접근성 이름 조회 중 발생하던 스레드 접근 예외를 수정했다. `check-local.ps1 -Players 2`로 2인 검사도 지원한다. 최종 `testbuild`에서 실제 2인 생성·그림 전송·1:1 결과까지 확인했으며 전체 **943,598,864바이트 / 444개 파일**이다. [원인·재현·검사](GALLERY_CRASH_REPORT.md).

**2026-10-17 재개 완료:** 중단 직전 publish에만 있던 최종 앱 DLL을 `testbuild`에 반영했다. 단일 창·설정·실제 생성과 통합 대기방·닉네임·3인 TCP 2라운드를 최종 EXE에서 재검사했으며 전체 **943,596,302바이트 / 444개 파일**이다. [재개·검사 기록](SHELL_LOBBY_REPORT.md).

**2026-10-15 단일 창·통합 대기방:** `MenuWindow`가 창과 공통 설정 패널을 소유하고 `GameScreen` 콘텐츠를 교체한다. `ThemeManager`/`DynamicResource`로 라이트·다크를 적용하며 `WaitingRoomCoordinator`가 닉네임·방 설정·준비를 EOS 속성 또는 로컬 TCP로 동기화한다. 기본 대기방에서 방장이 설정을 적용하고 다른 참가자 전원 준비 후 게임을 시작하며 기존 연결 합의는 내부에서 진행한다. 규칙은 `multiplayer-waiting-v8`. 최신 `testbuild`는 **943,594,766바이트 / 444개 파일**이며 실제 생성·로컬 3인 경기·실제 EOS 단일 참가자 속성 저장을 확인했다. [사용·구현·검사·재현](SHELL_LOBBY_REPORT.md). 아래 새 창/두 대기방 관련 설명은 이전 상태다.

**2026-10-14 화면 통일:** 공통 UI 리소스는 `src/AIrtist.App/Theme.xaml`이다. 메인·싱글·멀티의 배경, 카드, 버튼, 입력창을 이 테마에 맞춘다. 투표·결과에서는 그림 영역을 전체 폭으로 표시하고 작성 단계에서 입력 UI를 복원한다. 최신 `testbuild`는 **943,547,171바이트 / 444개 파일**이다. [구현·검사·재현](UI_THEME_REPORT.md).

**2026-10-14 최신:** 메인 메뉴 디자인과 같은 PC 여러 프로세스의 자동 방 코드 연결을 추가했다. 기본 연결부는 `AutomaticRoomFactory`이며 로컬 등록이 없는 코드는 EOS로 조회한다. 생성 GPU 작업은 프로세스 간 순차 실행한다. 최신 `testbuild`는 **943,543,075바이트 / 444개 파일**이다. 로컬 3프로세스 검사는 `scripts/check-local.ps1`, 상세 사용법과 제한은 [로컬 멀티 보고서](LOCAL_MULTIPLAYER_REPORT.md)를 따른다.

**2026-10-08 최신:** 멀티 플레이 진입을 방 생성/방 참가 카드로 바꾸고 방 코드 생성·입력·복사와 내장 설정 자동 접속을 연결했다. 최신 `testbuild/AIrtist.exe`는 943,484,515바이트 / 444개 파일이다. 개발 빌드의 내장 설정 준비는 `scripts/prepare-online-config.ps1`이며 `build-app.ps1`이 로컬 설정 존재 시 호출한다. 키를 소스에 직접 적지 않는다. [동작·검사·재현](ROOM_CODE_REPORT.md). 아래 파일 선택/설정 미포함은 이전 상태다.

**2026-10-07 메인 메뉴·바로 그리기:** 시작 화면은 흰 배경의 AIrtist 제목과 그림 그리기·멀티 플레이·설정·종료 메뉴다. 싱글·멀티 모두 그림 그리기 한 번으로 내부 번역 후 생성하며 번역 확인 화면을 표시하지 않는다. 멀티는 번역 완료 후 기존 금지어 검사와 방장 승인을 거쳐 생성권을 소비한다. 설정은 창 해상도와 BGM/SFX 음량을 저장하며 사운드 재생은 아직 없다. 아래 번역 확인·상단 메뉴 관련 설명은 과거 기록이다. [구현·검사·테스트 빌드](MAIN_MENU_REPORT.md).

**2026-10-07 테스트 빌드:** 최신 소스를 Release로 다시 게시해 `testbuild/AIrtist.exe`에 반영했다. 모델·런타임을 포함해 **943,443,042바이트 / 444개 파일**이다. 최종 경로에서 실제 파라미터 생성 4장과 자유/고정 모의 온라인 검사가 통과했고 패키지 파일 해시를 확인했다. 기존 `testbuild`는 `.cache/testbuild-before-20261007`에 보존했다. 아래 별도 패키지 경로는 이전 작업 당시 기록이다.

**2026-10-06 최신:** 좌측 CFG·Steps·Seed와 하단 프롬프트 UI를 추가했다. 방 생성의 “방 파라미터 설정”으로 공통 고정/참가자 자유 설정을 선택한다. 최신 실행 파일은 `testbuild-parameters/AIrtist.exe`, 전체 943,444,066바이트 / 444개 파일이다. 실제 생성 4장과 자유/고정 모의 3인·2라운드, 대기방 UI 검사를 통과했다. 실제 여러 PC 온라인 검증은 남아 있다. [사용법·구현·검사](GENERATION_PARAMETERS_REPORT.md). 아래 경로·버전·25단계 고정은 해당 날짜의 기록이다.

현재 소스와 최신 `testbuild`는 **그림그리기 모드·경기 미리보기·개발용 온라인 방**을 제공한다. 그림그리기는 제시어·AI 채점·작성 마감 없이 번역 확인과 반복 생성을 진행한다. 온라인 방은 EOS 인증·로비·전원 연결 준비와 기존 경기 화면을 전용 스레드로 연결했다. 실제 EOS 서비스는 포털 설정이 없어 미검증이다. 2026-10-04 패키지는 GPU 공급자 재사용·취소 수정을 포함한 **943,404,694바이트 / 444개 파일**이며 수정본 30장·복구 검사와 배포 EXE 실제 생성·경기·모의 온라인·최종 실행 검사를 통과했다. [최신 수정·패키지 보고서](GPU_RESOURCE_FIX_REPORT.md), [온라인 구현·제약](ONLINE_UI_REPORT.md)을 따른다.

## 실행과 동작

동봉 패키지는 `D:\AIrtist\testbuild\AIrtist.exe`를 옵션 없이 실행한다. `testbuild` 폴더 전체에 모델·런타임·EOS DLL/고지가 들어 있다. 온라인 접속 설정은 포함하지 않으며 화면에서 별도로 선택한다.

최신 개발 EXE는 `--models D:/AIrtist/models/app-stage3 --eos-config D:/AIrtist/config/eos.local.json`으로 실행한다. 상단 **온라인 방**에서 설정 파일을 직접 선택할 수도 있다. 로컬 설정은 배포·Git에 포함하지 않는다. WPF 빌드에는 이제 고정 EOS SDK가 필요하다. `--online-smoke`는 실제 서비스/AI 없이 전용 스레드와 화면·경기 연결을 검사한다. 아래 날짜별 설명의 미연결 언급은 해당 단계 당시 상태다.

2026-10-01 연결 합의 단계 정리: 공통 연결/참가 세션 ID·전원 P2P 준비와 Core 대기방 전달을 추가했다. 모의 3인에서 합의부터 기존 대기방/경기 시작까지, 전체 규칙 검사를 통과했다. WPF 온라인 화면/작업 스레드는 아직 연결 전이며 현재 화면은 기존 로컬 미리보기다. [연결 합의 구현·검사](EOS_CONNECTION_REPORT.md)

2026-09-30 후속으로 별도 EOS 모듈에 로비 생성/검색/참가/나가기와 서비스 명단 확인을 추가했다. `lobby` 대역 검사는 통과했으며 앱 화면에서는 아직 사용할 수 없다. 공통 연결 범위·전원 준비 확인과 온라인 화면 연결, 실제 서비스 검증이 남아 있다. [로비 구현·검증](EOS_LOBBY_REPORT.md)

2026-09-30 `IRoundTransport` 계약을 `AIrtist.Transport`로 분리하고 별도 EOS P2P 어댑터를 추가했다. 기존 전체 규칙과 대기방 화면 검사를 통과했으며, 온라인 로비/화면과 실제 송수신은 아직 연결 전이다. WPF 앱은 작은 공유 계약만 참조하고 EOS SDK는 자동으로 포함하지 않는다. [P2P 구현·검증](EOS_P2P_REPORT.md)을 참고한다.

2026-09-29 EOS C# SDK 1.19.2.1과 별도 인증 모듈/검사 도구를 추가했다. WPF 앱의 기존 빌드에는 아직 포함하지 않았다. 실제 native 초기화·종료만 검증했고 온라인 인증은 포털 설정 입력이 필요하다. [SDK 검사와 로그인 명령](EOS_SETUP.md#sdk-빌드인증-검사), [구현·검증 범위](EOS_AUTH_REPORT.md)를 따른다.

최신 경기 미리보기는 **대기방 만들기 → 역할별 준비 완료 → 방장: 경기 시작** 순서다. 방장이 설정을 바꿔 적용하면 전원이 다시 준비해야 한다. 준비/취소·호환성·시작 권한을 Core에서 확인한다. 실제 온라인 방 검색/접속은 아직 없으며 [대기방 보고서](LOBBY_REPORT.md)에 구현 경계와 검사를 기록했다.

이 개발 PC에서는 `testbuild/AIrtist.exe`를 실행한다. 모델·.NET·WPF·ONNX Runtime·DirectML이 포함된 폴더 전체가 필요하다. 런타임에 모델을 내려받거나 Python을 실행하지 않는다.

1. 사과·토끼처럼 대상 이름을 포함해 원하는 그림을 2~160자로 작성하고 **번역 확인**을 누른다.
2. 영어 묘사를 2~320자로 확인·수정한다. **원문 다시 쓰기**로 돌아가도 원문을 유지한다.
3. **확정하고 그리기**로 생성한다. 수정한 영어 문장을 다시 번역하지 않는다.
4. 완성 후 **다시 그리기**로 같은 묘사의 새로운 그림을 만든다. **묘사 수정**에서는 영어를 수정하거나 원문으로 돌아갈 수 있다.

작성·번역 확인에는 시간 제한이 없다. 번역과 생성 각각의 기술적 처리 제한은 현재 180초이며 제품 최소 사양을 의미하지 않는다. 한 번에 한 AI 작업만 실행한다. 취소 요청 후 작업 정리가 끝날 때까지 새 작업을 받지 않고 늦은 결과를 폐기한다. 실패·취소·시간 초과 후 다시 시도할 수 있으며 마지막 완성 그림은 새 그림이 완성될 때까지 보관한다.

두 입력란의 Enter는 줄바꿈이며 기본 제출 버튼이 아니다. 영어 확인란에는 한글·특수 토큰·길이 위반을 허용하지 않는다. 대상 이름 금지 검사는 그림그리기에 적용하지 않는다. 자동 번역의 의미 왜곡은 남아 있으므로 확인·수정 기능을 정확도 개선으로 설명하지 않는다.

일반 실행은 입력과 최근 완성 그림 한 장을 메모리에 보관한다. 파일 저장·보관함은 구현하지 않았으며 앱을 닫으면 보관되지 않는다. 진단 명령만 지정한 폴더에 결과를 저장한다.

이전 9월 23일 `testbuild`의 **투표 미리보기**는 참가자 2~4명의 PNG를 불러와 투표했다. 현재 소스와 패키지의 상단 **경기 미리보기**에서는 인원·라운드 수(1~10, 기본 3)·첫 제시어·작성/생성 시간을 정한다. 각 역할에서 묘사 → 번역 확인·수정 → 최종 제출을 진행하고 모든 유효 그림이 전달되면 다른 사람에게 투표한다. 전원 결과 수신/AI 정리를 확인하면 방장의 **다음 라운드**가 나타난다. 마지막에는 라운드별 득표 기록을 표시한다. 이후 제시어는 목록을 섞어 추첨하며 최종 우승 기준은 미정이다. **다시 설정**은 새 경기이며 이전 기록을 지운다. 창을 닫으면 진행 중 AI를 취소하고 기록/이미지를 해제한다. 같은 PC의 모델 작업은 직렬화하며 대기 시간도 각자의 마감에 포함한다. 작성 기본 300초·생성 180초는 미리보기 임시값이다.

모델을 중복 복사하지 않고 최신 소스를 실행하려면:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet build src/AIrtist.App -c Release --no-restore
& ./src/AIrtist.App/bin/Release/net10.0-windows/win-x64/AIrtist.exe --models D:/AIrtist/models/app-stage3
```

`data/words.json`은 일반 빌드와 publish 출력에 복사된다. `--models`는 검증한 로컬 모델 경로 지정이며 다운로드를 수행하지 않는다. 새 복제본은 별도로 모델 준비가 필요하다.

## 소스 구조

2026-09-28 최신: `MultiplayerMatchRuntime.cs`는 경기 전체 제어 채널을 유지하고 라운드별 실행기를 교체한다. 설정·순서·이전 결과 해시·현재 ID·호스트 권한을 검사하고 전원 결과 확인/AI 종료 전에는 전환하지 않는다. 기록에는 숫자 결과만 남기고 이전 PNG는 해제한다. 현재 호환 규칙은 `multiplayer-match-v6`다. 아래 v5 이전 내용은 과거 단계다.

현재 단계: `MultiplayerRoundRuntime.cs`가 한 참가자·한 라운드의 패킷 루프, 호스트 타이머/상태/그림/결과 배포와 취소 가능한 AI 작업을 묶는다. `RoundTransport.cs`는 SDK 독립 전송 인터페이스와 제한된 메모리 내 미리보기 전송이다. `RoundPreviewWindow`는 실행기에 명령을 보내고 상태를 표시하며 투표 집계·승인 권한을 직접 판단하지 않는다. 현재 규칙은 `multiplayer-round-v5`이며 아래 v4 이전 내용은 단계별 기록이다.

2026-09-27 후속 `ReliableImageSendQueue.cs`는 업로드·갤러리의 누락 조각 재전송·완료 확인·공통 속도/메모리 제한을 맡는다. 생성 실패 통지도 제어 dispatcher에 연결했다. 현재 규칙 버전은 `multiplayer-round-v4`이며 아래 v3/v2 기록은 이전 단계다. WPF·실제 AI/EOS 실행 연결은 남아 있다. [이미지 전송 복구·검사](IMAGE_RECOVERY_REPORT.md)

2026-09-27 `RoundControlDispatch.cs`, `MultiplayerSubmissionClient.cs`에 시작·최종 제출·생성 승인·이미지 전송 허가와 기존 갤러리/투표 메시지 분배를 연결했다. `ReliableControlEndpoint`는 응답 등록 후 ACK하며 송신 대기열이 가득 차도 요청 처리를 반복하지 않는다. 현재 규칙 버전은 `multiplayer-round-v3`다. WPF·AI 실행·EOS 연결은 남아 있다. [구현·검사 보고서](ROUND_DISPATCH_REPORT.md)

2026-09-26 `ReliableControlChannel.cs`에 상대·라운드별 제어 메시지 분할·재전송·처리 후 ACK·대기열/속도 제한을 추가했다. `ControlTransportChecks.cs`는 누락·중복·상한·마감과 실제 로컬 UDP를 검사하고, `GallerySyncChecks.cs`는 이 채널로 갤러리·투표 제어 메시지를 전달한다. 제품 EOS 어댑터·이미지 재시도는 별도이며 [제어 전송 보고서](CONTROL_TRANSPORT_REPORT.md)에 연결 계약을 기록했다.

2026-09-25 `GalleryProtocol.cs`와 `MultiplayerRoundClient.cs`에 목록/이미지 배포·수신 확인·참가자별 투표/결과 적용을 추가했다. 호스트는 그림 수집 후 갤러리 수신을 기다린 다음 투표한다. 규칙 버전은 `multiplayer-round-v2`다. 기존 WPF와 실제 EOS는 아직 연결 전이며 [동작·검사·남은 범위](GALLERY_SYNC_REPORT.md)를 참고한다.

2026-09-24 멀티플레이 기반 소스를 추가했다. `MultiplayerRoundHost.cs`는 호스트의 승인·수집·마감·투표 진입, `MultiplayerProtocol.cs`는 SDK 독립적인 계약·호환성·이미지 패킷을 담당한다. 기존 WPF 미리보기와 실제 EOS에는 아직 연결하지 않았다. [검사 결과](MULTIPLAYER_FOUNDATION_REPORT.md), [EOS 준비](EOS_SETUP.md)

- `src/AIrtist.App/MainWindow.xaml`, `.xaml.cs`: 자유 입력·번역 확인·그림·다시 그리기 화면. 제시어와 경기 설정·점수 UI를 제거했다.
- `src/AIrtist.Core/DrawingController.cs`: 제시어·채점 메서드가 없는 `IDrawingAi`, 입력/초안 상태·실행 버전·단일 작업·취소·시간 초과·실패 복구. 번역·생성은 작업 스레드에서 실행한다.
- `InputRules.cs`: 공통 길이·형식 검사와 기존 멀티용 금지 별칭 검사를 분리했다.
- `WordCatalog.cs`, `data/words.json`: 멀티플레이용 ID·표시명·뜻·영어명·금지 별칭. 채점 벡터와 독립적으로 읽고 패키지의 `data/words.json`에 복사한다. 그림그리기는 이를 읽지 않는다.
- `RuntimeAssets.cs`, `data/runtime-assets.json`: `drawing-v1`/버전 2의 공통 자산 목록. 모델·토크나이저·설정·고지 28개만 요구하며 런타임과 패키저가 같은 목록을 사용한다.
- `tests/AIrtist.Checks/DrawingChecks.cs`: 직접 대상명·확인/재작성·반복·중복/오래된 명령·실패/취소/시간 초과·종료 검사.
- `AssetAndWordChecks.cs`, `DrawingStability.cs`: 독립 단어 목록·자산 무결성 검사와 실제 반복 생성·취소/시간 초과 복구 검사.
- `VotingRound.cs`: 완성 PNG 검사·소유권·참가자·투표 단계·한 표·멱등 재전송·호스트 마감·확정 결과. UI는 집계하지 않는다.
- `VotingWindow.xaml`, `.xaml.cs`: 그림 목록·자기 그림 표시·선택·결과·로컬 참가자 역할 전환.
- `VotingChecks.cs`: 3인 집계·2인 동점·위조/중복/오래된 요청·마감 경계·결과 직렬화·이미지 검증.
- `App.xaml.cs`, `scripts/check-app.ps1`: 현재 앱의 `--self-check`, `--smoke`, `--drawing-smoke`, `--voting-smoke`, `--round-smoke`, `--match-smoke` 진단. `--round-smoke`는 실제 번역·생성 3회와 한 라운드를, `--match-smoke`는 6회 생성·2라운드와 기록 일치를 확인한다. `-ModelDirectory`로 개발용 모델 경로를 지정할 수 있다.

구형 `MatchController`, `RoundController`, `ImageScorer`, 채점 데이터 타입과 전용 전처리·검사는 삭제했다. 현재 패키지는 채점 모델·벡터·TinyCLIP 전용 고지를 포함하지 않는다. 구형 버전 1 모델 manifest는 현재 앱에서 거절한다.

생성용 CLIP 텍스트 인코더·토크나이저, ONNX Runtime·DirectML과 공용 이미지 표시 기능은 계속 필요하다. 채점 코드 정리 과정에서 함께 지우지 않는다. 과거 경기/채점 기록은 [3단계](APP_STAGE3_REPORT.md), [4단계](APP_STAGE4_REPORT.md), [채점 실험](APP_QUALITY_REPORT.md)에 남아 있다.

## 빌드와 패키징

SDK는 `global.json`의 **.NET 10.0.103**, 자체 포함 런타임은 **10.0.3 / win-x64**다. NuGet 버전은 각 프로젝트의 `packages.lock.json`으로 고정한다. [EOS SDK 준비](EOS_SETUP.md)도 필요하다. 앱 참조를 통해 EOS managed/native DLL과 고지가 빌드·publish에 포함된다. 새 패키지의 전체 크기는 별도로 재측정해야 한다. 준비된 `models/app-stage3`가 있는 이 PC에서는 Python 없이 다음 명령을 사용한다.

```powershell
.\scripts\build-app.ps1 -CheckRules -PackageDirectory testbuild-new -PackageReport outputs/build/package-size.json
```

출력 폴더는 없는 새 경로여야 한다. 빌드 스크립트는 잠금 파일 기반 복원·규칙 검사·Release publish·manifest 해시 검사·패키징을 수행한다. PDB·LIB는 제외하고 모델·런타임·고지를 포함한 실제 바이트 합을 기록한다.

모델을 의도적으로 변환·교체했거나 자산 구성을 변경한 경우에만 아래 준비 명령으로 `data/MODEL_CHANGES.txt`와 manifest를 갱신한다. 일반 빌드는 기존 해시를 검증하며 변경된 파일에 자동으로 새 해시를 부여하지 않는다.

```powershell
.\scripts\prepare-runtime-assets.ps1 -ModelsDirectory models/app-stage3
```

새 저장소 복제에는 Git에서 제외한 모델이 없다. 원본 재현이 필요하면 [Tiny-SD 준비](../experiments/tiny_sd/README.md), [경량화·ONNX](../experiments/tiny_sd/STAGE2.md)와 `experiments/app_stage3` 도구를 참고한다. `download_sources.py`는 번역·공용 고지만 준비하고 `prepare_models.py`는 번역·생성 변환만 제공한다. Python의 `package_app.py`도 위 PowerShell 준비·패키징을 호출한다. 과거 채점 실험은 별도 개발 자료이며 앱 모델 경로로 재설치하는 도구는 제거했다.

## 검사

최신 `LobbySessionChecks`는 공유 상태가 없는 3인 대기방의 메시지/ACK 누락·준비/설정 동기화·시작 확인·취소 경합·시간 초과·경기 연결을 검사한다. 전체 `unit` 또는 `dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- lobby-session .`로 실행한다. 경기 미리보기도 이 세션을 사용한다. [구현 범위와 제약](LOBBY_SYNC_REPORT.md)

`LobbyChecks`는 준비/설정/명단/버전/권한·재전송·접속 교체·동시 시작을 검사하며 전체 `unit` 또는 `dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- lobby .`로 실행한다. WPF `lobby-smoke`는 모델 파일을 검증한 뒤 AI 추론 없이 3역할 준비→경기 진입→다시 설정을 검사한다. 실행 명령은 [대기방 보고서](LOBBY_REPORT.md)를 따른다.

`MatchRuntimeChecks`는 3인 2라운드의 누락 복구·기록 일치·전환 권한·결과 확인 대기·늦은 AI·이전 패킷/요청·잘못된 시작/상태·이탈/응답 없음·무투표 경기를 검사한다. 전체 `unit` 또는 `dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- match-runtime .`로 실행한다. 실제 생성 6회와 화면 검증은 [여러 라운드 보고서](MATCH_RUNTIME_REPORT.md)의 `match-smoke` 명령을 따른다.

`RoundRuntimeChecks`는 실행기의 자동 진행, 패킷 누락, 3인 결과 일치, 공유 AI 직렬화·대기 취소·마감 뒤 늦은 결과 폐기, 0/1장 결과 동기화와 상태 위조/역행을 `unit`에서 검사한다. 실제 AI를 사용하는 WPF 검사는 [경기 실행기 보고서](ROUND_RUNTIME_REPORT.md)의 `round-smoke` 명령으로 실행한다. 메모리 내 전송이며 EOS/다른 PC 검증은 아니다.

`ImageSendChecks`와 `RoundDispatchChecks`는 이미지 선택 재전송·메모리/속도/대기열 상한·마감·생성 실패/완료 통지를 검사한다. `dotnet run --project tests/AIrtist.Checks -c Release --no-build --no-restore -- image-recovery .`는 기존 실제 PNG 3장으로 제어/이미지 패킷을 모두 누락시키며 모의 라운드를 완주한다. 실제 소켓·EOS·AI 실행 검사가 아니다. [최신 결과와 입력 경로](IMAGE_RECOVERY_REPORT.md)

`unit`의 `RoundDispatchChecks`는 시작/호환성·요청/승인 대조·한 번만 생성·이미지 전송 허가·응답 대기/마감과 메모리 내 3인 라운드를 검사한다. `dotnet run --project tests/AIrtist.Checks -c Release --no-build --no-restore -- round-dispatch .`는 기존 실제 PNG로 같은 흐름을 검사한다. 생성 승인 반환 횟수를 검사하며 AI를 실행하지 않는다. EOS/소켓·이미지 누락 복구 검증도 아니다. [상세 결과](ROUND_DISPATCH_REPORT.md)

`unit`에는 제어 메시지 분할·재전송·누락/중복·대기열/속도/마감 검사도 포함한다. `reliable-gallery`는 기존 PNG를 쓰는 메모리 내 3인 갤러리/투표 통합 검사이고, `control-loopback`은 한 프로세스의 실제 로컬 UDP 소켓 두 개로 임의 32KiB 제어 본문을 보내 누락 복구를 검사한다. 두 명령 모두 EOS나 다른 PC의 접속을 검증하지 않는다. [실행 결과·제약](CONTROL_TRANSPORT_REPORT.md)

```powershell
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- reliable-gallery .
dotnet run --project tests/AIrtist.Checks -c Release --no-build --no-restore -- control-loopback .
```

`unit`에는 독립된 참가자 상태 3개의 갤러리·준비 확인·투표/결과·실패 검사도 포함한다. `dotnet run --project tests/AIrtist.Checks -c Release --no-build --no-restore -- gallery-sync .`는 기존 실제 PNG 3장을 각 참가자에 전달해 바이트와 결과 일치를 검사한다. 결과 경로와 메모리 내 모의 전송의 한계는 [보고서](GALLERY_SYNC_REPORT.md)에 기록했다.

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet restore tests/AIrtist.Checks --configfile NuGet.Config --locked-mode
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- unit .
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- reference .
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- stability .
.\scripts\check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/drawing-mode/check-new -Mode drawing-smoke
.\scripts\check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/drawing-mode/layout-new -Mode self-check
```

`unit`는 그림그리기·단어·자산·투표와 멀티플레이 승인/이미지 패킷 Core 검사를 실행한다. 구형 점수·싱글 경기 검사는 제거했다. 작은 PNG 픽스처와 수동 시계를 사용해 실제 대기 없이 마감 직전·정각 요청을 구분한다. `dotnet run --project tests/AIrtist.Checks -c Release --no-build --no-restore -- multiplayer-transfer .`는 이 PC의 기존 생성 PNG 3장을 패킷으로 분할·재조립하고 투표 결과를 검사한다. 실제 EOS·소켓 전송이 아니며 자세한 입력·결과 경로는 [보고서](MULTIPLAYER_FOUNDATION_REPORT.md)를 따른다.

투표 화면 검사는 서로 다른 실제 PNG 3장으로 실행한다. 아래 경로는 이 개발 PC에 남겨 둔 기존 생성 결과이며 신규 복제본에서는 별도 PNG를 준비한다.

```powershell
.\scripts\check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/voting/check-new -Mode voting-smoke -VotingImages @('outputs/scoring-removal/packaged-drawing/generated.png','outputs/scoring-removal/packaged-drawing/generated-repeat.png','outputs/scoring-removal/packaged-drawing/generated-revised.png')
```

`--voting-smoke`는 PNG를 생성하지 않고 불러와 모의 3인 투표·자기 표 거절·1/2/0표 집계·결과 일치·초기화와 100%/150% 렌더를 검사한다. 결과 JSON의 직렬화는 실제 네트워크 결과 동기화 검증과 구분한다.

`reference`는 로컬 `outputs/app-stage3`의 기존 참조 자료를 사용해 생성용 CLIP 토크나이저·Marian 번역·DPM 스케줄러를 비교한다. 신규 복제본에서는 해당 Python 참조 실험을 먼저 재현해야 한다. `stability`는 같은 프로세스에서 6장 생성과 생성 중 취소·10초 기술 제한·복구를 검사한다. 시간 초과 사례만 짧은 제한을 가진 별도 컨트롤러를 사용하며 AI 실행 객체는 공유한다. 장시간 내구성·저사양 검증은 별도다.

`soak`는 실제 번역·생성을 더 오래 반복하는 별도 진단이다. 기본 30장, 묘사 6종과 재생성, 10장마다 취소/10초 제한 각 1회와 이후 복구를 검사한다. 같은 프로세스와 공유 직렬 AI를 사용하며 작업 중 강제 GC 없이 시간·메모리·핸들/스레드 수를 기록한다. 시간 초과만 별도 컨트롤러로 유도한다. 첫 묘사별 대표 PNG와 마지막 2장만 보관한다.

```powershell
dotnet build tests/AIrtist.Checks -c Release --no-restore
.\scripts\check-drawing-soak.ps1 -OutputDirectory outputs/drawing-soak/run-new -Images 30 -MaxMinutes 30
```

`Images`는 10~300의 10 단위 값이다. 새 출력 경로를 사용하며 `MaxMinutes`를 넘기면 이 명령으로 시작한 진단 프로세스만 종료한다. 스크립트 성공 종료와 `results.json`의 `status=passed`·완료 장수를 확인한다. 중단된 `running` 기록은 통과가 아니다. `process.json`은 GPU 전체 표본과 실제 런타임 경로/해시를 기록한다. 검사는 **apphost EXE**로 실행하며 ORT/DirectML이 검사 폴더 외부에서 로드되면 실패한다. `dotnet ...dll` 실행은 시스템 DirectML이 선택될 수 있어 위 스크립트를 사용한다. 모든 작업 종료 후의 강제 GC는 잔류 자원을 구분하기 위한 별도 진단으로만 기록한다. WPF 조작·패키지 최초 실행·인터넷 멀티플레이 검사와 구분한다. [측정 결과와 제약](DRAWING_SOAK_REPORT.md)

2026-10-03 후속 `resource-probe`는 추론·컨트롤러·PNG 없이 CPU/GPU 모델 세션을 각각 4회 생성·해제하고 매번 GC 후 자원을 기록한다. `.\tests\AIrtist.Checks\bin\Release\net10.0-windows\AIrtist.Checks.exe resource-probe . outputs/drawing-resource/run-new`로 실행한다. 이어 공급자 생성/해제만으로 핸들 증가를 재현해 GPU 공급자를 재사용하고 취소를 native 호출 사이에서 처리하도록 수정했다. 수정 후 30장·취소/시간 초과 각 3회를 통과했으나 무제한 실행의 누수 부재를 보장하지 않는다. [수정·검사·제약](GPU_RESOURCE_FIX_REPORT.md)

`provider-probe`는 새 공급자만 준비/해제, 새 공급자+디코더, 공유 공급자+디코더를 각각 12회 기록한다. `gpu-lifetime`은 없는 모델 후 회복·동시 생성의 프로파일 분리·종료 멱등성·종료 후 새 생성 거절과 살아 있는 세션의 추론을 검사한다. 위 `resource-probe` 명령에서 모드와 출력 경로를 각각 바꿔 실행한다. 공급자/자원 진단의 `recorded`는 누수가 없다는 판정이 아니다. 실제 GPU 오류·장치 리셋 복구는 별도 검사가 필요하다.

`--self-check`는 manifest·화면 배치·Enter 설정·그림그리기 초기화를 검사한다. `--smoke`는 한국어 대상명 직접 입력·영어 확인·한글 확정 거절·원문 복귀·영어 수정·실제 생성을 검사한다. `--drawing-smoke`는 같은 묘사로 재생성과 새로운 원문으로 생성까지 실제 그림 3장을 만든다. 진단 창은 표시하지 않으며 화면 렌더와 결과 JSON을 저장한다.

`check-app.ps1`은 새 출력 폴더만 허용하고 네이티브 런타임의 패키지 내 로딩 경로와 메모리를 수집한다. GPU 생성 검사는 동시에 실행하지 않는다. GPU 사용량은 다른 앱을 포함한 전체 값이다. 렌더 검사는 실제 사람의 IME 입력·모니터 DPI 변경·별도 저사양 PC 검증을 대신하지 않는다.

기존 참조·번역 후보 실험 명령은 각각 [과거 품질 비교](../experiments/app_quality/README.md), [후속 번역 재현](../experiments/translation_quality/README.md)을 참고한다. 후보 TC big은 미채택 상태이며 현재 번역 모델을 바꾸지 않았다.

## Git 보관 범위

소스·단어 목록·패키지 잠금 파일·문서·최종 보고서를 보관한다. `.cache`, `.venv`, `models`, `dist`, `testbuild*`, `bin`, `obj`, 생성 이미지·로그·프로파일·실행 결과 JSON은 `.gitignore`로 제외한다. 문서의 `outputs/`·`testbuild/` 링크는 결과를 보관한 개발 PC에서만 열린다.
