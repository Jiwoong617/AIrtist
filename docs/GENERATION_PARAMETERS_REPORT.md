# 생성 파라미터와 화면 변경 — 2026-10-06

**2026-10-07 테스트 빌드:** 최신 소스를 Release로 다시 게시해 `testbuild/AIrtist.exe`에 반영했다. 모델·런타임을 포함해 **943,443,042바이트 / 444개 파일**이다. 최종 경로에서 실제 파라미터 생성 4장과 자유/고정 모의 온라인 검사가 통과했고 패키지 파일 해시를 확인했다. 기존 `testbuild`는 `.cache/testbuild-before-20261007`에 보존했다. 아래 별도 패키지 경로는 이전 작업 당시 기록이다.

## 사용 방법

최신 실행 파일은 `D:\AIrtist\testbuild-parameters\AIrtist.exe`다. 폴더 전체를 함께 보관한다. 기존 `testbuild`가 실행 중이어서 해당 앱을 종료하거나 덮어쓰지 않고 별도 패키지로 준비했다.

- 그림그리기: 좌측에서 CFG·Steps·Seed를 입력하고 하단에서 묘사 작성 → 번역 확인·수정 → 생성한다.
- CFG는 1~20의 유한한 실수, 기본값 7이다. Steps는 1~50의 정수, 기본값 25다. 이 범위는 초기 제품 제한이며 모든 값의 그림 품질을 보장하지 않는다.
- Seed는 0~2147483647의 정수 또는 매번 무작위다. 기본은 무작위이며 결과 옆에 실제 사용한 설정을 표시한다.
- 방 생성 화면의 **방 파라미터 설정**을 체크하면 CFG·Steps·Seed 숫자가 나타나고 참가자 모두 그 값으로 고정된다. 고정 방은 무작위 Seed를 선택하지 않는다.
- 체크하지 않으면 참가자 각자가 설정한다. 설정은 최종 제출 시 확정되며 번역만으로 생성권을 소비하지 않는다.
- 대기방에서는 방장이 정책을 바꿔 적용할 수 있다. 적용하면 전원이 다시 준비해야 한다. 경기 중 방 설정은 바뀌지 않는다.
- 멀티 경기에서도 좌측은 생성 설정, 중앙은 그림·투표, 하단은 묘사·번역 확인이다.
- Strength/이미지 입력, 해상도 변경, 스타일 선택은 이번 변경에 포함하지 않는다. 해상도는 512×512다.

## 구현

`GenerationParameters`는 불변 값 객체이며 범위 검사를 UI와 별도로 Core에서 수행한다. 그림그리기는 작업 시작 시 무작위 Seed를 확정하고 결과에 값을 보관한다. `IDrawingAi`와 직렬 실행기를 통해 실제 `LocalGenerator`까지 전달한다. 입력 오류는 생성 작업을 시작하지 않는다.

멀티플레이는 `MultiplayerRoundPolicy.FixedParameters`가 null이면 자유 설정, 값이 있으면 방 고정이다. 이 정책은 대기방 설정 합의와 라운드 시작 메시지에 포함된다. 온라인 방 생성 시 선택한 정책은 전용 worker가 보관하고 연결 준비 후 호스트 Core 대기방에 전달한다. 참가자는 기존 대기방 동기화 경로로 이를 수신한다.

제출 요청에 파라미터를 포함하고 호스트가 범위·고정값 일치를 검증한다. 호스트가 무작위 Seed를 한 번만 확정해 승인에 담는다. 동일 요청의 재전송은 같은 승인·Seed를 반환하며 요청 내용을 바꾸면 거절한다. 클라이언트도 승인 값이 요청과 일치하는지 확인한다. 실제 AI에는 승인된 값만 전달하며 제시어는 전달하지 않는다.

호환 규칙은 `multiplayer-parameters-v7`로 올렸다. 이전 v6 앱은 같은 경기로 연결하지 않는다. 로컬 변조 앱의 실제 추론 설정까지 증명하는 부정행위 방지 기능은 아니다.

