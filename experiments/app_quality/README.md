# 번역·채점 비교 재현

> **채점은 과거 연구 자료다.** 2026-09-22 제품의 채점 코드·모델·벡터와 `install_scoring.py`를 제거했다. 아래 Python 비교·변환은 개발 전용이며 일반 앱 빌드에 필요하지 않다. C#의 구형 `quality`·`quality-scoring` 진단도 제거했다. 현재 작업 순서는 [통합 기획서](../../AI_Image_Game_Summary.md)를 따른다.

결론과 한계는 [품질 보고서](../../docs/APP_QUALITY_REPORT.md)를 참고한다. 공개 모델을 추론·변환할 뿐 학습하지 않는다. 변환 환경은 배포 패키지에 포함하지 않는다.

Windows x64/Python 3.12에서 CPU 환경을 만든다. 아래의 `python`은 설치된 Python 3.12 경로로 대체할 수 있다. 현재 실행에서 사용한 Python은 3.12.14다. CPU 패키지도 압축 해제 후 수 GB를 사용하므로 변환 후 환경을 제거해도 일반 앱 실행·C# 재빌드에는 영향이 없다.

```powershell
python -m venv .venv-quality
.\.venv-quality\Scripts\python.exe -m pip install --no-cache-dir torch==2.8.0 --index-url https://download.pytorch.org/whl/cpu
.\.venv-quality\Scripts\python.exe -m pip install --no-cache-dir -r experiments/app_quality/requirements-lock.txt
.\.venv-quality\Scripts\python.exe experiments/app_quality/download_scoring.py
```

개발 15장에는 과거 `outputs/app-stage4/quality`의 그림·scores.json, 비교 21장 중 4장에는 `outputs/tiny-sd/runs/baseline-fp32-20260913-01/images`가 필요하다. 이는 저장소에 넣지 않는 로컬 실험 결과이며 현재 앱으로 과거 scores.json을 새로 만들 수는 없다. 자료가 없으면 과거 커밋의 실험 코드와 고정 환경까지 복구해야 한다. 단순 도형·글자 17장은 아래 스크립트로 재현한다. 글자 렌더링에는 Windows의 맑은 고딕을 사용하며 폰트는 복사·배포하지 않는다.

```powershell
.\.venv-quality\Scripts\python.exe experiments/app_quality/make_holdout.py
.\.venv-quality\Scripts\python.exe experiments/app_quality/score_vectors.py development --model 40m
.\.venv-quality\Scripts\python.exe experiments/app_quality/score_vectors.py holdout --model 40m
.\.venv-quality\Scripts\python.exe experiments/app_quality/export_scoring.py
```

`export_scoring.py`는 개발용 변환물을 만들고 19장의 원본 대비 코사인 오차가 0.01 이내인지 확인한다. `prepare_scoring.py`의 8M 출력은 `models/legacy-scoring/8m`에만 저장한다. 두 도구 모두 현재 앱 모델 manifest·배포 패키지를 갱신하지 않는다. 단어 목록·문구·전처리 변경 시 과거 측정치를 그대로 재사용할 수 없다.

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet restore tests/AIrtist.Checks --configfile NuGet.Config --locked-mode
dotnet run --project tests/AIrtist.Checks --no-restore -- quality-translation . development
dotnet run --project tests/AIrtist.Checks --no-restore -- quality-translation . holdout
dotnet run --project tests/AIrtist.Checks --no-restore -- reference .
```

번역 비교는 독립된 개발 24문장(`translation-development.json`)과 별도 16문장(`translation-holdout.json`)을 사용한다. 과거 경기/채점 진단 출력 없이 번역 비교를 실행할 수 있다. 6개 후보 코드는 검사 프로젝트에만 있다. `reference`에는 생성용 토크나이저·번역·스케줄러만 남겼다. 명령 성공을 의미 품질 전체 합격으로 해석하지 않는다.

현재 `experiments/app_stage3/download_sources.py`는 TinyCLIP을 다운로드하지 않는다. 과거 8M 후보를 다시 비교할 때만 `scoring-8m-source.json`의 고정 URL·크기·SHA-256으로 `models/tinyclip/<revision>`에 원본을 복구하고 `score_vectors.py --model 8m`을 사용한다. 40M 출처는 `scoring-source.json`과 `download_scoring.py`에 남겨 두었다. 원본 가중치·변환 중간물·CPU 환경은 일반 앱에 필요하지 않다.
