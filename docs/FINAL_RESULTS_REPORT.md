# 멀티플레이 최종 결과 화면 — 2026-10-18

## 확정 동작

- 모든 라운드의 확정 결과를 참가자 전원이 수신·확인하면 방장에게 **최종 결과** 버튼을 표시한다. 자동으로 결과 화면을 열지 않는다.
- 방장이 버튼을 누르면 연결된 참가자가 같은 최종 결과 화면으로 이동한다.
- **모든 라운드에서 받은 표의 합계**가 가장 높은 플레이어를 표시한다. 라운드 1위 횟수로 계산하지 않는다.
- 1위가 한 명이면 가운데 배치한다. 여러 명이면 공동 1위 카드를 가로로 배치하고, 화면 폭을 넘으면 스크롤바·마우스 휠·좌우 방향키로 이동한다.
- 카드는 닉네임과 누적 득표수를 표시한다. 닉네임이 같아도 참가자 ID로 따로 집계한다.
- 사용자 답변에 따라 전원 0표일 때는 1위 없이 **집계된 표가 없습니다**를 표시한다.
- 결과 화면의 **방 나가기**는 연결을 정리하고 **메인화면으로 바로 복귀**한다.
- 사용자 답변에 따라 방장이 먼저 나가도 나머지 참가자는 결과를 보다가 직접 나간다. 이때 결과 화면에 연결 오류를 덮어쓰거나 오류음을 반복하지 않는다.
- 기존 단일 창·라이트/다크·우측 상단 공통 설정·인게임 BGM을 유지한다. 별도 승리 음악이나 추가 순위표는 추가하지 않았다.

## 구현

`MatchFinalResults`가 확정된 라운드별 득표를 합산해 참가자별 총표와 공동 1위 목록을 만든다. 숫자·참가자 ID·라운드 순서를 검증하고 읽기 전용 목록으로 보관한다. 그림 없는 참가자는 0표이며 전체가 0표이면 우승 목록은 비어 있다.

`MultiplayerMatchRuntime.ShowFinalResults`는 방장·현재 마지막 라운드·전체 경기 완료·연결 상태를 확인한다. 참가자의 호출이나 이전 라운드 요청은 거절하고, 같은 요청은 한 번만 반영한다. 기존 경기 제어 메시지 `MatchProgress.FinalResultsOpened`를 신뢰성 채널로 전달한다. 조기/위조 요청·오래된 상태·결과 화면을 되돌리는 상태 변경을 거절한다. 이미 확인된 라운드 기록을 사용하므로 별도의 점수 패킷이나 새로운 이미지 전송은 필요 없다.

방장의 나가기 버튼은 연결된 참가자에게 결과 전환 메시지가 전달·확인될 때까지 잠깐 비활성화하고 전달 중 상태를 표시한다. 기존 채널의 재전송·시간 제한을 사용하며, 연결이 끊겼거나 전송 실패가 확정되면 결과를 보존하고 나갈 수 있다. 강제 종료·네트워크 단절 중 모든 PC의 화면 전환까지 보장하는 기능은 아니다.

`FinalResultsView`는 기존 테마의 카드·버튼을 사용하는 화면이다. 한 번 표시한 결과는 화면 객체에 보존하고, 정기 갱신과 테마 변경으로 가로 스크롤 위치를 초기화하지 않는다. 작은 창과 긴 닉네임도 줄바꿈/스크롤로 표시한다.

`RoundPreviewWindow`의 마지막 라운드 화면에서 최종 결과 화면으로 전환한다. 결과의 나가기 요청은 `OnlineRoomWindow`까지 닫아 메인 메뉴로 돌아간다. 일반 경기 중의 기존 방 나가기 동작은 그대로다.

메시지 필드가 추가되어 호환성 규칙을 **`multiplayer-results-v9`**로 변경했다. 기존 `multiplayer-waiting-v8` 빌드와 한 방에서 혼용하지 않는다.

주요 변경 파일:

- `src/AIrtist.Core/MatchFinalResults.cs`
- `src/AIrtist.Core/MultiplayerMatchRuntime.cs`, `MultiplayerProtocol.cs`
- `src/AIrtist.App/FinalResultsView.xaml`, `FinalResultsView.xaml.cs`
- `src/AIrtist.App/RoundPreviewWindow.xaml`, `RoundPreviewWindow.xaml.cs`
- `src/AIrtist.App/OnlineSessionWorker.cs`, `OnlineRoomWindow.xaml.cs`
- `tests/AIrtist.Checks/MatchRuntimeChecks.cs`, `src/AIrtist.App/FinalResultsSmoke.cs`, `LocalProcessSmoke.cs`

## 검사

| 범위 | 결과 |
|---|---|
| 전체 Core unit | 기존 경기·투표·이미지·취소/실패 복구 통과 |
| 누적 집계 | 단독·공동 1위·전원 0표·동일 닉네임의 별도 집계·잘못된 라운드/음수 표 거절 통과 |
| 권한·동기화 | 방장 전용·마지막 라운드 이전 거절·중복 요청·패킷 유실/재전송·전달 ACK 대기·호스트 이탈 후 결과 보존 통과 |
| 결과 UI | 한 명 가운데 배치·2인 공동 1위·16인 가로 스크롤/마지막 카드·긴 닉네임·0표·라이트/다크 통과 |
| 독립 앱 3개 | TCP·모의 AI 2라운드, 6표 합산·단독 1위 4표, 전원 결과 화면 이동·방장 선퇴장·각자 메인 복귀 통과 |
| 최종 testbuild 실제 2인 | 실제 Tiny-SD PNG 2장·서로 다른 그림·1:1 투표·공동 1위 1표씩·동일 결과 동기화·방장 선퇴장 후 화면 보존·메인 복귀 통과 |
| 최종 testbuild UI | `results-smoke` 통과, 화면 이미지 육안 확인 |
| 패키지 | 필수 454개 파일의 해시/길이 기록·publish 대조·1GB 이내 확인 |

단독/공동/16인/0표 화면 이미지와 숫자 검사를 함께 확인했다. 첫 UI 검사는 모의 마우스 이벤트에 `RoutedEvent`를 지정하지 않아 중단됐다. 검사 입력에 이벤트 종류를 지정한 뒤 `ui-v2`와 최종 패키지 검사에서 통과했다. 제품의 실제 마우스 이벤트에는 WPF가 이벤트 종류를 제공한다.

검사 출력은 `outputs/final-results-20261018/`의 `ui-v2`, `local-3`, `package-ui`, `package-real-2`, `package.json`에 있다. 실제 여러 PC·서로 다른 회선의 EOS 경기는 이번에도 미검증이다.

재검사 명령:

```powershell
./scripts/build-app.ps1 -CheckRules
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-results-ui -Mode results-smoke
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-results-three -Players 3
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-results-real-two -Players 2 -RealAi
```

## 배포

실행 파일은 `testbuild/AIrtist.exe`다. **폴더 전체 953,004,463바이트 / 455개 파일**, 1,000,000,000바이트 기준 **46,995,537바이트 여유**다. 새 모델·이미지·음원 에셋은 추가하지 않았다.

`AIrtist.dll` SHA-256: `8e644bbc52129c9250dc68d5a1d9ae06d87c525a4b865a24207ec931343fdcdb`.

기존 사운드 변경은 보존했다. 변경 전 DLL·안내·manifest 소형 백업은 `.cache/testbuild-before-final-results-20261018`에 있고, 모델과 전체 패키지를 중복 백업하지 않았다.
