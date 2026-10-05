# 후속 한국어 번역 모델 비교

2026-09-20에 OPUS-MT TC big 후보의 토크나이저 수정, ONNX 변환, 혼합 4/8비트 저장, 개발 40문장·별도 16문장 비교를 수행했다. **의미 손실과 회귀 때문에 앱에 채택하지 않았다.** [판단 근거와 수치](../../docs/TRANSLATION_QUALITY_REPORT.md)를 참고한다. 스크립트는 `models/app-stage3`와 `testbuild`를 변경하지 않는다.

고정 체크포인트와 파일 SHA-256·라이선스 출처는 `source.json`에 있다. 개발 40문장은 이전에 본 24+16문장이다. `holdout.json`의 새 16문장은 이번 후보 실행 전에 정했으며 혼합 정밀도·greedy 설정을 정한 뒤 실행했다. 이후 설정을 다시 맞추는 데 사용하지 않았다. 모두 플레이어 묘사만 입력하며, 참고용 `meaning`이나 게임 정답은 모델에 전달하지 않는다.

## 재현

Windows x64, Python 3.12.14에서 프로젝트 루트 PowerShell 7로 실행한다. Python·CPU PyTorch 개발 환경은 수 GB이며 제품 구성에 포함하지 않는다. 의존성은 앞선 품질 실험과 같은 잠금 목록을 재사용한다.

```powershell
python -m venv .venv-translation
.\.venv-translation\Scripts\python.exe -m pip install --no-cache-dir torch==2.8.0 --index-url https://download.pytorch.org/whl/cpu
.\.venv-translation\Scripts\python.exe -m pip install --no-cache-dir -r experiments/app_quality/requirements-lock.txt
$env:PYTHONIOENCODING = 'utf-8'
.\.venv-translation\Scripts\python.exe experiments/translation_quality/download_model.py
.\.venv-translation\Scripts\python.exe experiments/translation_quality/evaluate_source.py development
.\.venv-translation\Scripts\python.exe experiments/translation_quality/export_model.py
.\.venv-translation\Scripts\python.exe experiments/translation_quality/evaluate_compact.py development > outputs/translation-quality/compact-development.log
.\.venv-translation\Scripts\python.exe experiments/translation_quality/evaluate_source.py holdout --beams 1
.\.venv-translation\Scripts\python.exe experiments/translation_quality/evaluate_compact.py holdout > outputs/translation-quality/compact-holdout.log
.\.venv-translation\Scripts\python.exe experiments/translation_quality/evaluate_compact.py development --precision fp32
.\.venv-translation\Scripts\python.exe experiments/translation_quality/evaluate_compact.py holdout --precision fp32
```

`candidate.py`는 원본 SentencePiece의 ID 그대로 한국어 입력/영어 출력 어휘표를 분리한다. 기본 HF 어휘표를 그대로 쓰면 한국어가 대부분 미등록 토큰이 된다. Windows 기본 인코딩과 무관하게 읽히도록 파생 JSON은 ASCII escape로 저장한다. `export_model.py`는 encoder/decoder FFN MatMul 24개만 block32 대칭 INT4로, 나머지 큰 행렬은 INT8 저장으로 바꾼다. 작은 파라미터는 FP16 저장, 활성값과 계산은 FP32다. 공유 임베딩의 출력용 전치 사본을 중복 저장하지 않는다. 보정·학습은 하지 않는다.

FP32 중간 ONNX가 이미 있으면 재사용하므로 모델·변환 코드를 바꾸면 별도 출력 폴더에서 재변환해야 한다. 혼합 모델은 매번 다시 만든다. 측정 스크립트는 각각의 JSON을 `outputs/translation-quality`에 기록한다. `compact` 필드명은 두 정밀도에서 공통으로 쓰는 결과 문자열이며 FP32 실행은 `onnx-fp32-*.json`에 저장한다.

기존 C# 앱 번역의 비교값은 준비된 `models/app-stage3`로 수집한다. 새 복제본은 먼저 [앱 개발 안내](../../docs/APP_DEVELOPMENT.md)에 따라 기존 모델을 준비한다.

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.cache/nuget'
dotnet restore tests/AIrtist.Checks --configfile NuGet.Config --locked-mode
dotnet run --project tests/AIrtist.Checks --no-restore -- quality-translation-baseline . development
dotnet run --project tests/AIrtist.Checks --no-restore -- quality-translation-baseline . holdout
```

이번 개발 40문장의 기존 모델 결과는 앞선 `outputs/app-quality/translation-{development,holdout}.json`의 greedy 결과를 재사용했다. 위 명령은 같은 묘사를 현재 앱 모델로 다시 측정한다. 명령 성공은 의미 품질 통과를 뜻하지 않는다.

집계는 위 실행에서 남긴 JSON·로딩 로그와 후보 모델이 있을 때 수행한다.

```powershell
.\.venv-translation\Scripts\python.exe experiments/translation_quality/summarize.py
```

이 명령은 크기·해시·변환 일치 수와 시간을 기록한다. 미채택 판단은 보고서의 의미 검토에 따른 기록이며 자동 번역 품질 판정이 아니다. 단순 문자열 일치와 의미 정확도는 구분한다. 입력/출력 JSON·로그·원본·중간 모델·가상환경은 Git에서 제외한다. 이번 후보 모델과 변환 환경은 결과 기록 후 정리했으며 다시 실행하려면 위 다운로드·환경 준비가 필요하다.
