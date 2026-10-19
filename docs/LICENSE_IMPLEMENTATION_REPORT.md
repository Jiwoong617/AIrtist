# 모델 라이선스 안내 적용

작업일: 2026-10-19. 사전 조사: [모델 라이선스 점검](MODEL_LICENSE_REVIEW.md).

## 적용 내용

사용자가 선택한 방식대로 모든 화면 우측 상단 **설정 왼쪽에 라이선스 버튼**을 추가했다. 기존 창 안의 읽기 화면으로 열리며, 라이트·다크 테마를 따른다. 문서는 설치 폴더에서 읽으므로 인터넷이나 모델 실행이 필요하지 않다. 닫기·Esc로 돌아가고, 설정과 라이선스를 서로 전환할 수 있다. 긴 문서는 스크롤하고 내용을 선택·복사할 수 있다.

메인·그림그리기·방 생성/참가·대기방·경기·최종 결과에서 같은 버튼을 사용한다. 라이선스를 읽는 동안 생성 작업·경기 타이머·BGM은 계속 진행한다. 첫 실행 동의 팝업은 추가하지 않았다.

화면에서 제공하는 문서 10종:

1. 모델 이용 조건: Tiny-SD 제5항과 Attachment A의 사용 제한을 조건에 포함하고 한국어 안내 제공.
2. 모델 출처: 제작자, 저장소, 고정 리비전, 원문 링크 및 파일 연결.
3. 파일별 변경 내역: 최종 배포 파일명별 ONNX 변환·양자화·FP16 저장 등의 변경 표시.
4. Tiny-SD CreativeML Open RAIL-M 전문.
5. TAESD MIT 전문.
6. OPUS-MT 라이선스 안내: 두 공식 표기와 제작자·출처·변경 사실.
7. OPUS-MT 사용 버전의 원본 모델 카드.
8. Apache License 2.0 전문.
9. CC BY 4.0 전문.
10. CLIP MIT 전문.

OPUS-MT는 사용 리비전의 Apache-2.0 표기와 제작진 일반 안내의 CC BY 4.0 표기를 함께 보존했다. 어느 하나로 확정하거나 선택 가능한 이중 라이선스로 선언하지 않는다. 독립된 번역 모델에 Tiny-SD의 사용 제한을 확대 적용하지 않는다. 모델 제작자에게 문의하거나 알림을 보내지는 않았다.

## 소스와 패키징

