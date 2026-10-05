# 대기방 준비·경기 시작 — 2026-09-28

후속 작업으로 참가자별 상태·준비·시작 확인을 제어 채널에 연결했다. 최신 미리보기는 독립된 상태 3개를 사용한다. [대기방 동기화 보고서](LOBBY_SYNC_REPORT.md)를 참고한다. 아래 공유 호스트 직접 조작 설명은 초기 대기방 단계 기록이다.

경기 미리보기에 **대기방 생성 → 역할별 준비 → 방장 시작 → 기존 여러 라운드 경기**를 연결했다. 방장 권한과 준비 판정은 Core에 있고 버튼 비활성화에 의존하지 않는다. 한 PC의 로컬 미리보기이며 실제 온라인 접속은 아니다. `config/eos.local.json`과 기본 SDK 경로 `sdks/eos`는 이번 확인에서도 없었다.

## 변경 파일

- `src/AIrtist.Core/MultiplayerLobbyHost.cs`: 참가자 목록, 경기 설정, 호환성, 준비 상태, 시작 허가를 관리한다. WPF·EOS SDK 타입과 비밀값을 받지 않는다.
- `src/AIrtist.App/RoundPreviewWindow.Lobby.cs`, `RoundPreviewWindow.xaml/.cs`: 대기방, 역할별 준비/취소, 방장의 설정 적용/시작을 표시하고 시작 계획으로 기존 경기 실행기를 연다.
- `tests/AIrtist.Checks/LobbyChecks.cs`, `Program.cs`: 대기방 권한·재전송/역순·설정/명단 변경·호환성·시작 경합 검사를 추가했다.
- `App.xaml.cs`, `scripts/check-app.ps1`: AI 생성 없이 대기방부터 경기 입력 단계 진입·초기화까지 확인하는 `lobby-smoke`를 추가했다. 기존 실제 AI `round-smoke`/`match-smoke`도 대기방 준비를 먼저 수행한다.

## 준비와 시작 규칙

임시 정책은 **참가자 2명 이상 전원 준비 후 방장만 시작**이다. 2인 경기를 금지하지 않으며 Core 상한 16명과 로컬 UI의 2~4명 선택을 유지했다. 제품 인원·투표의 최종 기획을 확정한 것은 아니다.

설정을 바꾸거나 참가자가 들어오고 나가면 대기방 revision을 올리고 전원의 준비를 취소한다. 같은 설정이나 순서만 바뀐 같은 명단 통지는 준비를 유지한다. 화면에서 아직 적용하지 않은 설정이 있으면 준비·시작을 막는다. 첫 제시어 선택은 로컬 미리보기 편의 기능으로 방을 만들 때 고정하며 온라인 방 옵션으로 추가하지 않았다.

준비 요청은 방 ID·현재 revision·인증된 송신자/참가자 ID·접속 세션 ID를 대조한다. 재참가 시 연결부는 새 세션 ID를 부여해야 한다. 증가하는 sequence로 이전 준비 요청이 나중 준비 취소를 덮어쓰는 것을 막는다. 동일 요청 재전송은 멱등하게 처리하고, 동일 sequence의 다른 내용은 거절한다.

통신 버전·규칙·모델 manifest 해시·단어 목록 해시가 다르면 준비 완료로 만들지 않고 다른 항목을 상태로 구분한다. 해시 일치 주장은 실제 모델 검증이나 변조 방지 보장이 아니다. 각 PC는 `RuntimeAssets.Verify`를 통과한 값만 보고해야 한다.

시작 판정과 명단 고정은 하나의 잠금 안에서 수행한다. 같은 시작 요청 재전송은 같은 MatchId/계획을 반환하고 다른 중복 시작은 거절한다. 고정된 설정·명단·호환성을 `MatchRuntimeContext`로 전달하며 외부 배열 변경이 이를 수정하지 못하게 한다.

방장 이탈/소유자 변경은 방을 닫는 임시 정책이다. 시작 후 명단 변경도 `Closed`로 전달하며 자동 호스트 이전이나 중도 참가로 취급하지 않는다. 실제 어댑터는 이를 관찰하면 경기의 `Abort()`를 호출해야 한다. 이미 받은 시작 계획은 변경하지 않는다.

