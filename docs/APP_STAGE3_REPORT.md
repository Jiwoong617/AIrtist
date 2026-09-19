# 3단계 — 최소 앱 연결과 배포 용량 검증

- 정리일: 2026-09-15. 2026-09-14~15 개발·검증 기록.
- 상태: **C#·WPF 최소 앱의 한국어 번역 → 이미지 생성 → 실제 이미지 채점 연결과 초기 자체 포함 패키지 검증 완료.**
- 당시 실행 파일: `dist/AIrtist-preview-03/AIrtist.exe`. 구버전은 용량 정리로 제거했으며 현재 실행 파일은 `testbuild/AIrtist.exe`다. [정리 내역](WORKSPACE_CLEANUP.md)
- [빌드·재현 명령과 임시 규칙](APP_DEVELOPMENT.md). 이 보고서는 Git에 보관하며 아래 `outputs/`·`dist/` 원자료는 로컬 산출물로 Git에서 제외한다.

## 구현한 범위

한국어 묘사를 입력하고 버튼을 누르면 같은 PC에서 OPUS-MT로 번역하고, Tiny-SD U-Net W8 + TAESD로 512×512 그림 한 장을 만든 뒤 TinyCLIP으로 제시어와 그림의 코사인 유사도를 계산한다. 생성기는 번역된 묘사와 공통 스타일만 받고, 채점기는 최종 이미지와 제시어 ID만 받는다. 제시어를 번역기·생성기에 주입하지 않는다.

`AIrtist.Core`가 제출 승인·생성 1회 제한·취소·시간 초과·실패·오래된 결과 거절을 관리한다. UI는 명령을 요청하고 결과를 표시한다. 번역·생성·채점은 작업 스레드에서 순차 실행하며 부품마다 ONNX 세션을 해제한다. 생성 중 취소는 ORT `RunOptions.Terminate`와 취소 토큰으로 전달한다. 승인된 입력은 고정되며 실패·취소 후에는 다음 제시어로 넘어간다.

현재는 임시 8개 제시어로 한 장씩 연습하는 앱이다. 180초 제한·입력 2~160자·별칭의 정규화 부분 문자열 검사·점수 변환 규칙은 임시 구현이다. 여러 라운드, 작성 마감, 최종 승자, EOS 연결은 아직 없다. 실제 IME 조작은 미검증이며 Enter를 제출에 연결하지 않고 줄바꿈으로 처리하는 설정을 검사했다.

## 선택한 모델·런타임

| 구성 | 고정 버전 또는 리비전 | 앱에서의 사용 |
|---|---|---|
| Tiny-SD | `cad0bd7495fa6c4bcca01b19a723dc91627fe84f` | 기존 U-Net 혼합 INT8·텍스트 인코더 ONNX, FP32 연산 |
| TAESD | `614f76814bbe30edbe2e627ace1c2234c81a2c0e` | 기존 FP32 디코더 ONNX |
| OPUS-MT ko-en | `e42d1f41b66194e6d10512f8a27bebc1f4f5097e` | FP16 저장·FP32 연산, greedy 번역, CPU |
| TinyCLIP ViT-8M/16 Text-3M | `a2a8c6eaa2549ad66eb7c31b85022bf58273a26c` | FP32 이미지 인코더·projection ONNX와 같은 모델의 사전 계산 텍스트 벡터, CPU |
| ONNX Runtime DirectML | `1.22.0` | 신경망 실행, 생성 3부품은 DirectML |
| 동봉 DirectML | NuGet `Microsoft.AI.DirectML 1.15.4` | x64 DLL 18,527,776바이트 |
| Microsoft.ML.Tokenizers | `2.0.0` | Marian SentencePiece Unigram 처리 |
| .NET SDK / 실행 런타임 | `10.0.103` / `10.0.3 win-x64` | WPF 자체 포함 배포, 트리밍·Native AOT 미사용 |

