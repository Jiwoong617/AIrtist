# 실제 온라인 화면 경로·종료 복구 검사

작업·검사: 2026-10-05.

## 완료 범위

WPF 온라인 창의 이벤트 처리와 실제 `OnlineSessionWorker`/EOS 연결부를 사용해 **기기 인증 → 독립 범위 방 검색 → 정원 2의 방 생성 → 서비스 명단 1명 확인 → 나가기 응답 → 같은 기기 재접속 → 나간 방 검색 제외 → 종료**를 검증했다. 전용 네트워크 스레드와 WPF Dispatcher를 실행하고 창 내용을 렌더링하는 진단이며 사람이 실제 버튼을 클릭한 수동 검사는 아니다.

한 PC·한 참가자 검사다. **다른 참가자의 방 검색/참가·속성 전파·P2P·실제 PNG 전송·온라인 투표는 미검증**이다. 새 진단에서 AI는 실행하지 않는다. 실제 게임의 검색에 섞이지 않도록 임의 진단 호환성 범위를 사용한다.

## 발견한 문제와 수정

첫 실제 검사에서 서비스 작업과 재접속까지 통과했지만 마지막 창 닫기에 WPF `InvalidOperationException`이 발생했다. 네트워크 작업 스레드가 이미 끝나 `DisconnectAsync()`가 즉시 완료되면 `Closing` 이벤트 내부에서 `Close()`를 다시 호출했기 때문이다. 주 창의 `StopAsync()` 종료 경로에도 같은 형태가 있었다.

두 창 모두 접속 정리 후 두 번째 `Close`를 Dispatcher에 예약하여 첫 `Closing`이 반환된 뒤 처리하도록 수정했다. 기존 모의 검사에 **이미 종료된 worker를 가진 온라인 창과 주 창 닫기**를 추가했다. 실제 진단도 worker 종료와 창의 `Closed` 이벤트까지 기다린 후에만 통과한다.

변경 파일:

- `OnlineRoomWindow.xaml.cs`, `MainWindow.xaml.cs`: 완료된 비동기 종료 후 중첩 창 닫기 방지.
- `OnlineLiveSmoke.cs`, `App.xaml.cs`, `OnlineWindows.Smoke.cs`: 명시적 `--online-live-smoke` 검사, 이벤트/버튼 상태·서비스 명단·같은 기기 재접속·정상 종료·화면 렌더.
- `OnlineSmoke.cs`: 두 창의 종료 경계 회귀 검사.
- `scripts/check-app.ps1`: `online-live-smoke`와 필수 `-EosConfigPath` 지원. 기존 `online-smoke`는 계속 모의 검사이며 자동으로 네트워크에 연결하지 않는다.
- `scripts/package-app.ps1`: 현재 검증 범위 안내 갱신.

## 결과와 패키지

| 검사 | 결과 |
|---|---|
| Release 빌드 | 경고 0 / 오류 0 |
| 수정 후 개발 EXE 실제 EOS | 인증·빈 범위 검색·생성/명단·나가기·동일 기기 재접속·worker/창 종료 통과 |
| 기존 `online-smoke` | 모의 3인 × 2라운드·PNG 6개·1/2/0표·실패/이전 명령 거절·추가 창 닫기 검사 통과 |
| 최종 `testbuild` 실제 EOS | 같은 단일 참가자 서비스 검사 통과, Dispatcher 104회 진행, 진단 구간 약 11.81초 |
| 최종 `testbuild` `self-check` | 모델 검증·그림그리기 초기 화면 통과 |

실행 파일은 `D:\AIrtist\testbuild\AIrtist.exe`이며 전체 폴더 **943,420,637바이트 / 444개 파일**이다. 이전 패키지의 전체 무결성을 확인한 뒤 변경된 `AIrtist.dll`과 안내문·패키지 manifest만 교체했다. 모델·추론 DLL·EOS 연결 구현은 바꾸지 않았다. Client Secret/로컬 설정은 패키지에 넣지 않았다. 실제 AI 생성은 이번 UI 종료 수정에서 반복하지 않았고 기존 10월 4일 검사 기록과 구분한다.

`package-manifest.json` SHA256:

```text
D4A6829250CC32C66081C7CA197B1E2E3325D4F52CB7C8373ADCFC7C4869319D
```

기록은 Git 제외 폴더 `outputs/online-live-20261005/`에 있다. `live/`는 최초 종료 실패, `live-fixed/`는 수정 후 성공, `mock-regression/`는 모의 회귀, `package-live/`와 `package-selfcheck/`는 최종 경로 검사다. `progress.json`은 진행 기록이며 최종 통과는 `status.json`과 프로세스 종료 코드로 판단한다. 로그에는 자격 증명·참가자 ID·전체 세션 스냅샷을 저장하지 않는다.

## 재실행

새 결과 폴더를 지정한다. 이 명령은 실제 서비스에 접속하고 임시 방 하나를 생성한 뒤 나간다.

```powershell
.\scripts\check-app.ps1 -PackageDirectory testbuild `
  -OutputDirectory outputs/online-live-new -Mode online-live-smoke `
  -EosConfigPath config/eos.local.json
```

두 PC 검증 준비와 단계별 기록 양식은 [여러 PC 검사 절차](MULTIPC_TEST_GUIDE.md)에 정리했다. 다른 PC·회선의 준비 여부는 아직 확인되지 않았고 해당 검사는 실행하지 않았다. 인증 만료/갱신·취소, 장시간 사용·저사양·IME/DPI·깨끗한 PC 역시 남아 있다.
