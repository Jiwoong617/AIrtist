# AIrtist

Windows PC용 로컬 AI 이미지 묘사 게임. C#·WPF에서 한국어 번역 → Tiny-SD 이미지 생성 → TinyCLIP 이미지 평가와 오프라인 여러 라운드·최종 결과를 연결했다. 번역 의미 왜곡과 채점 오판이 확인돼 품질 개선이 필요하며, 멀티플레이는 구현 전이다.

- [통합 기획서](AI_Image_Game_Summary.md)
- [개발 지침](AGENTS.md)
- [최소 앱 실행·빌드·검사](docs/APP_DEVELOPMENT.md)
- [3단계 구현·배포 용량 결과](docs/APP_STAGE3_REPORT.md)
- [4단계 오프라인 경기·반복 실행·품질 결과](docs/APP_STAGE4_REPORT.md)
- [폴더 정리와 testbuild 실행 안내](docs/WORKSPACE_CLEANUP.md)
- [Tiny-SD 실험 환경 및 재현 명령](experiments/tiny_sd/README.md)
- [Tiny-SD 원본 검증 결과](outputs/tiny-sd/BASELINE_REPORT.md)
- [경량화·ONNX 실행 검증](outputs/tiny-sd/stage2/COMPRESSION_REPORT.md)
- [작업 폴더 용량 설명](outputs/tiny-sd/stage2/WORKSPACE_SIZE.md)

모델과 개발용 가상환경, 생성 이미지 원본은 Git에서 제외한다. 게임 엔진·유료 AI API·추가 모델 학습은 사용하지 않는다.

이 개발 PC의 최신 실행 파일은 `testbuild/AIrtist.exe`다. 모델과 .NET·WPF·ONNX Runtime·DirectML을 포함한 현재 패키지는 **955,637,367바이트**다. `testbuild` 폴더 전체를 함께 사용한다. 기본 3라운드·작성 60초·AI 처리 180초이며 시작 화면에서 바꿀 수 있다. EOS·설치 프로그램·저사양/깨끗한 PC 검증은 남아 있다.

중복 배포본·중간 모델·Python 실험 환경을 정리해 작업 폴더를 약 **18.96GB → 3.44GB**로 줄였다. 현재 앱 실행과 준비된 모델을 사용하는 C# 빌드·패키징에는 Python이 필요하지 않다. 변환 실험을 재개할 때만 환경과 필요한 원본을 다시 준비한다.

Git에는 기획·개발 문서, 변환·추론·검증을 재현할 소스와 잠금 파일, 최종 보고서·모델 출처·라이선스를 보관한다. 실행 로그·프로파일·잠재값·실행 환경 및 용량 스냅샷은 로컬 산출물로 제외한다. 보고서의 로컬 산출물 링크는 이를 보관한 개발 PC에서만 열리며, 저장소 복제만으로 원본 실험 결과가 내려오지는 않는다.