## 실제 연결부의 경계

`UpdateMembership`은 **신뢰할 수 있는 연결부가 확정한 전체 명단**을 받는다. 클라이언트가 보낸 명단을 그대로 넣으면 안 된다. 연결부가 명단 통지를 정리하고 증가하는 version을 부여한다. 오래된 version은 무시하고 같은 version의 충돌은 거절한다. 이는 EOS가 같은 형태의 revision을 제공한다는 뜻이 아니다.

인증된 송신자 식별, 대기방 상태/준비/시작 메시지의 직렬화·배포·수신 확인, 멤버 속성과 세션 연결, 연결 허용 목록, 온라인 오류/재시도 화면은 아직 없다. 로컬 UI는 한 개의 대기방 호스트를 직접 조작하고 경기 진입 후 `LocalRoundNetwork`를 사용한다. **이 Core 클래스만으로 온라인 대기방 동기화가 완성되지 않는다.** 경기 패킷 형식은 바꾸지 않아 `multiplayer-match-v6`를 유지한다.

## 검증 결과

- 전체 `unit` 통과: 기존 그림그리기·투표·제출·제어/이미지 전송·한 라운드/여러 라운드 검사와 새 대기방 검사를 포함한다. 경기 검사는 메모리 내 전송·모의 AI다.
- 대기방 검사: 미준비·준비 취소·비방장 시작 거절, 설정 변경 후 전원 재준비, 잘못된 요청/방/세션/revision, 중복·역순·충돌 요청, 호환성 4종 불일치·복구를 확인했다.
- 명단 검사: 순서 변경/중복 통지 보존, 이전 소유자 통지 무시, 이탈·재참가·신규 참가 시 준비 초기화, 방장 변경/누락, 시작 후 명단 변경, 닫힌 방 재시작 거절을 확인했다.
- 상한/불변성 검사: 1인 시작 거절·2인 허용·16인 허용·17인 거절, 중복 ID/세션·잘못된 설정 거절, 입력 배열 변경 방어, 동시 시작 16건 중 1건만 성공했다. 실제 16인 네트워크 부하 검사는 아니다.
- Release 빌드: 경고 0 / 오류 0.
- WPF `lobby-smoke`: 3역할 준비/취소, 비방장 시작 거절, 설정 변경 후 재준비, 경기 입력 단계 진입, 다시 설정 통과. `outputs/multiplayer/lobby-ui-20260928/`에 상태 JSON과 화면 렌더를 남겼다.
- 기본 1116×764/96dpi와 작은 892×602/144dpi 대기방 렌더를 확인했다. 작은 창에서는 설명 영역을 스크롤한다. 실제 모니터 DPI 변경·IME 조합 입력 검사는 아니다.

이번에는 AI 추론·실제 이미지 생성·GPU 성능을 다시 측정하지 않았다. 이전의 3인 2라운드 실제 생성 결과는 [경기 보고서](MATCH_RUNTIME_REPORT.md)를 따른다. 새 모델·SDK·패키지를 내려받거나 중복 복사하지 않았으며 `testbuild`는 기존 패키지 그대로다. 진단 출력·개발 빌드는 기존 Git 제외 규칙을 적용한다.

## 실행과 다음 작업

최신 개발 EXE의 **경기 미리보기**에서 대기방을 만든 뒤 역할을 바꾸어 준비하고, 방장 역할로 돌아와 시작한다. 실행 경로는 [개발 안내](APP_DEVELOPMENT.md)를 따른다.

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- lobby .
dotnet build src/AIrtist.App -c Release --no-restore
./scripts/check-app.ps1 -PackageDirectory src/AIrtist.App/bin/Release/net10.0-windows/win-x64 `
  -ModelDirectory models/app-stage3 -OutputDirectory outputs/multiplayer/lobby-ui-next -Mode lobby-smoke
```

다음 온라인 작업에는 [EOS SDK·포털 설정](EOS_SETUP.md)이 필요하다. 준비되면 기기 ID 인증 → 실제 방 생성/참가 및 대기방 규칙 동기화 → P2P 경기 연결 → 서로 다른 PC의 이미지 전송·투표 검증 순서로 진행한다.
