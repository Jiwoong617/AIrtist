# 단일 창·테마 설정·통합 대기방

갱신: 2026-10-17.

## 재개 후 마무리

다섯 요청의 구현과 10월 15일 검사 기록은 보존되어 있었다. 다만 그날 21시의 마지막 수정(시작 정보가 참가자 준비 정보보다 먼저 도착할 때 대기, 파라미터 툴팁 표시 간격)이 publish에만 있고 `testbuild`에는 반영되지 않은 것을 SHA-256으로 확인했다. 최신 소스를 다시 Release 게시하고 앱 DLL과 패키지 manifest를 갱신했다.

- `outputs/shell-20261017/menu-final/`: 최종 배포본의 실제 이미지 3장, 같은 HWND·창 1개, 메인/싱글/멀티 설정 접근, 테마 저장, 생성 중 테마 전환, 해상도·음량·종료 검사 통과.
- `outputs/shell-20261017/local-final/`: 독립 앱 3개·TCP·모의 AI 2라운드, 닉네임, 방 설정 동기화와 준비 초기화, 참가자 준비·방장 시작, 경기 중 공통 설정, PNG 해시와 투표 일치 통과. 준비 정보가 늦게 오는 경우의 시작 대기 검사도 포함한다.
- `outputs/shell-20261017/package-verification.json`: 필수 파일 443개 크기·해시 일치, publish와 배포 앱 DLL 일치. 전체 **943,596,302바이트 / 444개 파일**.
- 이번 재개에서는 기존에 통과한 전체 unit·실제 EOS 단일 참가자 검사를 반복하지 않았다. 마지막 소스 수정 후의 기존 `local-6`·`eos-2` 통과 기록도 확인했다. 다른 PC EOS 경기 검증은 여전히 남아 있다.

## 사용 흐름

- 메인·그림그리기·멀티플레이·경기는 하나의 Windows 창 안에서 전환한다.
- 모든 화면의 우측 상단 **설정**에서 라이트/다크 모드, 창 크기, BGM/SFX 값을 저장한다. 테마는 즉시 반영되며 재실행 후에도 유지한다. 사운드 재생은 기존처럼 미구현이다.
- 설정을 열어도 현재 입력·이미지·접속·경기를 유지한다. 이미지 생성과 멀티플레이 타이머는 계속 진행한다. 테마나 음량만 바꾸면 창 위치와 크기를 바꾸지 않는다.
- 방 생성/참가에서 **닉네임**을 입력한다. 이름은 대기방, 그림 소유자, 투표 결과에 사용한다. 앞뒤 공백을 제거하고 1~16자 및 제어 문자 등을 검사한다. 동일 닉네임을 금지하지 않으며 권한은 별도 참가자 ID로 검사한다.
- 대기방은 하나다. **왼쪽 참가자 목록 / 오른쪽 방 설정**이며 방장만 설정을 변경·적용한다. 일반 참가자는 **준비 완료/준비 취소**, 방장은 **게임 시작**을 사용한다.
- 최소 2명이며 다른 참가자가 전부 준비해야 방장의 시작 버튼이 활성화된다. 설정·명단이 바뀌면 준비를 해제한다. 시작하면 내부 연결 합의 후 바로 경기 화면으로 이동한다.
- CFG·Steps·Seed 이름에 마우스를 약 1초 올려 두면 짧은 설명을 표시한다. 고정 파라미터의 비활성 입력 영역에서도 설명을 허용한다.

## 구현

`MenuWindow`가 실제 창과 설정 패널을 소유한다. 기존 `MainWindow`, `OnlineRoomWindow`, `RoundPreviewWindow`는 `GameScreen : UserControl`로 바꿔 `ContentControl`에 표시한다. 화면 종료는 생성 취소·네트워크 종료를 기다리고 메인으로 돌아간다. 기존 클래스명은 진단 코드 호환을 위해 유지했다. 일반 사용에서 추가 Window를 열지 않으며, 명시적 구형 투표 진단용 `VotingWindow`만 별도로 남아 있다.

`Theme.xaml`의 공통 리소스를 `DynamicResource`로 참조하고 `ThemeManager`가 밝은/어두운 색상을 교체한다. 카드·입력창·버튼·툴팁·스크롤바·투표 그림 테두리가 함께 변경된다. `GameSettings.DarkMode`를 추가했으며 이전 설정 파일은 라이트 모드로 읽는다.

`WaitingRoomCoordinator`는 기존 P2P 연결 합의 **이전**의 닉네임·방 설정·준비 상태를 관리한다. EOS에서는 방장 전용 방 속성과 인증된 본인 회원 속성, 로컬에서는 인증된 TCP 연결의 참가자 ID를 사용한다. 표시 이름으로 송신자 권한을 판단하지 않는다.

