# 2단계: 경량화·실행 호환성 실험

1단계의 원본 FP32 이미지와 같은 묘사·시드·스케줄러·512×512·25단계·CFG 7을 사용한다. 추가 학습은 수행하지 않는다.

## 실험 구성

| 이름 | U-Net | 복원기 | 활성값 |
|---|---|---|---|
| base | 원본 | 원본 VAE | FP32 |
| taesd | 원본 | TAESD 디코더 | FP32 |
| int8 | Quanto W8 혼합 구성 | 원본 VAE | FP32 |
| int8-taesd | 같은 Quanto W8 혼합 구성 | TAESD 디코더 | FP32 |

8비트화는 Optimum Quanto 0.2.7이 수행한다. U-Net의 147개 Conv2d/Linear 가중치를 출력 채널별 대칭 INT8로 저장한다. 입력·출력층, 시간 임베딩, 정규화·편향은 부동소수점으로 유지한다. 텍스트 인코더는 변경하지 않는다. 활성값 양자화와 학습은 없으며 활성값 보정 데이터도 필요하지 않다. 소형 번역·채점 모델은 이 단계에 포함하지 않는다.

TAESD의 고정 리비전은 `614f76814bbe30edbe2e627ace1c2234c81a2c0e`다. 공개 full safetensors를 로드한 후 Diffusers 디코더 가중치만 추출했다. 생성에 사용하지 않는 인코더를 제거하고, 전체 TAESD와 추출본의 출력이 같은지 검사한다. 디코더는 학습·양자화하지 않았고 FP32를 유지한다. TAESD의 latent scaling factor는 **1.0**이다.

## 준비·실행

1단계 가상환경과 모델 다운로드가 끝난 Windows PowerShell에서 실행한다. 경로는 프로젝트 루트 기준이다. `setup.ps1 -Stage2`는 추가 실험 의존성까지 설치하며 다른 프로그램이나 GPU 설정을 바꾸지 않는다.

```powershell
.\experiments\tiny_sd\setup.ps1 -Python 'C:\path\to\python.exe' -Stage2
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\download_taesd.py
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\prepare_variants.py

.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\benchmark.py --variant taesd --cpu-offload --output .\outputs\tiny-sd\runs\stage2-taesd-offload-02
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\benchmark.py --variant int8 --cpu-offload --save-latents --output .\outputs\tiny-sd\runs\stage2-int8-offload-02
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\decode_cached_latents.py --source .\outputs\tiny-sd\runs\stage2-int8-offload-02 --output .\outputs\tiny-sd\runs\stage2-int8-taesd-offload-02
# 조합 모델의 독립적인 전체 생성 경로 확인
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\benchmark.py --variant int8-taesd --cpu-offload --cases fruit_red --seeds 20260913 --repeats 0 --output .\outputs\tiny-sd\runs\stage2-offload-smoke-01

.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\verify_run.py .\outputs\tiny-sd\runs\stage2-taesd-offload-02
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\verify_run.py .\outputs\tiny-sd\runs\stage2-int8-offload-02
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\verify_run.py .\outputs\tiny-sd\runs\stage2-int8-taesd-offload-02
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\compare_stage2.py
```

`prepare_variants.py`와 `export_onnx.py`는 기존 산출 폴더를 덮어쓰지 않는다. 이미 완료된 산출물이 있으면 해당 준비 명령을 생략한다. 벤치마크를 다시 수행할 때는 새 실행 폴더를 사용하고 비교 스크립트의 PATHS도 명시적으로 바꾼다.

`--cpu-offload`는 활성 신경망 부품만 GPU에 두고 나머지를 CPU에 보관한다. 정밀도·묘사·시드·단계 수는 바꾸지 않는다. 이 모드의 model load는 CPU 로딩까지이며 최초 GPU 전송 비용은 첫 생성에 포함된다.

INT8 단독 실행은 최종 latent를 저장하며 작은 `.npy` 저장 비용도 이 실행 시간에 포함된다. 조합 품질 48장은 저장 파일과 내부 값의 SHA-256을 확인해 **동일 latent를 TAESD로만 복원**한다. 이 48장의 시간은 decoder-only이며 25단계를 다시 실행한 시간이 아니다. 복원기 외의 차이를 통제하고 중복 생성 비용을 줄이는 비교다. 조합 모델의 독립적인 전체 생성 3장은 별도 smoke 실행으로 확인한다.

실제 TAESD 전체 실행은 다른 앱의 GPU 부하에서 수분 단위 지연이 발생해 36장 완료 후 실험 프로세스만 중단했다. `--resume-from .\outputs\tiny-sd\runs\stage2-taesd-full-01`을 추가해 입력 설정·소스·이미지 해시를 확인하고 새 폴더에 기존 36장을 복사한 뒤 나머지 15장을 생성했다. 이전 폴더와 실행 코드는 보존한다. `resumed` 행의 시간·메모리는 이전 조건이며 `new_segment_*`는 새 offload 구간이다. 위의 신규 재현 명령은 처음부터 offload로 실행하므로 실제 혼합 구간의 시간을 그대로 재현하지 않는다.

## ONNX·DirectML 검증