## Steps 계산

기존 25단계 계산표를 중간에 끊는 방식으로 구현하지 않았다. 다른 Steps에는 Tiny-SD의 scaled-linear beta(0.00085~0.012, 학습 시간축 1000), linspace 시간 선택, DPM-Solver++ 2차 midpoint와 마지막 sigma 0을 사용해 전체 계산표를 만든다. 25단계는 기존 검증된 배포 계산표를 그대로 사용해 기본 동작을 유지한다.

근거: [Tiny-SD scheduler 설정](https://huggingface.co/segmind/tiny-sd/blob/main/scheduler/scheduler_config.json), [Diffusers 0.35.1 구현](https://github.com/huggingface/diffusers/blob/v0.35.1/src/diffusers/schedulers/scheduling_dpmsolver_multistep.py). 새 C# 계산표의 25단계 계수는 기존 Diffusers 산출물과 절대 오차 0.00003 미만으로 비교했다. 이는 다른 GPU 간 픽셀 일치를 보장하는 검사가 아니다.

## 검증 결과

- Release 빌드·publish 통과. 모델 재학습·새 모델 다운로드·가중치 변경 없음.
- 기존 전체 unit 검사 통과. 새 `parameters` 검사도 통과하며 이후 전체 unit 실행에 포함된다.
- 1~50단계 모두의 감소하는 시간 순서, 유한한 계산 결과, 마지막 단계와 기존 25단계 기준 계수 비교 통과.
- NaN·Infinity·범위 밖 값 거절, 자유 설정·고정 방 변조 거절, 재전송 시 Seed 유지, 위조 승인 거절, 2인 메모리 연결에서 AI까지 값 전달을 검사했다.
- 최종 패키지 `--parameter-smoke`: 실제 GPU로 4장 생성. CFG 5.5 / 8 Steps / Seed 42는 첫 12.92초, 반복 10.90초이며 PNG가 일치했다. CFG만 7.25로 바꾼 그림과 이후 Steps만 12로 바꾼 그림은 각각 다른 PNG였고 사용값도 일치했다. 소요 시간은 이 개발 PC의 관측값이다.
- 최종 패키지 `--online-smoke`: 자유 방과 고정 방 각각 모의 3인·2라운드 통과. 생성 화면과 방 생성 체크박스, 참가자 수정 가능 여부, worker/Core/AI 전달값, 투표·결과 동기화를 검사했다. 실제 EOS/인터넷 검사와 구분한다.
- 최종 패키지 `--lobby-smoke`: 로컬 대기방 준비·설정 변경·참가자 권한·경기 전환·초기화 통과.
- 최종 패키지는 **943,444,066바이트 / 444개 파일**이며 1GB 예산 이내다. 모델·런타임 포함 수치다.
- 결과는 `outputs/parameters/package-drawing`, `package-online`, `package-lobby`의 화면 PNG·JSON과 `outputs/parameters/package-size.json`에 저장했다.

## 재현 명령

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet build src/AIrtist.App -c Release --no-restore
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- parameters .
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- unit .
./scripts/check-app.ps1 -PackageDirectory testbuild-parameters -OutputDirectory outputs/parameters/recheck-drawing -Mode parameter-smoke
./scripts/check-app.ps1 -PackageDirectory testbuild-parameters -OutputDirectory outputs/parameters/recheck-online -Mode online-smoke
```

## 남은 검증

실제 다른 회선의 여러 PC에서 고정/자유 설정 전달과 온라인 그림·투표를 확인해야 한다. 저사양·깨끗한 PC·장시간 반복·실제 IME/DPI 조작은 이번 검사로 완료되지 않는다. 높은 Steps는 생성 제한 시간을 늘려 주지 않으며 기존 취소·시간 초과 정책을 따른다. 동일 Seed의 결과 재현도 이번 검사 PC·동일 실행 환경에 한정한다.
