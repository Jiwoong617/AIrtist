# 그림 제출 후 투표 화면 종료 수정 — 2026-10-17

## 확인한 원인

사용자가 실행한 `testbuild/AIrtist.exe` 두 프로세스는 2026-10-17 20:01:25에 같은 처리되지 않은 .NET 예외로 종료됐다. Windows Application 로그의 이벤트 1026에서 다음 경로를 확인했다.

```text
InvalidOperationException: 다른 스레드가 이 개체를 소유하고 있어 호출 스레드가 해당 개체에 액세스할 수 없습니다.
BitmapDecoder.ToString()
StringBuilder.Append(Object)
RoundPreviewWindow.Card.PrintMembers(StringBuilder)
RoundPreviewWindow.Card.ToString()
ItemAutomationPeer.GetNameCore()
AutomationPeer.UpdateSubtree()
ContextLayoutManager.fireAutomationEvents()
```

모든 그림을 받은 뒤 투표 카드가 표시되면 WPF UI Automation이 카드의 접근성 이름을 요청한다. 카드가 C# `record`여서 기본 `ToString()`이 이미지 속성까지 문자열로 변환했다. 이미지는 네트워크 작업 스레드에서 디코딩하고 `Freeze()`해 표시할 수 있지만, 문자열 변환이 접근하는 내부 `BitmapDecoder`는 원래 스레드에 속한다. UI 스레드에서 이를 읽어 예외가 발생했고 처리되지 않아 앱이 종료됐다.

인원수가 2명일 때만 발생하는 규칙 오류는 아니다. UI Automation의 이름 조회가 발생하는 환경과 시점에 따라 3인 경기에서도 같은 코드가 실행될 수 있다. 이전 렌더링·PNG·투표 검사는 이 이름 조회 경로를 명시적으로 실행하지 않아 놓쳤다.

## 변경

- `RoundPreviewWindow.Card.ToString()`은 이미지를 읽지 않고 참가자 이름 문자열만 반환한다.
- 같은 형태의 `VotingWindow.Card`에도 동일한 수정을 적용했다. 접근성 기능을 끄거나 예외를 무시하지 않는다.
- `GalleryAccessibilityChecks`는 UI 스레드에서 실제 WPF `ItemAutomationPeer.GetName()` 경로를 호출하고 반환한 이름을 확인한다. 외부 접근성 프로그램이 연결되지 않아도 실행한다.
- `check-local.ps1 -Players 2`를 추가해 독립 앱 2개로 방 생성·참가부터 제출·투표·1:1 공동 결과·종료까지 검사한다. 기본 3인 검사는 유지한다.
- 실제 AI 진단은 서로 다른 두 묘사를 사용하며 이미지 해시가 서로 다른지도 확인한다.

## 검사

산출물은 Git 제외 경로 `outputs/gallery-crash-20261017/`에 있다.

| 경로 | 결과 |
| --- | --- |
| `windows-crash-events.json` | 사용자 앱 두 프로세스의 실제 이벤트 1026 기록 |
| `before-fix-2` | 수정 전 제품 코드에 새 진단을 추가해 독립 앱 2개·모의 AI로 동일 `BitmapDecoder.ToString → Card.ToString → ItemAutomationPeer.GetNameCore` 예외 재현 |
| `after-fix-2` | 수정 후 독립 앱 2개·TCP·모의 AI 2라운드, 접근성 이름·그림 수신 해시·1:1표·다음 라운드·방장 이탈 통과 |
| `after-fix-3` | 독립 앱 3개·TCP·모의 AI 2라운드, 접근성 이름·0/1/2표·그림 해시·방장 이탈 통과 |
| `online` | 모의 온라인 자유/고정 파라미터 각각 3인·2라운드 통과 |
| `voting-preview-2` | 기존 실제 PNG 3장으로 별도 투표 미리보기의 접근성 이름·선택·집계 통과 |
| `final-real-2` | 갱신된 `testbuild`의 독립 EXE 2개·실제 Tiny-SD 1라운드/2장. 서로 다른 PNG 생성·TCP 전송·양쪽 수신 해시 일치·접근성 이름·1:1 공동 결과·방장 이탈 통과 |
| `package-verification.json` | 필수 파일 443개 해시·길이와 publish DLL 일치 |

첫 `voting-preview` 진단은 아직 표시되지 않은 Window에서 UI Automation을 호출해 진단 코드의 NullReferenceException으로 실패했다. 명시적 투표 진단에서 창을 표시하고 레이아웃을 만든 뒤 이름을 조회하도록 고쳐 `voting-preview-2`로 통과했다. 일반 실행 화면 흐름에는 영향을 주지 않는다.

Release 게시와 `git diff --check`를 통과했다. 실제 EOS의 다른 PC 연결은 이번 수정의 검증 범위가 아니다.

## 배포

- 실행 경로: `D:\AIrtist\testbuild\AIrtist.exe` (폴더 전체 필요)
- 전체 필수 파일: **943,598,864바이트 / 444개 파일**
- 앱 DLL SHA256: `5c9706dd900d409c502e2467bd8440f904a505842f32b000410d7a2ca1e8cb61`
- 변경 전 DLL·manifest는 `.cache/testbuild-before-gallery-crash-20261017/`에 보존했으며 이후 사용자가 요청한 [폴더 정리](WORKSPACE_CLEANUP_20261017.md)에서 제거했다.
- 모델·런타임은 그대로이며 앱 DLL과 패키지 manifest를 갱신했다.

## 재검사 명령

출력 폴더는 아직 존재하지 않는 경로를 지정한다.

```powershell
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/local-2-new -Players 2
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/local-real-2-new -Players 2 -RealAi
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/local-3-new -Players 3
```
