# 오프라인 경기 앱 개발·실행

현재 앱은 **여러 라운드의 묘사·생성·평가와 최종 결과를 갖춘 오프라인 개발 버전**이다. 번역·채점 품질 미달이 확인돼 경쟁용 품질 개선이 필요하다. 멀티플레이는 구현 전이다. [4단계 최신 결과](APP_STAGE4_REPORT.md)와 [3단계 초기 연결 결과](APP_STAGE3_REPORT.md)를 참고한다.

## 실행

이 개발 PC의 최신 패키지는 `testbuild/AIrtist.exe`다. 해당 폴더 전체에 모델·.NET·WPF·ONNX Runtime·DirectML이 들어 있다. 실행 프로그램만 다른 폴더로 옮기면 동작하지 않는다. 런타임에서 모델을 내려받거나 Python을 실행하는 코드는 없다. 구버전 `dist/AIrtist-preview*`는 [용량 정리](WORKSPACE_CLEANUP.md)로 제거했다.

라운드 수와 제한 시간을 고르고 **경기 시작**을 누른다. 기본값은 3라운드·작성 60초·AI 처리 180초다. 제시어를 쓰지 않고 2~160자로 묘사한 뒤 **한 장 그리기**를 누른다. Enter는 줄바꿈이며 기본 제출 키로 연결하지 않았다. 제출이 승인되면 생성 기회를 소비하고 입력을 고정한다. 취소·시간 초과·실패·건너뛰기는 0점으로 기록하고 다음 라운드로 진행한다. 마지막에는 누적·평균 점수와 라운드별 결과를 표시하며 새 경기를 시작할 수 있다. 결과 이미지는 메모리에 한 장만 보관하고 다음 라운드에서 해제한다. 진단 명령을 제외하면 이미지·프롬프트를 파일에 저장하지 않는다.

## 구조와 임시 규칙

- `src/AIrtist.App`: WPF 화면과 입력·진행·결과 표시. 무거운 연산은 UI 스레드에서 실행하지 않는다.
- `src/AIrtist.Core`: `MatchController`의 설정·작성 마감·라운드·최종 점수, `RoundController`의 제출 승인·유효성 검사·AI 순서·취소·실패·오래된 결과 거절. 네트워크 권한 모델은 아직 구현하지 않았다.
- `data/words.json`: 임시 8개 제시어, 뜻, 금지 별칭. 앱은 목록을 섞어 경기 내에서 중복 없이 사용한다.
- `experiments/app_stage3`: 개발용 모델 준비·변환·참조값 생성·패키징. 설치 패키지에는 포함하지 않는다.
- `tests/AIrtist.Checks`: 상태 전이와 독립 Python 참조값을 확인하는 콘솔 검사기. 제품에는 포함하지 않는다.

입력은 NFKC 정규화·소문자화 후 문자/숫자만 남겨 별칭의 부분 문자열을 검사한다. 띄어쓰기, 전각 영문, 기호·제로폭 문자 삽입은 이 검사로 잡는다. `개`, `범`처럼 짧은 별칭 때문에 관련 없는 문장도 거절할 수 있으며, 동음이의어·외국어의 모든 별칭을 지원하는 것은 아니다. 현재 보수적인 임시 규칙이다.

제시어와 별칭은 번역·생성 함수에 전달하지 않는다. 번역 후 제시어가 나타나면 Core에서 생성 전에 중단한다. 이 검사가 번역의 모든 의미 변화·추측을 탐지하는 것은 아니다. 예를 들어 원문에 없는 단어로 의미가 변해도 별칭이 아니면 통과할 수 있다.

전체 번역·생성·채점 제한은 기본 180초이며 경기 시작 전에 바꿀 수 있다. 작성 마감은 UI 타이머와 별개로 Core에서 단조 증가 시계로 판정한다. 점수는 코사인 유사도를 `clamp((cosine - 0.1) / 0.3 × 100, 0, 100)`으로 표시한다. 빈 이미지에도 높은 점수가 나오는 등 미보정 문제를 확인했으며 정답 확률이 아니다. 합계는 성공 라운드 점수의 합, 평균은 합계를 전체 설정 라운드 수로 나눈 값이다. 단어 또는 평가 문구를 바꾸면 같은 TinyCLIP 체크포인트로 벡터를 다시 만들고 별도 자료로 채점 품질을 검증해야 한다.

## 개발 도구와 모델 준비

SDK는 `global.json`의 **.NET 10.0.103**, 자체 포함 런타임은 **10.0.3 / win-x64**다. C# 패키지는 각 프로젝트의 `packages.lock.json`으로 고정한다. 현재 앱 소스 빌드·패키징에는 Python이 필요하지 않다. 준비된 `models/app-stage3/manifest.json`과 모델·고지를 PowerShell 패키저가 검사하고 복사한다.

현재 개발 PC에서는 아래 명령만으로 새 패키지를 만든다. 출력 폴더는 없는 새 경로여야 하며 기존 `testbuild`를 덮어쓰지 않는다.

```powershell
.\scripts\build-app.ps1 -CheckRules -PackageDirectory testbuild-new
```

원본 다운로드·모델 변환을 다시 수행할 때만 다음 준비가 필요하다. 대용량 `.venv-tiny-sd`와 완료된 Tiny-SD 원본·중간 모델은 용량 정리로 삭제했다. 변환 코드는 보존했으므로 Python 3.12로 환경을 재생성하고 필요한 모델을 다시 받아 실행한다. 이미 준비된 앱을 실행할 때 추가 다운로드를 요구한다는 뜻은 아니다.