```powershell
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\export_onnx.py
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\verify_onnx.py --provider cpu --output .\outputs\tiny-sd\stage2\onnx-cpu-01
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\verify_onnx.py --provider dml --disable-dml-fusion --disable-metacommands --output .\outputs\tiny-sd\stage2\onnx-dml-safe-03
.\.venv-tiny-sd\Scripts\python.exe .\experiments\tiny_sd\run_onnx_pipeline.py --disable-dml-fusion --disable-metacommands --output .\outputs\tiny-sd\runs\stage2-onnx-pipeline-02
```

- 그래프는 ONNX opset 17이며 CFG batch 2, latent 64×64, token 77의 **고정 입력 크기**로 export했다. 다른 해상도·배치 크기 지원을 검증한 것이 아니다.
- ONNX INT8 값과 scale은 새로 계산하지 않고 저장한 Quanto 가중치에서 옮긴다. **147개 가중치가 모두 매칭되는지 확인**한 뒤 `DequantizeLinear(axis=0)`를 넣는다.
- 원래 FP16에 정확히 표현되는 부동소수점 상수는 FP16으로 저장하고 `Cast`로 FP32에 복원한다. TAESD는 FP32 가중치 그대로다.
- 저장 크기를 줄인 **W8/FP32 실행 그래프**이며 활성값 INT8 연산 가속을 주장하지 않는다. 실행기의 constant folding 또는 GPU 로딩으로 가중치가 FP32로 펼쳐질 수 있다.
- CPU/DirectML 출력은 같은 가중치의 PyTorch 출력과 비교한다. 0.01의 최대 절대 오차 상한은 이 실험의 변환 오류 감지값이며 게임 품질 합격 기준이 아니다.
- DirectML 세션은 메모리 패턴 최적화를 끄고 순차 실행한다. 프로파일의 실제 provider 실행 이벤트를 기록한다. 세션에 DML 이름이 있다는 이유만으로 GPU 실행 성공으로 간주하지 않는다.
- 기본 DirectML graph fusion은 이 PC의 U-Net 실행에서 `887A0006` 오류로 실패했다(`onnx-dml-01`). `--disable-dml-fusion`은 공식 세션 키 `ep.dml.disable_graph_fusion=1`을 적용한다. 이 설정으로 세 부품 모두 실제 DML 실행·수치 검증을 통과했다. 오류 메시지의 한국어 디코딩 예외가 함께 발생했으나 원래 GPU 오류를 해결한 것은 문자 디코딩 수정이 아니다. OS 드라이버/TDR 설정은 변경하지 않았다.
- graph fusion만 끈 전체 생성에서는 오류가 재발했다(`stage2-onnx-pipeline-01`). 최종 성공 실행은 `--disable-metacommands`도 함께 사용해 provider option `disable_metacommands=True`를 적용했다. 이 설정으로 부품 검사와 전체 생성 3건이 통과했으며 실제 장시간 안정성은 추가 검증이 필요하다.
- 전체 생성 실험의 신경망은 ONNX Runtime이 실행하며 스케줄러는 검증된 Diffusers 구현을 CPU에서 호출한다. **C#만으로 실행하는 앱 구현은 아직 아니다.**
- 전체 생성 실험은 텍스트 인코더·U-Net·디코더 세션을 차례로 생성·해제한다. 세션 로딩·해제·프로파일 처리 비용도 각 이미지 생성 시간에 포함하며, 세션을 계속 보관하는 PyTorch 시간과 직접 가속률을 계산하지 않는다.

## 비교 해석

- 모델 파일 크기와 RAM·VRAM을 구분한다. PyTorch allocated/reserved 및 GPU 전체 샘플을 구분해 기록한다.
- 1단계와 현재 데스크톱 부하가 다르다. 품질 생성 도중 수행한 CPU 변환/검사와 다른 앱의 부하도 시간에 영향을 줄 수 있으므로 기존 7.94초 평균과 직접 비교해 가속률을 계산하지 않는다.
- RMSE/PSNR은 같은 시드 이미지가 얼마나 달라졌는지 나타내며 의미 정확도 점수가 아니다. 원본에서 실패한 한국어·위치·개수 사례도 유지한다.
- INT8 단독과 조합 품질의 latent는 저장된 동일 값을 사용한다. 해시 일치는 이 통제 조건의 무결성 검사이며 독립 실행 48쌍의 결정성을 입증한 것이 아니다. 별도 조합 smoke의 같은 입력 결과도 비교한다.
- 산출 모델·원본 PNG·대형 프로파일은 Git에서 제외한다. 작은 manifest·측정 보고서는 보존한다.

## 출처

- [TAESD 모델](https://huggingface.co/madebyollin/taesd/tree/614f76814bbe30edbe2e627ace1c2234c81a2c0e), [TAESD 구현·스케일 설명](https://github.com/madebyollin/taesd), [MIT 라이선스](https://github.com/madebyollin/taesd/blob/main/LICENSE)
- [Quanto 가중치 양자화·freeze·serialize·requantize](https://github.com/huggingface/optimum-quanto)
- [ONNX Runtime 양자화 표현](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html), [DirectML 실행 제약](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- [ORT 1.22.0 DirectML 세션 키 정의](https://github.com/microsoft/onnxruntime/blob/v1.22.0/onnxruntime/core/providers/dml/dml_session_options_config_keys.h)