학습·미세조정·직접 증류는 수행하지 않았다. OPUS-MT는 공유 임베딩과 출력 가중치의 중복을 제거하고 FP16으로 저장한 뒤 ONNX `Cast`로 FP32 연산한다. 모델 파일은 155,801,363바이트다. 번역 INT8 후보는 초록색 과일 묘사가 무관한 감탄사로 바뀌는 사례가 있어 채택하지 않았다. FP16 저장 후보도 모든 묘사의 의미를 보존한다고 확정하지 않는다.

MobileCLIP은 초기 변환 후보였지만 확인한 `LICENSE_MODELS`가 연구 목적 사용으로 제한하고 제품 개발을 제외한다. 현재 패키지에는 넣지 않았다. MIT로 공개된 TinyCLIP으로 교체하고 이미지 인코더만 배포한다. TinyCLIP 이미지 ONNX는 **33,207,974바이트**다. [MobileCLIP 확인 버전의 모델 라이선스](https://github.com/apple/ml-mobileclip/blob/48faa0fea4b08d74188b3841771aca6ff2c92852/LICENSE_MODELS), [TinyCLIP 공식 구현](https://github.com/microsoft/Cream/tree/main/TinyCLIP), [사용한 공개 체크포인트](https://huggingface.co/wkcn/TinyCLIP-ViT-8M-16-Text-3M-YFCC15M/tree/a2a8c6eaa2549ad66eb7c31b85022bf58273a26c).

DirectML의 `ep.dml.disable_graph_fusion=1`과 provider option `disable_metacommands=True`를 C#의 generic provider API에 전달한다. 프로파일은 실제 GPU 실행 이벤트로 확인했다. 초기 패키지 점검에서 OS DirectML DLL 사용을 발견해 x64 DLL 복사를 명시했고, 최종 패키지는 .NET·ORT·DirectML을 모두 **자체 폴더에서 로드**했다. [ORT 1.22 provider 등록 구현](https://github.com/microsoft/onnxruntime/blob/v1.22.0/onnxruntime/core/session/provider_registration.cc), [실제 로드 경로](../outputs/app-stage3/packaged-process-03.json).

## 실제 크기

압축 크기가 아닌 최종 폴더의 442개 파일 전체 바이트 합이다. 라이선스·manifest·안내문까지 포함한다.

| 범위 | 바이트 |
|---|---:|
| 생성 모델·토크나이저·25단계 설정 | 580,082,624 |
| 번역 모델·SentencePiece·어휘 | 158,363,034 |
| TinyCLIP 이미지 모델·단어 벡터·설정 | 33,330,912 |
| 앱·.NET/WPF·ORT/DirectML·안내문 | 183,232,494 |
| 라이선스·모델/패키지 목록 | 570,139 |
| **현재 패키지 합계** | **955,579,203** |
| **1,000,000,000바이트 예산의 잔여** | **44,420,797** |

현재 최소 앱은 1GB 안에 들어간다. **EOS, 추가 에셋, 완성된 경기 기능을 포함한 최종 게임의 1GB 달성을 확정한 것은 아니다.** MSI 등 설치 프로그램은 아직 없으며 자체 포함 실행 폴더를 측정했다. 모델·DLL 해시와 파일 목록을 패키지에 보관한다. [용량 집계](../outputs/app-stage3/package-size.json), [전체 파일 목록](../dist/AIrtist-preview-03/package-manifest.json).

## 수행한 검사

- C# Release 빌드 성공, 오류·경고 0개. Python 변환 소스와 PowerShell 빌드 스크립트 구문 검사. 개발용 `sacremoses`의 누락 의존성 `click==8.1.8`을 고정·설치한 뒤 `pip check`도 통과했다.
- 규칙 검사: NFKC·공백·제로폭 문자·전각 영문 별칭 차단, 유효하지 않은 입력이 생성권을 소비하지 않음, 진행 중/완료 후 중복 제출 거절, 번역된 정답 차단, 다음 제시어에 늦은 결과 미반영, 취소·시간 초과 전이.
- CLIP 토크나이저 13건, Marian 토크나이저와 실제 번역 12건을 Python 기준값과 비교해 일치 확인. 이는 Python/C# 구현 일치 검사이며 번역의 모든 의미가 맞다는 판정이 아니다.
- Diffusers 0.35.1로 만든 25단계 스케줄러 참조값 대비 최대 오차 `3.815e-6`. 고정 25단계 DPM-Solver++/2차 midpoint 계수에 한정한다.
- TinyCLIP ONNX를 원래 PyTorch 모델과 비교하고, C# 전처리·채점을 영어 생성 이미지 3장과 8개 단어 벡터로 검사. Python 대비 코사인 최대 차이 `7.811e-5`. Pillow와 C# 리사이즈 사이 최대 채널 차이는 1/255이며 정규화 후 입력 최대 차이는 `0.015008`이었다. [참조 검사](../outputs/app-stage3/csharp-reference-checks.json).

동봉 DirectML 1.15.4를 사용한 C# 생성 3건은 기존 Python의 같은 초기 잠재값·묘사·512×512·25단계·CFG 7 결과와 비교했다.

| 영어 묘사 | 전체 생성 시간 | 최대 채널 차이 | 픽셀 RMSE (0~255) |
|---|---:|---:|---:|
| 빨간 둥근 과일 | 22.24초 | 1 | 0.01591 |
| 긴 귀·흰 털의 작은 동물 | 22.09초 | 1 | 0.00943 |
| 노란 상자 아래의 빨간 구체 | 21.92초 | 1 | 0.01093 |

매 이미지마다 세션 로딩·해제를 포함한 시간이다. 각 실행에서 텍스트 인코더 375회, U-Net 22,025회, 디코더 52회의 DML provider 이벤트를 확인했다. 동일 초기 잠재값을 주입한 참조 검사이며, 앱의 .NET 시드와 Python 시드가 같은 난수를 만든다는 주장은 아니다. 기존 실험과 데스크톱 부하·런타임 조건이 달라 속도 향상률로 해석하지 않는다. [생성 기록](../outputs/app-stage3/csharp-generation-bundled/results.json), [픽셀·실제 provider 검사](../outputs/app-stage3/csharp-generation-bundled-comparison.json).

최종 배포 EXE의 WPF 창 클래스로 `귀가 길고 하얀 털을 가진 작은 동물`을 제출해 그림·점수 표시까지 완료했다. 번역은 `A long-eared, white-haired little animal.`이었다. 생성 중 UI Dispatcher 타이머와 완료 후 버튼 상태를 검사했고, 결과와 화면은 아래 로컬 기록에 보관했다. [.NET/WPF 통합 실행](../outputs/app-stage3/packaged-smoke-03/smoke.json), [결과 화면](../outputs/app-stage3/packaged-smoke-03/app-result.png).

실측 PC는 Ryzen 5 5600X, GTX 1660 Ti 6GB, RAM 약 48GiB, Windows 11이다. 다른 앱을 종료하거나 드라이버·TDR 설정을 변경하지 않았다. 개발 도구 없는 PC의 실행을 측정한 것은 아니다.

## 남은 제약과 다음 작업

번역에서 `껍질을 벗겨 먹는 과일`이 껍질을 먹는다는 뜻으로 변하는 등 의미 왜곡이 남았다. 정답 별칭 차단만으로 전체 의미 보존을 보장할 수 없다. Tiny-SD의 위치·개수 표현 한계도 기존 실험에서 확인됐다. TinyCLIP의 유사 오답·다른 화풍·글자 이미지 평가와 점수 보정은 아직 끝나지 않았다. 현재 점수는 제품 품질 합격 기준이 아니다.

다음은 **저사양·반복 실행과 오프라인 한 경기**다. 우선 번역·채점 평가 사례를 늘리고 실패 후 재시작, 취소·마감 경계, 메모리 회수, 실제 IME·DPI를 확인한다. 4GB VRAM·16GB RAM은 별도 기기가 있어야 실제 사양 검증으로 보고할 수 있다. 이후 작성 시간·여러 라운드·최종 결과를 연결한다. EOS·이미지 전송·호스트 동기화·다른 회선의 두 PC·깨끗한 Windows 설치는 이후 단계로 남는다.
