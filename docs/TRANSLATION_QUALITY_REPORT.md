# 후속 번역 모델 검증 — 2026-09-20

> **기획 변경 안내:** 2026-09-21부터 AI 채점을 폐지하고 그림그리기·멀티 투표로 전환한다. 이 문서의 번역 비교·미채택 이유는 계속 유효한 기록이며, 현재 구현 순서는 [통합 기획서](../AI_Image_Game_Summary.md)를 따른다.

**후보의 실행·경량화 비교를 완료했지만 기본 번역 모델 교체는 보류했다.** 갈기·짖는 반려동물·위치·줄무늬 개수 표현이 일부 개선되는 동시에 다의어·부정·형태에서 회귀가 있었다. 현재 앱 자산과 `testbuild/AIrtist.exe`는 이전 검증 구성인 **963,310,223바이트** 패키지를 유지한다. 4단계의 번역 품질 전체 완료로 취급하지 않는다.

## 후보와 토크나이저 문제

- 원본: [Helsinki-NLP/opus-mt-tc-big-ko-en](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-ko-en), revision `fa26583a41d95346933f26b4cb8f9b700da0d445`, CC-BY-4.0. 파일별 해시는 `experiments/translation_quality/source.json`에 기록했다. 제품에 넣을 경우 모델 출처·변경 사항·[CC-BY-4.0 고지](https://creativecommons.org/licenses/by/4.0/)가 추가로 필요하다.
- 기본 `vocab.json`·단일 어휘표 설정에서는 한국어 입력 대부분이 `<unk>`가 되어 빈 번역·무관한 출력이 생겼다. 이 문제는 [원본 저장소의 수정 제안](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-ko-en/discussions/5)에도 보고돼 있다.
- 원본의 `source.spm`·`target.spm` ID에서 각각의 어휘표를 만들고 `separate_vocabs=True`로 분리해 정상 번역을 확인했다. EOS=2, PAD/decoder 시작=32000이다. Windows 로캘의 CP949 읽기 오류는 JSON ASCII escape 저장으로 해결했다.
- 생성기·번역기에 정답·참고 영어 의미를 넣거나 특정 제시어로 치환하지 않았다. 공개 가중치의 추론·변환만 했으며 새 학습·미세조정·증류를 하지 않았다.

## 실측 구성과 변환 검증

CPU PyTorch 2.8.0, Transformers 4.56.2, ONNX 1.19.0, ONNX Runtime 1.22.0, SentencePiece 0.2.1, Python 3.12.14, CPU 스레드 6개. Ryzen 5600X/RAM 약 48GiB의 현재 PC에서 측정했으며 별도 저사양 PC 결과가 아니다. 원본의 FP16 저장 가중치를 FP32 계산으로 불러왔다.

| 항목 | 실측 결과 |
|---|---:|
| FP32 ONNX 전체 파일 | 972,182,785바이트 |
| 혼합 정밀도 ONNX | 173,667,124바이트 |
| INT4 FFN 행렬 | 24개, block32 대칭 양자화 |
| INT8 저장 행렬 / FP16 저장 텐서 | 75개 / 156개 |
| PyTorch greedy와 FP32 ONNX의 동일 출력 | 개발 40/40, 별도 16/16 |
| 원본 greedy와 혼합 모델의 동일 출력 | 개발 32/40, 별도 15/16 |

FFN만 INT4, 그 외 큰 행렬은 INT8 저장이며 활성값은 FP32다. 공유 임베딩/최종 출력 가중치의 전치 사본을 중복 저장하지 않았다. INT4에는 [ORT MatMulNBits](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html)를 사용했다. 보정 데이터·학습 없이 변환했다. 혼합 파일 SHA-256: `52c04ee20d81d5040a6b216af34f888ac6b1b2a2555fd2495345081d2b06e2b7`.

위 일치 수는 **문자열 일치이며 번역 정확도가 아니다.** 문구 차이가 의미를 유지하는 경우도, 꼬리 같은 중요한 속성이 빠지는 경우도 있다. 파일 크기는 모델 단독 실측이다. 토크나이저·새 고지·C# 런타임을 합친 후보 패키지는 만들지 않았으므로 후보 전체 설치 크기나 앱 호환성을 완료했다고 보고하지 않는다.

혼합 모델 로딩은 개발/별도 실행에서 1.25/1.31초였다. 세션을 재사용한 한국어 번역은 개발 38건 평균 1.78초(0.90~2.75), 별도 15건 평균 2.23초(1.13~3.44)다. 영어 3건은 그대로 전달하며 평균에서 제외했다. 기존 C# 번역의 매 호출 세션 로딩 포함 시간과 측정 범위가 달라 직접적인 속도 향상 비교에 쓰지 않는다.

## 의미 검토와 미채택 이유

개발 문장은 앞선 실험에서 본 40개다. 새 16개는 후보 실행 전에 고정했다. greedy·혼합 정밀도를 정한 뒤 새 문장을 평가했으며 그 결과로 다시 설정을 맞추지 않았다. 개발 40개에서는 원본의 4개 후보 탐색도 비교했지만 갈기 누락·짖는 소리 오역이 남아 단순 탐색 수 증가로 해결되지 않았다.

| 묘사의 핵심 | 기존 앱 | 새 혼합 모델 | 판단 |
|---|---|---|---|
| 멍멍 짖는 작은 반려동물 | `connoitant`, 반복된 `howling` | `Small pet barking.` | 개선 |
| 노란 상자 **위** 빨간 공 | `in the yellow box` | `on a yellow box` | 개선 |
| 검은 줄무늬 3개인 주황 공 | `Three-silver orange balls.` | `Orange ball with three black stripes` | 개선 |
| 갈색 털·긴 갈기·네 발 동물 | 동물 4마리·`mangles`로 왜곡 | `Four-legged animal with brown hair and long mane` | 새 16문장에서 개선 |
| 빨간 과일과 파란 **용기** | `blue containers` | `blue courage` | 회귀 |
| 빨갛지 않고 노란 긴 과일 | 부정 범위가 모호 | `red and yellow`로 부정 소실 | 미해결·회귀 |
| 노란 별 아래 파란 **네모** | `blue square` | `blue box` | 새 16문장에서 형태 회귀 |
| 문 앞에서 **꼬리** 흔드는 반려동물 | 반려동물 오역, 꼬리는 유지 | `Small animals are waving at the door.` | 반려 의미·꼬리 누락 |
| 껍질 두껍고 **속** 노란 둥근 과일 | 껍질·안쪽 속성 손실 | `Thick, yellow, round fruit` | 양쪽 미해결 |

마지막 꼬리 사례의 새 원본/FP32 ONNX는 `shake their tails`를 유지했지만 혼합 모델에서는 빠졌다. 다른 15개 별도 문장은 원본 greedy와 출력이 같았다. 네모→상자, 용기→용감함, 부정 소실은 양자화 전부터 존재하므로 정밀도를 올리는 것만으로 해결되지 않는다. 고양잇과를 별개의 고양이와 동물처럼 표현하거나 탈것을 바퀴만으로 줄이는 문제도 남는다.

일부 문장의 개선보다 **이전 정상 문장의 핵심 의미를 바꾸는 회귀**를 우선해 미채택했다. 체크포인트에 있는 공식 어휘표 문제와 용량 문제의 해결 가능성은 확인했지만 제품 번역 품질 합격은 아니다.

## 변경·검사·정리

- `experiments/translation_quality/`: 고정 다운로드·토크나이저 복원·변환·40+16 입력 자료·원본/ONNX 평가·결과 집계·재현 안내를 추가했다.
- `tests/AIrtist.Checks/TranslationBaselineChecks.cs`, `Program.cs`: 현재 앱 번역으로 같은 고정 문장을 기록하는 `quality-translation-baseline` 검사를 추가했다.
- 56개 묘사의 원본/FP32 ONNX 일치, 혼합 모델 56개 실행, 현재 C# 모델의 새 16문장 실행, Python 문법 검사, C# 규칙·경기 검사와 기존 배포본 자체 검사를 수행했다. 의미 검토는 위 표와 같이 별도로 판정했다.
- 후보 C# 연동·새 모델의 실제 이미지 생성·저사양·장시간 반복·IME/DPI는 이번에 검증하지 않았다. 앱 모델을 교체하지 않았으므로 새 EXE를 배포하지 않았다.
- 정리 후 기존 패키지 manifest에 등록된 442개 파일의 크기·SHA-256도 일치했다. manifest 자체를 포함한 443개 파일 합계는 963,310,223바이트다.
- 원자료는 `outputs/translation-quality`에, 재현 명령은 [실험 안내](../experiments/translation_quality/README.md)에 있다. Git에는 소스·고정 입력·출처·이 보고서를 보관한다. 검사 후 신규 실험 환경·미채택 원본·중간 모델과 재생성한 빌드 산출물 **5,244,183,931바이트**를 정리해 작업 폴더를 약 3.80GB로 되돌렸다. 기존 앱과 채택 원본은 보존했다. [폴더 정리 기록](WORKSPACE_CLEANUP.md)을 참고한다.

이후 [생성 전 번역 확인·수정](TRANSLATION_REVIEW_REPORT.md)을 구현했다. 2026-09-21의 다음 작업은 해당 흐름을 재사용한 그림그리기 전환과 AI 채점 의존성 제거다. 후속 번역 후보를 검토할 때는 이번 회귀 문장과 새로운 비교 문장을 사용한다. 멀티 투표·EOS·저사양·장시간·깨끗한 PC 검증은 [새 작업 순서](../AI_Image_Game_Summary.md)를 따른다.
