# Tiny-SD 원본 검증

제품 앱과 분리된 Windows x64/Python 3.12 개발 실험이다. 이 문서는 1단계 원본 검증을 설명한다. 양자화·TAESD 교체 및 ONNX 실행 검증은 [2단계 재현 문서](STAGE2.md)를 참고한다. 추가 학습은 수행하지 않으며 제품의 배포 구성을 확정한 것은 아니다.

## 재현

프로젝트 루트의 PowerShell에서 실행한다. 최초 환경 준비와 모델 다운로드에는 인터넷이 필요하며, 벤치마크는 저장된 모델만 사용한다.

```powershell
# 설치된 Python 3.12 실행 파일을 지정한다.
.\experiments\tiny_sd\setup.ps1 -Python 'C:\path\to\python.exe'
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\download_model.py
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\benchmark.py --precision fp32 --output .\outputs\tiny-sd\runs\baseline-01
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\verify_run.py .\outputs\tiny-sd\runs\baseline-01
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\make_gallery.py .\outputs\tiny-sd\runs\baseline-01
```

- 모델: `segmind/tiny-sd`, 리비전 `cad0bd7495fa6c4bcca01b19a723dc91627fe84f`.
- 가중치·토크나이저·설정 파일을 원본 그대로 다운로드하고 upstream LFS SHA-256 또는 Git blob SHA-1과 비교한다. 각 파일의 SHA-256을 manifest에 기록하고 실행 전에 다시 검증한다.
- PyTorch 2.8.0+cu126, Diffusers 0.35.1, Transformers 4.56.2. 설치한 전체 패키지는 `requirements-lock.txt` 및 실행별 `environment.json`에 기록한다.
- 원본 파일을 유지하고 FP32로 메모리에 로딩한다. 원래 VAE, 저장소의 DPM-Solver 스케줄러 설정, 512×512, 25단계, CFG 7, 한 장씩 생성한다. 이 PC의 FP16 실행은 첫 디노이징 단계에서 비정상 수치가 발생했다. `--precision fp16`은 실패 재현 또는 다른 환경 비교에 사용한다.
- 공통 스타일: `simple flat 2D illustration, plain background`. negative prompt는 빈 문자열.
- 시드: `20260913`, `42`. CPU Generator를 새로 만들어 언어·속성 쌍에서 초기 노이즈를 맞춘다. 다른 장치·버전 간 픽셀 일치는 보장하지 않는다.
- 기본 실행은 최초 생성 1회 + 동일 입력 반복 2회 + 12묘사 × 2언어 × 2시드 = 51회다. 최초·반복의 중복 이미지를 품질 표본 수에 더하지 않는다.
- 생성기가 읽는 `prompts.json`에는 묘사·공통 스타일·중립 ID만 있다. 정답·금지 별칭·평가 항목은 `review_cases.json`에 분리하며 생성 실행기는 읽지 않는다.
- 한국어/영어 모두 같은 영어 공통 스타일을 사용한다. 한국어 텍스트 토큰 수와 77토큰 잘림 여부를 기록한다.
- `--cases fruit_red --seeds 20260913 --repeats 0`으로 작은 실행을 할 수 있다. 기존 출력 폴더를 지정하면 덮어쓰지 않고 실패한다.

## 측정 범위

`metrics.jsonl`은 생성 직후 매번 기록하므로 중간 실패 이전의 결과가 남는다. `summary.json`은 완료·실패 여부를 명시한다. 모든 생성은 네트워크 없는 로컬 추론이다.

- 라이브러리 import와 모델 로딩을 구분한다. 모델 로딩은 CUDA 동기화 후 시작하고 GPU로 이동한 뒤 동기화하여 끝낸다.
- 실행 전 해시 검사가 파일 캐시를 데우므로 **OS 파일 캐시가 비어 있는 최초 설치/콜드 디스크 로딩 시간이 아니다.** 새 프로세스에서의 첫 모델 로딩이다.
- 생성 시간에는 텍스트 인코딩·디노이징·VAE 복원·PIL 변환이 포함되고 PNG 저장은 별도로 측정한다.
- RAM은 실험 Python 프로세스 RSS를 50ms 주기로 샘플링하고 Windows 프로세스 생애 최대 working set도 별도 기록한다.
- VRAM은 PyTorch allocated/reserved 최대값과 NVIDIA 장치 전체 사용량의 약 500ms 샘플을 구분한다. 장치 전체 수치에는 바탕화면·다른 앱도 포함된다. 샘플링 최대치는 순간 피크를 놓칠 수 있다.
- 오류가 나면 실패를 기록하고 중단한다. CPU·더 낮은 정밀도·다른 모델로 몰래 대체하지 않는다.
- 매 디노이징 단계에서 유한값 검사와 GPU 동기화가 발생하므로 그 검사 비용도 생성 시간에 포함된다. 단색 출력은 실패로 취급한다.
- 품질 평가는 실제 출력 이미지의 관찰 기록이다. 작은 표본에 대한 관찰을 모델의 정확도나 게임 정답 확률로 부르지 않는다.

## 저장 위치

- `models/tiny-sd/<revision>/`: 원본 모델. Git 제외.
- `.venv-tiny-sd/`: 개발 전용 Python·CUDA 라이브러리. Git 제외. 제품 설치 용량에 포함될 구성으로 간주하지 않는다.
- `outputs/tiny-sd/model-manifest.json`: 실측 파일 크기·해시·출처.
- `outputs/tiny-sd/runs/<name>/`: 원본 PNG, 실행 환경, 프롬프트 사본, 성능 로그. Git 제외.
- 실행 당시의 `benchmark-source.py`도 실행 폴더에 보존한다. 측정 후 스크립트 기본값을 바꾸어도 당시 실행 코드와 환경을 대조할 수 있다.
- `outputs/tiny-sd/`: 검토한 최종 보고서·모델 출처·라이선스만 Git에 보관한다. 실행별 검사 결과·로그·환경 스냅샷은 로컬에만 남긴다.

## 공식 근거

- [Tiny-SD 모델 카드](https://huggingface.co/segmind/tiny-sd/tree/cad0bd7495fa6c4bcca01b19a723dc91627fe84f)
- [제작진 구현 및 라이선스](https://github.com/segmind/distill-sd)
- [PyTorch 2.8.0 설치 조합](https://pytorch.org/get-started/previous-versions/#v280)
- [Diffusers 0.35.1 로컬 모델 로딩](https://huggingface.co/docs/diffusers/v0.35.1/en/using-diffusers/loading)
