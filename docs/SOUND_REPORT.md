# AIrtist 사운드 제작·적용 기록 — 2026-10-18

## 반영 내용

포근한 그림 작업실을 기준으로 BGM 2곡과 효과음 7종을 제작하고 게임에 연결했다. 부드러운 피아노·마림바·현을 뜯는 음색과 작은 종이·나무 질감을 합성한다. 외부 음악 API, 음원 다운로드, 제3자 샘플을 사용하지 않으며 게임 실행에 Python이나 오디오 패키지를 추가하지 않는다. 라이트·다크 테마는 같은 음원을 사용한다.

| ID | 길이 | 재생 위치·구성 |
|---|---:|---|
| `bgm_menu` | 87.272744초 | 메인·방 생성/참가·대기방, 88 BPM·4박자·32마디 |
| `bgm_game` | 120초 | 그림그리기·멀티 작성/생성/투표/결과, 96 BPM·4박자·48마디 |
| `ui_click` | 0.08초 | 활성 버튼의 공통 나무 클릭음 |
| `round_start` | 0.8초 | 제시어 표시, 종이와 두 음 |
| `generation_complete` | 0.7초 | 본인 그림 첫 표시, 붓질 질감과 마림바 두 음 |
| `gallery_open` | 0.7초 | 투표 그림 공개, 종이와 가벼운 높은 음 |
| `deadline_tick` | 0.1초 | 본인의 미제출·미투표 마감 5·4·3·2·1초 |
| `round_result` | 1.5초 | 확정 결과, 따뜻한 세 음. 마지막 라운드도 동일 |
| `error_notice` | 0.35초 | 새로운 입력·생성·연결 오류, 내려가는 두 음 |

두 곡은 공통 선율과 악기를 사용한다. 인게임은 멜로디 빈도를 줄이고 작은 일정한 리듬을 늘렸다. 보컬·강한 저음·큰 고조·별도 승패 음악은 없다.

## 제작 파일

- 배포 음원·정보: [`assets/audio`](../assets/audio/README.md), WAV 9개와 manifest/안내.
- 재제작 소스: [`scripts/compose-audio.py`](../scripts/compose-audio.py). 개발용 Python + NumPy만 필요하다.
- 44.1kHz 스테레오 16-bit 원본 9개: `outputs/audio-production-20261018/masters/`, **37,309,476바이트**.
- 20초 메인 시안: `outputs/audio-production-20261018/preview-main-20s.wav`.
- 배포본: 22.05kHz 모노 16-bit PCM WAV, **9,327,668바이트**. 안내·manifest 포함 **9,333,267바이트**로 10MB 목표 이내다.
- `assets/audio/manifest.json`에 SHA-256, 샘플 수, 길이, BPM/마디, 반복 구간을 보관한다. BGM은 `[0, loopEnd)` 전체를 반복하며 잔향도 경계 너머에 이어 붙인다.
- 원본·시안은 Git 및 게임 배포에서 제외하고 로컬에 보존한다. 배포용 WAV와 제작 소스는 저장소 관리 대상이다.

## 재생 구현

`MenuWindow` 하나가 `GameAudio`를 소유하고 하위 화면에 공유한다. `AudioMixer`는 지속적인 PCM 타임라인에서 음악·효과음을 합산하고, `WaveAudioOutput`은 전용 스레드에서 Windows 내장 WinMM 장치로 전달한다. 새 NuGet·네이티브 라이브러리는 없다. 고정 PCM 메모리는 약 18.7MB이며 UI 스레드에서 파일을 읽거나 오디오 출력을 기다리지 않는다.

- 음악 변경만 0.8초 크로스페이드한다. 설정 열기·테마 변경·같은 곡 재요청·다음 라운드는 재시작하지 않는다. 음악을 바꿨다 돌아오면 보관한 위치에서 이어진다.
- `Button`의 실행 이벤트 하나를 공통 처리한다. 비활성 버튼·슬라이더 내부 반복 버튼·마우스 올리기·텍스트 입력은 클릭음을 내지 않는다. 빠른 클릭은 60ms 간격으로 제한한다. 화면 전환 중에도 대기 중인 클릭음을 보존하고, 종료 버튼은 클릭음이 출력되도록 180ms 뒤 닫는다.
- `ScreenAudioEvents`가 현재 라운드/본인 ID, 그림 첫 표시, 공개, 확정 결과, 오류 변경을 기억한다. 같은 화면 갱신과 반복 스냅샷으로 재생하지 않는다.
- 마감음은 본인이 작성 중이거나 아직 투표할 수 있을 때만 각 남은 초에 한 번 난다. 번역은 작성 제한 시간에 포함한다. 승인 요청 이후·AI 생성·전송·이미 투표한 상태는 제외한다. 지연 후 지나간 초를 몰아 재생하지 않는다.
- 생성 완료음은 200ms 보류한다. 그동안 공개가 오면 취소하고, 이미 시작한 경우에는 10ms로 줄이며 공개음을 우선한다. 결과음도 대기/진행 중인 마감음보다 우선한다.
- 효과음은 최대 3개 동시 재생하며 출력에 완만한 제한기를 적용한다. 오류음은 400ms 간격으로 제한한다. 로컬 생성 실패 후 방장의 실패 확인은 같은 오류로 묶는다.
- BGM/SFX 음량을 각각 적용한다. 0% 출력은 0이고, 음소거한 효과음은 나중에 몰아 재생하지 않는다. 장치 큐는 20ms × 4개이므로 음량 변경은 이미 큐에 들어간 최대 약 80ms 이후 모두 반영된다.
- 설정 슬라이더는 즉시 미리 적용하고 **적용**으로 저장한다. 적용하지 않고 닫으면 저장된 음량으로 돌아간다.
- 오디오 파일 누락·손상 또는 장치 열기 실패는 무음으로 처리하며 게임을 종료하지 않는다. 실행 중 오디오 장치를 바꾼 뒤의 자동 재연결은 구현하지 않았다.
- `DrawingSnapshot`·`RoundRuntimeSnapshot`에 로컬 실패 번호를 추가했다. 네트워크 메시지·경기 규칙·호환성 버전 `multiplayer-waiting-v8`은 변경하지 않았다.