- `data/licenses/`: 라이선스·고지 원본을 Git에 보관한다. 기존 Tiny-SD·TAESD·CLIP·Apache 사본과 OPUS 모델 카드는 보존했다.
- `data/licenses/CC-BY-4.0.txt`: [공식 텍스트](https://creativecommons.org/licenses/by/4.0/legalcode.txt)를 그대로 동봉했다. SHA-256 `9ba9550ad48438d0836ddab3da480b3b69ffa0aac7b7878b5a0039e7ab429411`.
- `data/MODEL_CHANGES.txt`: 모델별 요약을 최종 파일명별 변경 고지로 보강했다.
- `scripts/prepare-runtime-assets.ps1`: 원본 고지를 `models/app-stage3/licenses/`에 반영하고 manifest를 만든다. 일반 빌드가 모델 파일 변경을 자동으로 재서명하지 않는 기존 원칙은 유지한다.
- `data/runtime-assets.json`: 이용 조건·출처·OPUS 고지·CC 전문 4개를 필수 파일로 추가했다. 모델 manifest의 필수 파일은 32개다.
- `scripts/package-app.ps1`: 고지와 모델 manifest를 패키지에 포함하고 `README.txt`에 라이선스 화면·Tiny-SD 조건·OPUS 표기 차이를 안내한다.
- `MenuWindow.Licenses.cs`와 XAML: 고정 문서 목록만 열며, 문서 누락·읽기 실패를 안내한다.

모델 가중치와 토크나이저는 변경하지 않았다. 이번 재게시에서 기존 `data/words.json`과 `audio/README.md`의 줄바꿈도 소스의 Windows 줄바꿈으로 반영됐으며 내용은 동일하다. 저장소 루트의 게임 소개 `README.md`는 그대로다.

규칙 식별자는 `multiplayer-results-v9`를 유지한다. 고지 파일 변경으로 자산 manifest 해시가 바뀌므로 함께 플레이할 참가자는 모두 최신 패키지를 사용해야 한다.

## 확인 결과

| 검사 | 결과 |
|---|---|
| Release 빌드·전체 Core unit | 통과. 기존 투표·제출·이미지 전송·최종 결과·취소/실패 검사 포함 |
| 최종 EXE 라이선스 | 10종 원문 로드, 긴 문서 끝 스크롤, 내용 선택, 누락 문서 감지, 닫기·Esc, 설정 상호 전환 통과 |
| 화면·상태 유지 | 라이트/다크, 단일 창, 설정 위치, 설정 미저장 음량 복원, 생성 중 열기, BGM 재시작 없음 확인 |
| 최종 EXE 실제 그림그리기 | Tiny-SD 3장, 한국어 내부 번역, 반복 생성, 입력·파라미터 변경 통과 |
| 최종 EXE 로컬 독립 앱 3개 | 모의 AI 2라운드, PNG 전송·0/1/2표, 누적 0/2/4표, 최종 결과 동기화·방장 선퇴장·메인 복귀 통과 |
| 로컬 경기의 라이선스 | 대기방·작성·투표·최종 결과에서 열기/닫기 통과. 열어 둔 동안 작성 시간이 감소함을 검사 |
| 배포 무결성 | 패키지 458개 항목의 길이·SHA-256 검증, manifest 포함 459개 파일, 생성·번역 모델 파일 변경 없음 확인 |

처음 UI 검사에서 포커스 테두리 변화에 따른 버튼 좌상단 이동을 고정 위치 위반으로 오인했다. 기존 설정 검사가 사용하는 우측 끝·세로 중심 기준으로 검사만 수정했으며, 재게시한 최종 EXE에서 모두 통과했다. 라이트·다크와 투표 중 화면 캡처도 확인했다.

검사 자료: `outputs/licenses-20261019/build.log`, `build-v2.log`, `menu-v2/menu-smoke.json`, `local-3-v2/status.json`, 각 플레이어 `status.json`, `testbuild.json`, `final-verification.json`. 다른 PC의 실제 EOS 경기·깨끗한 PC·추가 DPI 환경 검사는 이번 범위에 포함하지 않았다.

재검사 명령(출력 폴더는 새 경로 사용):

```powershell
./scripts/prepare-runtime-assets.ps1
./scripts/build-app.ps1 -CheckRules
./scripts/check-app.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-license-menu -Mode menu-smoke
./scripts/check-local.ps1 -PackageDirectory testbuild -OutputDirectory outputs/new-license-local -Players 3
```

## 최신 testbuild

`testbuild/AIrtist.exe`. 폴더 전체 **953,049,536바이트 / 459개 파일**이며, 1,000,000,000바이트 기준 **46,950,464바이트 여유**다. 기존 패키지보다 45,073바이트 증가했다.

`AIrtist.dll` SHA-256: `852a218415a962c6ae2e3dc0ae7cd1b14048ada20a82f1381b4b83be10cc8558`.

변경 파일만 `.cache/testbuild-before-licenses-20261019`와 `-v2`에 백업했다. 모델 전체는 중복 백업하지 않았다. 최종 검증 후 이번 작업의 임시 패키지 복사본 2개(1,906,099,072바이트)를 제거하고 `testbuild`와 검사 자료는 보존했다. Git 커밋이나 GitHub 업로드는 수행하지 않았다.

같은 날 후속 [폴더 정리](WORKSPACE_CLEANUP_20261019.md)에서 위 백업과 완료된 일회성 패키지 갱신 스크립트도 제거했다. 최신 `testbuild`와 검사 결과는 보존했다.

라이선스 안내와 동봉 작업을 완료한 것이며, OPUS-MT 공식 자료 간 표기 차이가 해소되거나 계약의 법적 효력이 검증됐다는 의미는 아니다.