먼저 [Tiny-SD 원본 준비](../experiments/tiny_sd/README.md)와 [2단계 변환](../experiments/tiny_sd/STAGE2.md)을 완료한다. 아래 참조 검사는 2단계의 `stage2-onnx-pipeline-02` 영어 이미지 3장을 사용한다. 저장소에는 모델과 원본 결과가 없으므로 새 복제본에서는 해당 준비·생성 명령을 실행해야 한다.

모델 변환이 필요한 경우 프로젝트 루트 PowerShell:

```powershell
.\experiments\tiny_sd\setup.ps1 -Python 'C:\path\to\python.exe' -Stage2
.\.venv-tiny-sd\Scripts\python.exe -m pip install -r experiments/app_stage3/requirements.txt
.\.venv-tiny-sd\Scripts\python.exe experiments/app_stage3/download_sources.py
.\.venv-tiny-sd\Scripts\python.exe experiments/app_stage3/prepare_models.py translation
.\.venv-tiny-sd\Scripts\python.exe experiments/app_stage3/prepare_models.py scoring
.\.venv-tiny-sd\Scripts\python.exe experiments/app_stage3/prepare_models.py generation
.\scripts\build-app.ps1 -CheckRules -PackageDirectory testbuild-new
```

`PackageDirectory`는 아직 없는 새 폴더를 지정한다. 패키징은 기존 폴더를 덮어쓰지 않는다. `scripts/package-app.ps1`은 생성 모델 3개, 번역 모델·토크나이저, TinyCLIP 이미지 인코더·단어 벡터, 준비된 라이선스를 모델 manifest 기준으로 선택한다. 연구용 MobileCLIP, 번역 INT8 실패 후보, 원본·중간 모델은 패키지에 들어가지 않는다. 크기는 `package-manifest.json`을 포함한 모든 실제 파일의 바이트 합으로 기본 `outputs/build/package-size.json`에 기록한다. `-PackageReport`로 다른 보고서 경로를 지정할 수 있다. 모델을 새로 변환한 경우에는 Python 준비 도구로 manifest·라이선스를 먼저 다시 작성해야 한다.

변환 스크립트는 기존 ONNX 파일을 재사용한다. 변환 코드를 수정한 경우 기존 중간/최종 ONNX를 별도 보관하거나 새 산출 경로를 사용해 다시 변환해야 한다. 벡터와 참조 결과 JSON은 재실행 시 갱신된다.

## 검사

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet restore tests/AIrtist.Checks --configfile NuGet.Config --locked-mode
dotnet run --project tests/AIrtist.Checks --no-restore -- unit .
dotnet run --project tests/AIrtist.Checks --no-restore -- reference .
dotnet run --project tests/AIrtist.Checks --no-restore -- generate . csharp-generation-check
.\.venv-tiny-sd\Scripts\python.exe experiments/app_stage3/verify_generation.py --run csharp-generation-check
```

`reference`는 CLIP/Marian 토큰, 실제 번역, 25단계 스케줄러, TinyCLIP 전처리·코사인 결과를 별도 Python 구현의 값과 비교한다. 생성 참조 검사는 PyTorch가 만든 동일 초기 잠재값을 주입한다. 앱의 일반 시드는 .NET Random + Box–Muller 방식이므로 **같은 정수 시드가 Python과 같은 초기 잡음을 뜻하지 않는다**.

4단계 반복·품질 측정과 배포 EXE 검사는 다음과 같다. GPU 생성 검사는 다른 생성 검사와 동시에 실행하지 않는다. 반복 실행은 같은 프로세스의 성공 6건·취소 1건·시간 초과 1건이며 강제 GC를 호출하지 않는다. `quality`는 결과를 기록하고 의미를 검토하는 명령이며 종료 성공이 품질 통과를 뜻하지 않는다.

```powershell
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- stability .
dotnet run --project tests/AIrtist.Checks -c Release --no-restore -- quality .
.\scripts\check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/app-stage4/check-new -Mode match-smoke
.\testbuild\AIrtist.exe --self-check outputs/app-stage4/ui-check-new
```

품질 측정은 이전 `outputs/app-stage3/csharp-generation-bundled`의 생성 이미지와 `packaged-smoke-03/generated.png`가 필요하다. 새 복제본에서는 3단계 생성 참조/한국어 통합 검사로 해당 표본을 먼저 준비해야 한다. [3단계 기록](APP_STAGE3_REPORT.md)을 참고한다.

`--self-check`는 필수 파일 해시·화면 배치·Enter 제출 설정을 확인한다. `--smoke`는 한국어 한 건의 실제 창 제출·결과 경로를, `--match-smoke`는 한국어 생성 → 건너뛰기 → 영어 생성 → 최종 결과 → 새 경기 초기화를 검사한다. 진단은 창을 표시하지 않고 결과 폴더에 기록한다. `check-app.ps1`은 새 출력 폴더만 허용하고 네이티브 모듈 경로·메모리도 수집한다. GPU 사용량은 다른 앱을 포함한 전체 값이다. 실제 사람의 IME 입력, 모니터 DPI 변경, 별도 저사양 PC를 대신하지 않는다. `status.json`과 프로세스 종료 코드로 성공/실패를 확인한다.

## Git 보관 범위

소스, 단어 목록, 패키지 잠금 파일, 이 문서와 최종 보고서를 보관한다. `.cache`, `.venv`, `models`, `dist`, `testbuild`, `bin`, `obj`, 실행 이미지·로그·프로파일·참조 결과 JSON은 `.gitignore`로 제외한다. 문서의 `outputs/`·`testbuild/` 링크는 로컬 결과가 있는 PC에서만 열린다. 과거 `dist/` 링크는 정리된 이전 배포본을 가리킨다.