WinMM 수명은 [waveOutOpen](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutopen), [waveOutReset](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutreset), [WAVEHDR](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/ns-mmeapi-wavehdr)의 규칙을 따른다. 종료 시 reset → unprepare → 버퍼 해제 → 장치 닫기를 수행한다.

## 검사 결과

| 검사 | 확인 내용 |
|---|---|
| 전체 Core unit | 기존 그림그리기·승인·전송·경기·투표·취소·실패 복구 통과 |
| Release 빌드 | 고정 패키지 복원과 publish 통과, 새 외부 의존성 없음 |
| `audio-smoke` | 음원 9개 해시·용량, 두 BGM 각각 3회 반복의 모든 출력 샘플 비교, 추가 무음/누락/중복 샘플 없음 |
| 믹서 | 0.8초 전환, 같은 곡 유지, 독립 0% 음소거, 효과음 우선순위·연속 클릭/겹침 제한 통과 |
| 화면 이벤트 | 작성 5회·투표 5회 마감음, 제출/투표 후 제외, 2인 동점·다음/마지막 라운드·실패/연결 오류 중복 방지 통과 |
| 설정·버튼 | 전역 버튼 클릭, 비활성/슬라이더 무음, 음량 미리 적용·저장·취소 복원, 설정 중 음악 유지 통과 |
| 실제 오디오 장치 | 장치 열기·버퍼 출력·reset/닫기 3회 통과 |
| 실제 싱글 | 한국어 내부 번역 포함 3장 생성, 완료음 3회, 설정/테마 변경·단일 창·음악 유지 통과 |
| 최종 EXE 로컬 3인 | 독립 앱·TCP·모의 AI 2라운드, 각 앱 시작/공개/결과 2회, 참가자 연결 종료 오류 1회, 음악 전환 2회 통과 |
| 배포 EXE 실제 로컬 2인 | 독립 앱·TCP·실제 Tiny-SD 1라운드·서로 다른 PNG 2장·1:1 동점, 각 앱 시작/생성/공개/결과 1회, 참가자 연결 종료 오류 1회 통과 |

검사 자료는 `outputs/audio-20261018/`에 있다. `audio-v2`, `package-audio`, `menu`, `package-local-3-v2`, `package-real-2`가 성공 기록이다. 첫 `package-local-3`은 방장 종료를 의도한 검사에서 검사 도구가 종료 상태를 실패로 취급하여 중단됐다. 이 대기 단계에 종료 상태 허용을 추가한 후 재검사를 통과했다. 마지막 클릭음 보존·종료 지연 보완 뒤 `package-audio-final`에서 오디오/설정/종료 검사를 다시 통과했다. 그 보완 뒤 AI 생성 검사를 반복하지는 않았다.

반복 검사는 오프라인으로 실제 믹서를 3주기 구동한 수치 검사다. 장치 출력 성공은 확인했지만, 스피커에서 세 번 반복 청취하거나 음색·집중 방해 정도를 사람이 승인했다는 뜻은 아니다. 음량 균형과 음악 취향은 동봉 시안 및 실제 게임에서 최종 청감 확인이 필요하다. 서로 다른 PC의 EOS 경기 검증과 오디오 장치별 지연·장시간 출력 검증은 이번 검사에 포함하지 않았다.

검사 명령:

```powershell
./scripts/build-app.ps1 -CheckRules
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-audio-check -Mode audio-smoke
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-menu-check -Mode menu-smoke
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-local-check -Players 3
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-real-check -Players 2 -RealAi
```

각 검사 출력은 아직 없는 새 폴더를 지정한다. AI 검사는 순차 실행한다.

## 배포

`testbuild/AIrtist.exe`를 포함한 폴더 전체가 **952,974,286바이트 / 455개 파일**이다. 1,000,000,000바이트 기준으로 **47,025,714바이트**가 남는다. 압축 설치 파일의 크기가 아니라 설치 후 파일 합계다.

기존 필수 파일의 무결성을 확인한 뒤 변경 파일과 음원을 반영했다. `package-manifest.json` 자체를 제외한 454개 파일의 길이·SHA-256을 기록하고 publish 파일과 대조했다. 모델 파일은 기존 것을 그대로 사용한다. 변경 전 소형 파일만 `.cache/testbuild-before-audio-20261018`, `.cache/testbuild-before-audio-check-20261018`, `.cache/testbuild-before-audio-click-20261018`에 보관했다.

최종 `AIrtist.dll` SHA-256: `85a72fc14ed5e188b181e49047e93e30d37e1d91f97888486f59f9377b75b24c`.

전체 측정은 `outputs/audio-20261018/package.json`에 있다.
