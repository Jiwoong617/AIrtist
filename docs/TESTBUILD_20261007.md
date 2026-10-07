# 테스트 빌드 — 2026-10-07

- 실행: `D:\AIrtist\testbuild\AIrtist.exe`. 폴더 전체를 함께 보관한다.
- Windows x64 Release, 모델·번역·.NET/WPF·ORT/DirectML·EOS 런타임 포함.
- 설치 후 합계: 943,443,042바이트 / 444개 파일. 1,000,000,000바이트 목표 충족.
- 좌측 CFG·Steps·Seed, 하단 입력과 방 파라미터 고정/자유 설정을 포함한다.
- 기존 패키지는 `.cache/testbuild-before-20261007`에 보존했다.

## 재현과 확인

기존 캐시로 `dotnet publish src/AIrtist.App -c Release --no-restore --output .cache/stage4/publish`를 실행하고 `scripts/package-app.ps1`로 새 패키지를 구성한 후 최종 경로로 옮겼다. 앞선 빌드 스크립트의 복원은 사용자 NuGet.Config 접근 제한으로 실패했으므로 이미 복원된 의존성을 사용했다. 앱 소스와 모델은 이번 패키징에서 수정하지 않았다.

- 최종 EXE `parameter-smoke`: 실제 이미지 4장, 동일 Seed 재현·CFG/Steps 반영·UI 검사 통과. 동봉 CLR/ORT/DirectML 로딩 경로 확인.
- 최종 EXE `online-smoke`: 자유/고정 파라미터의 모의 3인·2라운드와 종료 검사 통과.
- 패키지 목록의 443개 파일 크기/SHA-256 일치. 목록 자체를 합하면 444개다. 로컬 EOS 설정 미포함 확인.
- 증거: `outputs/build-20261007/package-size.json`, `parameters/status.json`, `parameters/process.json`, `online/status.json`, `online/process.json`.

실제 다른 PC의 EOS 연결·그림 전송·온라인 투표, 낮은 사양, 개발 도구 없는 깨끗한 PC는 이번에 검사하지 않았다. 온라인 접속에는 별도 EOS 설정 파일이 필요하다.
