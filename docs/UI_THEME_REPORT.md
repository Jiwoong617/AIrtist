# 싱글·멀티 화면 디자인 통일

작성: 2026-10-14.

## 적용 내용

- 메인 메뉴의 연한 보라·녹색 배경, 흰 카드, 보라색 버튼과 짙은 글자색을 그림그리기·방 생성/참가·대기방·경기·투표 화면에 적용했다.
- `src/AIrtist.App/Theme.xaml`에서 공통 색상, 카드, 버튼, 입력창, 드롭다운, 체크박스를 관리한다. 메인 메뉴도 같은 배경과 버튼 스타일을 사용한다.
- 숫자 입력과 설명 글자의 크기·여백을 정리하고, 비활성화된 방 고정 파라미터도 읽을 수 있도록 밝은 배경과 구분되는 글자색을 사용한다.
- 그림그리기와 멀티 작성 단계는 좌측 파라미터, 하단 묘사 입력, 우측 그림 그리기·취소 버튼으로 배치를 맞췄다.
- 멀티 투표·결과 단계에서는 파라미터와 입력 영역을 접고 그림 영역을 전체 폭으로 넓힌다. 다음 라운드 작성 시 다시 표시한다. 선택한 그림은 보라색 테두리로 구분한다.
- 방 코드 복사와 방 나가기는 경기 화면 우측 상단에 유지한다. 라운드별 기록은 그림·득표 결과 아래에서 확인한다.
- 부가 홍보 문구를 추가하지 않았다. 기존 번역·생성·제출·전송·투표 규칙과 온라인/로컬 자동 연결은 유지한다.
- 기존 투표 미리보기에도 공통 테마에 맞는 색상을 적용했다.

## 검사 결과

모두 Release 빌드 기준이다.

| 검사 | 결과·증거 |
| --- | --- |
| WPF 빌드 | 경고·오류 없이 게시 완료 |
| 자유/고정 파라미터의 모의 온라인 | 각 3인 2라운드, 자기 투표 비활성화·1/2/0표·결과 동기화·다음 라운드 입력 복구 통과. `outputs/theme-20261014/review/` |
| 최종 배포 EXE의 메인·싱글 | 실제 생성 3장, 한국어 내부 번역, 반복 생성, 원문·파라미터 변경, 화면 이동, 해상도·음량 저장·종료 통과. `outputs/theme-20261014/final-menu/` |
| 최종 배포 EXE의 같은 PC 멀티 | 독립 앱 3개, 방 코드 참가, 실제 TCP, 모의 AI 2라운드, 동일 PNG 해시·투표·방장 이탈 처리 통과. `outputs/theme-20261014/final-local/` |
| 화면 확인 | 싱글 실제 그림, 방 생성/참가, 고정 파라미터, 대기방, 투표·결과, 작은 경기 화면의 144 DPI 렌더 확인 |
| 배포 파일 | manifest의 443개 파일 크기·SHA-256 일치. `outputs/theme-20261014/package-verification.json` |

멀티 검사의 그림은 모의 AI 출력이며 실제 Tiny-SD 품질 검사로 취급하지 않는다. 다른 PC의 실제 EOS 경기는 이번 디자인 작업에서 검사하지 않았다. 작은 창의 확장된 방 설정과 긴 결과 목록은 스크롤로 확인한다. DPI 검사는 렌더 수준이며 실제 여러 모니터 이동·한국어 IME 입력 전체 검증은 아니다.

## 테스트 빌드

- 실행: `testbuild/AIrtist.exe`
- 전체 설치 파일: **943,547,171바이트 / 444개 파일**. 1,000,000,000바이트 이내.
- 공통 테마는 앱 DLL에 포함되며 별도 이미지·폰트·모델 다운로드를 추가하지 않는다.
- 최신 publish와 비교해 변경된 `AIrtist.dll`만 교체했다. 기존 DLL과 manifest는 `.cache/testbuild-before-theme-20261014/`에 보존했다.

재현 명령:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet publish src/AIrtist.App -c Release --no-restore --output .cache/stage4/publish
./scripts/check-app.ps1 -PackageDirectory .cache/stage4/publish -ModelDirectory models/app-stage3 -OutputDirectory outputs/theme-online-new -Mode online-smoke
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/theme-menu-new -Mode menu-smoke
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/theme-local-new
```

진단 출력 경로는 매번 새 디렉터리여야 한다.
