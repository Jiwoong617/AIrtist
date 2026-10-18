# AIrtist 오디오

이 게임을 위해 작성한 선율과 합성 악기·종이/나무 질감이다. 외부 녹음, 샘플, 사운드폰트, 음악 모델을 사용하지 않았다. 게임과 함께 수정·배포할 수 있으며 별도의 제3자 음원 크레딧 의무는 없다.

- 제작 소스: `scripts/compose-audio.py` (개발 환경 Python + NumPy 필요, 게임 실행에는 불필요).
- 배포: 22,050 Hz / 모노 / PCM 16-bit WAV 9개, 합계 **9,327,668바이트**.
- 원본: `outputs/audio-production-20261018/masters/`, 44,100 Hz / 스테레오 / PCM 16-bit. Git·배포 제외, 소스로 재제작 가능.
- 메인 시안: 같은 출력 폴더의 `preview-main-20s.wav`.
- `manifest.json`에 각 파일의 SHA-256·샘플 수·길이·반복 구간을 기록했다. 반복 종료 샘플은 exclusive다.
- 메인: 88 BPM / 4박자 / 32마디 / 87.272744초. 경기: 96 BPM / 4박자 / 48마디 / 120초.
- 두 곡은 처음부터 끝까지 반복한다. 잔향은 반복 경계를 넘어 순환 배치했으며 추가 무음을 삽입하지 않는다.
- 효과음: `ui_click`, `round_start`, `generation_complete`, `gallery_open`, `deadline_tick`, `round_result`, `error_notice`.

재제작: 프로젝트 루트에서 `python scripts/compose-audio.py`. 고정된 난수 시드와 편곡을 사용한다. 산출물 해시는 생성 시 다시 기록한다.

구현·검사와 청감 검수 범위는 `docs/SOUND_REPORT.md`를 참고한다.
