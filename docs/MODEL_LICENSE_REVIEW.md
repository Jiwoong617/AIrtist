# AI 모델 공개·재배포 라이선스 점검

확인일: 2026-10-19. 대상: 현재 `testbuild`의 이미지 생성·한국어 번역 모델과 관련 토크나이저. 기준 소스 커밋: `02093a8`.

**후속 반영:** 사용자 승인에 따라 설정 옆 라이선스 화면·모델 이용 조건·출처·파일별 변경 고지와 OPUS-MT의 두 공식 표기를 배포에 반영했다. 구현·최종 패키지 검증은 [라이선스 적용 보고서](LICENSE_IMPLEMENTATION_REPORT.md)를 참고한다. 아래 패키지 대조와 작업 범위는 보완 전 조사 당시 기록이며, OPUS-MT 공식 표기 차이는 여전히 미확정이다.

공개된 라이선스와 실제 배포 파일을 대조한 기술적 점검이며 법률 자문이나 권리 관계 전체에 대한 보증은 아니다. EOS·.NET 등 실행 라이브러리의 계약은 이번 점검 범위에 포함하지 않는다.

## 판단

**사용 모델은 조건을 지키면 GitHub 공개, 수정본 재배포, 게임 동봉 배포가 가능한 라이선스로 안내되어 있다. 다만 현재 패키지를 아무 조건 없이 배포해도 된다고 결론 내릴 수는 없다.** Tiny-SD의 사용자 대상 사용 제한 전달을 보완해야 하며, OPUS-MT에는 공식 자료 간 라이선스 표기 차이가 있다.

| 구성 | 공개 라이선스 표기 | 모델 원본·수정본 공개/재배포 | 상업적 게임 포함 |
|---|---|---|---|
| `segmind/tiny-sd` | CreativeML Open RAIL-M | 사용 제한·라이선스·변경 고지 조건부 허용 | 같은 조건 아래 허용 |
| `madebyollin/taesd` 복원기 | MIT | 저작권·허가문 유지 조건부 허용 | 허용 |
| `Helsinki-NLP/opus-mt-ko-en` | 사용한 HF 버전: Apache-2.0 / 제작진 일반 안내: CC BY 4.0 | 두 라이선스 모두 허용하나 적용 표기 확인 필요 | 두 라이선스 모두 허용 |

