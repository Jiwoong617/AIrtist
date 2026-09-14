# AIrtist

Windows PC용 로컬 AI 이미지 묘사 게임. Tiny-SD 원본·경량화 품질 비교와 ONNX/DirectML의 초기 실행 검증을 완료했으며, 제품 앱은 구현 전이다.

- [통합 기획서](AI_Image_Game_Summary.md)
- [개발 지침](AGENTS.md)
- [Tiny-SD 실험 환경 및 재현 명령](experiments/tiny_sd/README.md)
- [Tiny-SD 원본 검증 결과](outputs/tiny-sd/BASELINE_REPORT.md)
- [경량화·ONNX 실행 검증](outputs/tiny-sd/stage2/COMPRESSION_REPORT.md)
- [작업 폴더 용량 설명](outputs/tiny-sd/stage2/WORKSPACE_SIZE.md)

모델과 개발용 가상환경, 생성 이미지 원본은 Git에서 제외한다. 게임 엔진·유료 AI API·추가 모델 학습은 사용하지 않는다.

Git에는 기획·개발 문서, 변환·추론·검증을 재현할 소스와 잠금 파일, 최종 보고서·모델 출처·라이선스를 보관한다. 실행 로그·프로파일·잠재값·실행 환경 및 용량 스냅샷은 로컬 산출물로 제외한다. 보고서의 로컬 산출물 링크는 이를 보관한 개발 PC에서만 열리며, 저장소 복제만으로 원본 실험 결과가 내려오지는 않는다.