방 설정에는 고유 리비전과 명단 해시가 들어가며 참가자는 해당 리비전에 준비한다. 설정·명단 변경 후 과거 준비 값은 시작 조건에서 제외한다. 서비스 명단 변경 번호는 방장의 설정 리비전 갱신 계기로만 쓰며 여러 PC의 공통 번호로 사용하지 않는다. 속성 크기 256바이트, 쓰기 간격 500ms, 저장 확인 30초 제한을 적용한다.

방장 시작 후 기존 연결 합의와 Core 대기방의 명단·설정·전원 준비·시작 확인을 내부에서 수행한다. 기존 검증 단계를 생략하지 않고 두 번째 대기 화면을 제거했다. 경기 시작 뒤의 참가자 변경은 기존처럼 경기를 중단하며 호스트 이전은 지원하지 않는다.

호환성 규칙은 **`multiplayer-waiting-v8`**이다. 이전 `multiplayer-parameters-v7` 빌드와 혼용하지 않는다. 자동 로컬/EOS 선택과 사용자 승인으로 내장한 EOS 설정은 유지한다.

## 검사 결과

| 검사 | 결과와 범위 |
| --- | --- |
| Release 게시 | 컴파일 오류·경고 없음 |
| 전체 Core unit | 제출·투표·연결 합의·경기·취소/시간 초과 규칙 통과 |
| EOS 로비 경계 검사 | 명단·권한·수명·지연 완료 처리 통과 |
| `menu-2` | 실제 Tiny-SD 3장, 같은 HWND와 창 1개, 화면 이동·종료, 생성 중 설정 변경, 두 테마 저장, 해상도·음량, 툴팁 1000ms 설정 통과 |
| `online-1` | 모의 연결의 자유/고정 파라미터 각각 3인·2라운드, 생성·PNG·투표 회귀 통과 |
| `local-5` | 독립 EXE 3개, TCP, 모의 AI 2라운드. 닉네임·방 설정·준비 초기화·재준비·단일 창·고정값·동일 수신 PNG 해시·0/1/2표·방장 이탈 통과 |
| `final-local-real` | 최종 `testbuild`의 독립 EXE 3개, 실제 Tiny-SD 1라운드/3장. 새 통합 대기방부터 생성·전송·투표·방장 이탈, 참가자별 창 1개, PNG 해시 일치 통과 |
| 로컬 경계 검사 | 혼자 시작 금지, 미준비 시작 제한, 참가자의 방 설정/시작 거절, 참가·이탈 시 준비 초기화, 준비 취소, 시작 시 이름 일치, 잘못된 닉네임 거절 통과 |
| `eos-1` | 실제 EOS 단일 참가자의 닉네임·고정 방 설정 저장/조회, 방 코드·나가기·재접속·닫힌 코드 거절 통과. P2P·다른 PC 경기 검사가 아님 |
| 패키지 무결성 | `testbuild` 필수 파일 443개 해시 확인, manifest 포함 444개 |

위 표의 10월 15일 검사 산출물은 Git 제외 경로 `outputs/shell-20261015/`에 있다. `ConnectionBootstrapChecks`의 다른 스레드 검사는 동기 대기 중 같은 스레드에 인라인될 수 있는 `Task.Run` 대신 전용 Thread를 사용하도록 수정했다. 실제 스레드 권한 검사를 안정적으로 실행하기 위한 테스트 수정이다.

## 테스트 빌드

- 실행: `D:\AIrtist\testbuild\AIrtist.exe`
- 설치 후 전체: **943,596,302바이트 / 444개 파일**. 1,000,000,000바이트 기준 **56,403,698바이트** 여유.
- 앱·번역/생성 모델·토크나이저·.NET/WPF·DirectML/ORT·EOS·고지를 모두 포함한 값이다. 압축 설치 파일 크기가 아니다.
- 10월 17일 교체 전 앱 DLL과 manifest: `.cache/testbuild-before-shell-finish-20261017/`. 10월 15일의 이전 변경 파일과 manifest/README는 `.cache/testbuild-before-shell-20261015/`에 보존했다.
- 새 README와 `package-manifest.json`을 반영했다. EXE만 이동하지 말고 `testbuild` 폴더 전체를 사용한다.

## 재현

출력 폴더는 기존 경로와 겹치지 않게 지정한다.

```powershell
dotnet publish src/AIrtist.App -c Release --no-restore --output .cache/stage4/publish
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- unit D:\AIrtist
dotnet run --project tests/AIrtist.EosChecks -c Release --no-restore -- lobby
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/shell-menu-new -Mode menu-smoke
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/shell-online-new -Mode online-smoke
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/shell-local-new
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/shell-real-new -RealAi
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/shell-eos-new -Mode online-live-smoke
```

## 미검증 범위

다른 회선의 여러 PC에서 EOS 준비 속성 전파·P2P 그림 전송·온라인 투표는 아직 미검증이다. [여러 PC 검사 절차](MULTIPC_TEST_GUIDE.md)를 따른다. 깨끗한 Windows, 저사양, 다중 모니터/DPI 조합, 수 시간 사용, 인증 갱신은 이번 검사 범위에 포함하지 않는다. 모델이나 번역 품질을 개선한 변경도 아니다.