근거: [Tiny-SD 고정 모델 카드](https://huggingface.co/segmind/tiny-sd/blob/cad0bd7495fa6c4bcca01b19a723dc91627fe84f/README.md), [제작진 Open RAIL-M 원문](https://github.com/segmind/distill-sd/blob/master/LICENSE), [TAESD 모델 카드](https://huggingface.co/madebyollin/taesd/raw/614f76814bbe30edbe2e627ace1c2234c81a2c0e/README.md), [MIT 원문](https://github.com/madebyollin/taesd/blob/main/LICENSE), [OPUS-MT 고정 모델 카드](https://huggingface.co/Helsinki-NLP/opus-mt-ko-en/blob/e42d1f41b66194e6d10512f8a27bebc1f4f5097e/README.md), [제작진 사전학습 모델 안내](https://github.com/Helsinki-NLP/OPUS-MT-train#pre-trained-models).

## 실제 사용 출처

| 모델 | 고정 리비전 | 현재 게임에 들어간 형태 |
|---|---|---|
| Tiny-SD | `cad0bd7495fa6c4bcca01b19a723dc91627fe84f` | U-Net 혼합 INT8 저장·FP32 연산, 텍스트 인코더 ONNX 변환, 생성 토크나이저 |
| TAESD | `614f76814bbe30edbe2e627ace1c2234c81a2c0e` | 원래 VAE 대신 디코더를 추출·ONNX 변환 |
| OPUS-MT ko-en | `e42d1f41b66194e6d10512f8a27bebc1f4f5097e` | 공유 임베딩 재사용, FP16 저장·FP32 연산용 ONNX, 번역 토크나이저 |

근거 파일은 `outputs/tiny-sd/provenance.json`, `outputs/tiny-sd/stage2/taesd-manifest.json`, `models/app-stage3-source-manifest.json`, `testbuild/models/manifest.json`, `testbuild/models/licenses/CHANGES.txt`다. 모델을 새로 학습한 것이 아니며, 변환·양자화했다고 원본 조건이 없어지지는 않는다.

Tiny-SD 모델 카드는 기반 모델을 `SG161222/Realistic_Vision_V4.0`으로 명시한다. 해당 주소는 현재 `Realistic_Vision_V4.0_noVAE`로 연결되며, 기존에 기록한 [기반 모델 리비전](https://huggingface.co/SG161222/Realistic_Vision_V4.0_noVAE/blob/1685907c0283c7278ba26c5fe561506f564b48d3/README.md)도 CreativeML Open RAIL-M으로 표시한다. 이 리비전은 기반 모델의 라이선스 확인 기록이며 Segmind가 실제 증류에 쓴 기반 가중치의 정확한 커밋까지 입증하지는 않는다.

## Tiny-SD: 배포 시 필요한 조건

[Open RAIL-M 4·5항](https://github.com/segmind/distill-sd/blob/master/LICENSE)을 기준으로 다음을 유지해야 한다.

- 라이선스 전문과 기존 저작권·출처 고지를 수신자에게 제공한다.
- 수정 파일에는 변경 사실을 명확히 표시한다.
- 5항과 Attachment A의 사용 제한을 사용·배포에 적용되는 계약에 집행 가능한 조항으로 포함하고, 사용자에게 적용 사실을 알린다.
- 재배포받는 사용자에게도 해당 사용 제한을 준수하도록 요구한다.

Attachment A는 불법 사용, 미성년자 위해, 타인을 해치기 위한 허위 정보·개인정보 유포, 괴롭힘·차별 등 특정 용도를 제한한다. 이 예시는 요약이며 전체 제한 목록을 대체하지 않는다. 무료 테스트 배포에도 조건은 적용된다.

라이선스 사본을 폴더에 넣는 것과 사용 제한을 계약·공지에 반영하는 것은 별개다. 특정 모양의 동의 팝업을 만들라는 조항은 없다. 배포 안내·모델 이용 조건·사용자에게 알리는 방식은 마련하되, 계약 효력은 실제 배포 방식에 맞춰 확인해야 한다.

## TAESD와 CLIP 관련 고지

TAESD는 모델 카드의 MIT 표기와 제작진 라이선스가 일치한다. 저작권 표시와 허가문을 보존하면 복사·수정·재배포·판매가 허용된다. 현재의 디코더 추출·ONNX 변환에도 고지를 유지한다. [모델 카드](https://huggingface.co/madebyollin/taesd/raw/614f76814bbe30edbe2e627ace1c2234c81a2c0e/README.md), [MIT 원문](https://github.com/madebyollin/taesd/blob/main/LICENSE).

생성용 텍스트 인코더·토크나이저는 Tiny-SD에서 가져온 구성품이다. 함께 제공하는 `CLIP_LICENSE.txt`는 [OpenAI CLIP의 MIT 고지](https://github.com/openai/CLIP/blob/main/LICENSE)다. 이 MIT 고지가 Tiny-SD 전체의 Open RAIL-M 조건을 대체한다고 해석하지 않는다.

## OPUS-MT: 공식 표기 차이

다운로드한 정확한 버전의 [모델 카드](https://huggingface.co/Helsinki-NLP/opus-mt-ko-en/blob/e42d1f41b66194e6d10512f8a27bebc1f4f5097e/README.md)는 Apache-2.0이다. 반면 [제작진 OPUS-MT 저장소](https://github.com/Helsinki-NLP/OPUS-MT-train#pre-trained-models)는 사전학습 모델과 Tatoeba Challenge의 추가 모델을 CC BY 4.0으로 안내한다. 해당 HF 카드는 Tatoeba의 `opus-2020-06-17.zip`을 원본으로 연결한다.

**Apache-2.0만 유일한 조건이라고 확정하거나, 임의로 이중 라이선스라고 선언하지 않는다.** 개별 HF 배포에 별도 허가가 있는지, 메타데이터 오류인지 이번 공개 자료만으로는 확정하지 못했다.

- Apache-2.0 기준: 라이선스 사본 제공, 수정 사실 표시, 해당 저작권·출처 고지 보존, 상류 NOTICE가 있으면 해당 고지 유지가 필요하다. 수정본·상업적 배포를 허용한다. [Apache 2.0 2~4항](https://www.apache.org/licenses/LICENSE-2.0).
- CC BY 4.0 기준: 제작자·출처·라이선스 링크와 제공된 고지를 유지하고 변경 사실을 표시한다. 수정본·상업적 배포를 허용하지만, 허용된 권리를 추가 약관이나 기술적 제한으로 막을 수 없다. [CC 공식 설명](https://creativecommons.org/licenses/by/4.0/), [원문 2·3항](https://creativecommons.org/licenses/by/4.0/legalcode.en).

배포 준비 방안은 기존 Apache 원문·모델 카드를 보존하면서, Helsinki-NLP 출처·정확한 버전·FP16/ONNX 변경 내역과 제작진 CC BY 안내를 함께 기록하는 것이다. 이는 두 표기의 고지 요건을 함께 고려하는 제안이며, 그 자체로 표기 차이가 해소되지는 않는다. 적용 라이선스를 하나로 확정해야 한다면 제작진에게 이 HF 리비전을 지정해 확인해야 한다. 이번 조사에서 문의를 보내지는 않았다.

## 보완 전 패키지 대조 결과

대상: `testbuild/models/licenses/`. 선택한 모델 파일 4개와 라이선스·고지 파일 8개의 SHA-256이 현재 모델 manifest와 모두 일치했다.

| 점검 항목 | 현재 상태 |
|---|---|
| Tiny-SD 라이선스 전문 | `Tiny-SD_LICENSE.txt` 포함. 기존 Segmind 원문 사본 해시와 일치 |
| TAESD 저작권·MIT 전문 | `TAESD_LICENSE.txt` 포함 |
| CLIP MIT 전문 | `CLIP_LICENSE.txt` 포함 |
| OPUS-MT 모델 카드·Apache 전문 | `OPUS-MT_MODEL_CARD.md`, `Apache-2.0.txt` 포함 |
| 모델 변경 고지 | `CHANGES.txt`, `UNet_NOTICE.txt`, `TAESD_NOTICE.txt` 포함 |
| Tiny-SD 제한의 사용자 안내 | README에는 라이선스 폴더 안내만 있음. 검사한 앱 소스·배포 안내에서 별도 이용 조건 반영은 확인하지 못함 |
| OPUS-MT의 CC BY 출처·조건 안내 | 모델 관련 고지에 없음. 공식 표기 차이를 설명하고 보완할 필요 |
| 수정 파일별 명확한 연결 | 기존 고지는 모델 단위 요약. 최종 ONNX 파일명별 원본·변경 내용 연결을 보강하는 것을 권장 |

현재 사본·변경 고지가 있는 점은 확인했으나 **이용 조건 전달까지 완료된 배포물이라고 검증한 것은 아니다.** 후속 보완에서는 모델별 출처 목록과 파일별 변경 고지를 배포에 포함하고, Tiny-SD 사용 제한의 계약·공지 반영 및 OPUS-MT 표기 정리를 처리한다. 모델 고지를 바꾸면 manifest 및 패키지 해시도 다시 생성·검사해야 한다.

## GitHub 저장소와 Releases

GitHub 공개 저장소에 모델을 올리는 것과 게임 ZIP에 넣어 Releases로 배포하는 것은 모두 제3자 배포에 해당하므로 모델 조건을 유지한다. 이 점검에서 발견한 조건은 모델을 사용한다고 게임 전체 소스 공개를 일괄 강제하는 조건은 아니다. 게임 소스의 라이선스는 별도로 정하며, 제3자 모델까지 게임 고유 라이선스로 덮어쓰지 않는다.

현재 Git 추적 목록에는 모델 가중치가 없다. `.gitignore`는 `models/`, `testbuild*/`, ONNX·체크포인트 등을 제외한다. 이는 저장소 관리 방침이며 재배포 금지 판정은 아니다.

일반 GitHub 저장소는 파일 하나가 100MiB를 넘으면 업로드가 차단된다. 현재 U-Net 327,169,011바이트, 텍스트 인코더 246,409,166바이트, 번역 모델 155,801,363바이트가 이를 넘는다. 따라서 소스·출처·고지는 Git에 보관하고, 조건을 보완한 모델 포함 실행 ZIP은 Releases 첨부 파일로 배포하는 구성을 유지한다. [GitHub 대용량 파일 안내](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github).

## 최초 조사 작업 범위

모델 출처·공식 조건 조사, 실제 패키지 고지·해시 대조와 이 보고서 작성을 완료했다. 앱 코드·모델·testbuild·이용약관은 변경하지 않았고 공개 업로드도 수행하지 않았다. 문서 작업이므로 앱 실행 검사는 하지 않았다. 이 보고서 작성만으로 위 배포 보완 사항이 해결되지는 않는다.
