# GPU 공급자 재사용과 반복 생성 검증

수정·반복 검사 2026-10-03 / 중단 후 결과 확인·패키지 검증 2026-10-04. [이전 반복 검사](DRAWING_SOAK_REPORT.md)에서 발견한 핸들 증가의 생성 경로를 분리하고 수정했다.

## 원인 범위와 변경

모델 세션을 만들기 전, 기존 `SessionOptions.AppendExecutionProvider("DML", ...)`를 호출하고 옵션만 해제해도 초기 준비 이후 회당 핸들이 2개씩 남았다. ORT 1.22.0 소스에서 이 호출은 공급자 factory와 D3D12/DirectML 장치·명령 큐를 만든다. factory는 장치/큐를 참조하고, 모델 세션은 별도 실행 context를 만든다. [공급자 등록](https://github.com/microsoft/onnxruntime/blob/v1.22.0/onnxruntime/core/session/provider_registration.cc), [고정 버전 factory 구현](https://github.com/microsoft/onnxruntime/blob/v1.22.0/onnxruntime/core/providers/dml/dml_provider_factory.cc)

같은 공급자로 디코더 세션을 12회 생성/해제하면 초기 준비 이후 핸들이 480개로 일정했다. 공급자 해제 뒤에는 385개로 줄었다. **반복 증가를 공급자 생성/해제 경로까지 분리한 결과이며, OS·그래픽 드라이버·DirectML 내부의 정확한 핸들 소유자를 특정한 것은 아니다.** 외부 라이브러리의 결함을 확정하거나 임의의 핸들을 닫지 않았다.

- `src/AIrtist.Core/OrtSessions.cs`: GPU 공급자 옵션을 지연 생성해 프로세스당 하나 재사용한다. 모델 세션·가중치·실행 context는 기존처럼 매 작업에서 해제하며 동시에 모든 모델을 상주시킬 필요가 없다.
- 세션 생성과 프로파일 설정은 잠금으로 직렬화한다. 프로파일 경로를 먼저 설정한 뒤 활성화하고, 생성 후 비활성화해 다음 세션에 전파되지 않게 한다. 실제 AI 실행은 기존 공유 `SerializedDrawingAi`로 직렬화한다.
- 공급자 생성 도중 실패한 옵션은 해제하고 재시도할 수 있다. `ShutdownGpu`는 종료를 영구 표시하고 factory를 해제한다. 새 GPU 세션은 거절하지만 이미 생성된 세션의 native 참조는 유지된다.
- `src/AIrtist.App/App.xaml.cs`의 앱 종료와 검사 실행기의 `finally`에서 해제한다. `ProcessExit`에도 멱등한 정리를 등록했다.
- `src/AIrtist.Core/LocalGenerator.cs`: GPU 추론 중 취소는 진행 중인 native 호출이 끝난 뒤 확인하고 결과를 버린다. 남은 생성 단계는 실행하지 않는다. CPU 번역의 기존 취소 방식은 유지한다. native GPU 오류로 생성이 실패하면 모델 세션의 해제 후 공급자도 무효화해 다음 작업에서 새로 준비한다.
- 모델·생성 설정·고정 ORT 1.22.0/DirectML 1.15.4·통신 규칙 버전을 바꾸지 않았다. 최종 Core SHA256은 `C568D389B3CEFB85A31BD2AC9B5C4082AC1C433DA0F7006282C99F9703F79ABC`다.

모델을 사용하지 않는 GPU 장치/큐의 기본 자원은 앱 종료까지 유지된다. 측정 중 임의의 GC로 자원을 감추거나 생성마다 프로세스를 재시작하는 방식은 적용하지 않았다. 취소는 현재 native 호출과 해제를 기다리므로 즉시 중단을 보장하지 않으며 세션 생성 중에도 대기할 수 있다. GPU 장치 제거/드라이버 리셋·실제 OOM 후 재시작 없는 복구는 이번에 실측하지 않았다.

### 중간 후보에서 발견한 GPU 오류

공급자를 재사용하면서 기존의 `RunOptions.Terminate`로 GPU 연산을 강제 취소한 첫 후보는 19장·취소/시간 초과 각 2회 이후, 20번째 복구 생성에서 native 초기화 오류 `887A0006`으로 실패했다. `outputs/drawing-soak/20261003-fixed-30/`는 **실패 기록**이며 이 후보를 `testbuild`에 적용하지 않았다. 이 결과를 근거로 GPU 취소를 native 호출 사이에서 처리하도록 바꾸고 전체 30장과 복구 검사를 새로 실행했다. 강제 취소와 GPU 오류의 연관성을 확인한 것이며 드라이버 내부 원인을 확정한 것은 아니다.

## 원인 분리·수명 검사

`DrawingProviderProbe`와 `GpuLifetimeChecks`를 검사 프로젝트에 추가했다.

```powershell
dotnet build tests/AIrtist.Checks -c Release --no-restore
.\tests\AIrtist.Checks\bin\Release\net10.0-windows\AIrtist.Checks.exe provider-probe . outputs/drawing-provider/run-new
.\tests\AIrtist.Checks\bin\Release\net10.0-windows\AIrtist.Checks.exe resource-probe . outputs/drawing-resource/run-new
.\tests\AIrtist.Checks\bin\Release\net10.0-windows\AIrtist.Checks.exe gpu-lifetime . outputs/gpu-lifetime/run-new
```

| 검사 | 결과 |
|---|---|
| 공급자만 생성/해제 12회 | 최초 준비 이후 매번 +2 핸들 재현 |
| 새 공급자+디코더 12회 | 대부분 매번 +2 핸들 재현 |
| 공유 공급자+디코더 12회 | 초기 준비 후 480개 유지 |
| 수정 후 CPU/GPU 각 모델 세션 4회 | 텍스트 435→435→435→435, U-Net 437→437→437→438, 디코더 443→443→443→443 |
| 없는 모델 후 정상 생성 | 통과 |
| 동시 생성 요청의 서로 다른 프로파일 | 분리 확인; 비활성 세션에 프로파일 미전파 |
| 종료 2회·종료 후 새 생성 | 중복 종료 허용, 새 생성 거절 |
| factory 종료 당시 살아 있는 세션 | 실제 디코더 추론 완료 후 해제; 핸들 467→362 |
| 전체 Core 규칙 검사·WPF/검사 Release 빌드 | 통과, 빌드 경고/오류 0개 |

프로파일 검사에서 활성화/경로 설정 순서를 처음에 잘못 적용한 것을 발견해 고쳤고 재검사를 통과했다. 이 검사는 작은 디코더로 세션 수명을 확인하며 전체 그림 품질 검사와 구분한다. 원자료는 `outputs/drawing-provider/20261003-factory/`, `outputs/drawing-resource/20261003-shared-provider/`, `outputs/gpu-lifetime/20261003-shared-fixed/`다. 최종 취소 방식으로도 `outputs/gpu-lifetime/20261003-cooperative/`의 수명 검사를 통과했다.

## 최종 수정본의 실제 30장·복구 검사

`outputs/drawing-soak/20261003-cooperative-30/`의 종료 코드 0과 `status=passed`를 확인했다. 같은 프로세스에서 실제 30장과 취소/시간 초과 각 3회 후 복구를 통과했다. 10월 4일에는 중단 전 완료된 결과를 확인했으며 30장을 다시 생성하지 않았다.

| 항목 | 실측 결과 |
|---|---:|
| 전체 검사 | 615.73초, 약 10분 16초 |
| 첫 생성 / 이후 29장 | 19.90초 / 17.60~18.44초, 평균 17.87초 |
| 첫 → 마지막 완료 직후 핸들 | 496 → 509 |
| 1~10 / 11~20 / 21~30장 핸들 범위 | 496~516 / 500~514 / 496~510 |
| 작업 중 최대 working set / private bytes 표본 | 1,799,749,632 / 3,701,370,880바이트 |
| 첫 → 마지막 완료 직후 private bytes | 490,876,928 → 500,727,808바이트 |
| 컨트롤러 해제·2초 대기 / 별도 진단 GC 후 private bytes | 500,916,224 / 234,438,656바이트 |
| 진단 GC 후 핸들 | 509 |
| 생성 단계 3에서 취소 요청 → 정리 완료 | 0.127~0.136초 |
| 10초 제한 작업의 실제 종료 | 10.32~10.49초 |
| GPU 전체 사용량 최대 표본 | 4,539MiB, 다른 앱 포함 |
| 수정 전과 같은 입력·시드의 PNG SHA256 비교 | **30/30 일치** |

이전 같은 DLL 구성의 30장 검사는 핸들 401→599로 증가했다. 수정 후에는 관측 구간에서 그와 같은 누적 증가를 재현하지 않았다. 공급자 재사용 때문에 처음부터 장치/큐 기본 자원을 보유하므로 첫 핸들 수만으로 악화 여부를 판단하지 않는다. 종료 후 진단 GC 시점에도 공급자는 살아 있고, 검사 실행기 `finally`에서 마지막으로 해제한다. 이 결과는 무제한 실행의 누수 부재를 증명하지 않는다.

실행 환경은 Ryzen 5 5600X·약 48GiB RAM·GTX 1660 Ti 6GB·드라이버 591.86이다. 모델은 기존 Tiny-SD 혼합 INT8 저장/FP32 연산·TAESD·OPUS-MT 번역이며 512×512·25단계·guidance 7을 유지했다. 모델 manifest SHA256은 `9D3E2CDFCE5A501C6455ED1724A9EABCA3D2C3265F31124F5701380D3831379D`다. 생성 시간에는 모델 준비와 추론이 포함되며 순수 로딩 시간은 분리하지 않았다. 이전 검사와 실행 시각·다른 앱 부하가 달라 속도 개선율을 확정하지 않는다.

검사 apphost에서 로드한 ORT/DirectML은 검사 폴더 내부이며 배포 DLL과 해시가 같다. ORT 1.22.0은 `95366724919F4E95ECC60010912ED538AD9804B6683FBD0AAD389749102834B9`, DirectML 1.15.4는 `9C9E6D822561C6C41B90E6994B3E8857CF1D66DBFB1E0C4C799C7C89B4E92DA1`다. 검사 실행기의 .NET 10.0.3은 개발 PC 전역 런타임이므로 자체 포함 패키지 검사는 별도로 수행한다. 동일한 PNG이므로 이 수정이 번역 의미 보존·이미지 품질을 개선한 것은 아니다.

## 실행 패키지

최종 후보를 검증하고 `D:\AIrtist\testbuild\AIrtist.exe`로 교체했다. **943,404,694바이트 / 444개 파일**의 폴더 전체가 필요하다. 모델·토크나이저·.NET/WPF·ORT/DirectML·EOS·단어 목록·고지를 포함하며 1GB 목표까지 56,595,306바이트가 남는다. 기존 패키지 대비 1,024바이트 증가했고 검사 실행기와 배포본의 Core DLL 해시가 일치한다. 등록 파일 443개의 크기·SHA256, manifest 자체 포함 전체 파일 수, 추가 파일·재분석 지점 부재를 확인했다. 최종 package manifest SHA256은 `D7CE0226BDC0D6C25AAD6DAD0992C4BE38C084B68CA963F30A08E76A0919E31A`다.

| 최종 후보 검사 | 결과 |
|---|---|
| 전체 Core `unit` | 통과: 입력·그림그리기·취소/실패/오래된 결과·투표·전송·대기방·경기 규칙 |
| 패키지 `drawing-smoke` | 실제 3장, 번역 확인/수정·재생성·원문 변경 통과; UI 타이머 551회 |
| 패키지 `match-smoke` | 실제 AI 3명×2라운드·PNG 6장 1,860,750바이트; 라운드마다 1/2/0표·기록 일치·자기 표 거절·입력 초기화·경기 종료 통과 |
| 자체 포함 런타임 | 두 실제 AI 검사에서 .NET/ORT/DirectML을 모두 후보 내부에서 로드 |
| 화면 렌더 확인 | 작은 창의 그림그리기 결과와 마지막 라운드 기록·갤러리 표시 확인 |
| 패키지 `online-smoke` | 모의 3인·2라운드·6개 모의 PNG·별도 작업 스레드·설정 누락·오래된 명령 거절 통과 |
| 최종 경로 `self-check`·무결성 | `testbuild`에서 최초 화면·모델 확인 및 전체 등록 파일 일치 통과 |

그림그리기 첫 생성 20.88초, 이후 18.12/17.89초, 전체 검사 67.35초다. 경기 승인부터 생성·로컬 업로드까지 18.19~20.66초, 전체 검사 131.03초, UI 타이머 1,168회다. 최대 working set 표본은 그림그리기/경기 각각 1,718,296,576/1,825,980,416바이트, private bytes는 3,611,832,320/3,669,905,408바이트였다. 다른 앱을 포함한 GPU 전체 최대 표본은 각각 4,315/4,311MiB다. 표본 간 순간 최대치와 앱 전용 VRAM은 미측정이다.

실제 경기 검사는 한 PC의 메모리 내 연결이고 온라인 검사는 모의 AI/접속이다. 실제 EOS 인증과 인터넷 멀티플레이를 검증한 것으로 합쳐 설명하지 않는다. 최종 후보는 `.cache/gpu-fix-20261003/candidate-v2/`에서 검사한 뒤 폴더를 `testbuild`로 옮겼으므로 원자료에 기록된 후보 경로는 현재 존재하지 않는다. 검사·파일 해시·교체 기록은 `outputs/gpu-fix-20261003/`의 `drawing-v2/`, `match-v2/`, `online-v2/`, `final-self-check/`, `final-verification.json`, `promotion.json`에 보존했다.

## 중복 파일 정리와 변경 파일

최종 실행·무결성 확인 후 이전 `testbuild`와 이번 publish 복사본 **1,149,224,081바이트**를 정리했다. 앞서 제거한 불합격 후보와 publish **1,149,223,981바이트**를 합하면 이번 작업의 중복 파일 제거량은 **2,298,448,062바이트(약 2.30GB)**다. 삭제 전 정확한 프로젝트 내부 경로·재분석 지점 부재·Git 추적 부재와 이전 패키지의 전체 해시를 확인하고 파일 목록을 남겼다. 소스·모델·SDK·검사 원자료는 보존했다.

정리 직후 작업 폴더 파일 합계는 **3,750,169,466바이트(약 3.75GB)**다. 이후 문서·기록 추가로 소량 달라질 수 있다. 실행에 필요한 것은 `testbuild` 전체 약 943MB이고, 나머지는 개발용 모델 약 1.05GB·캐시 약 980MB·검사 자료 약 378MB·소스/검사 빌드/SDK 등이다. 실행·모델·캐시·진단 출력은 기존 Git 제외 규칙이 적용됨을 확인했다. 커밋·외부 게시를 수행하지 않았다.

제품 변경 파일은 `OrtSessions.cs`, `LocalGenerator.cs`, `App.xaml.cs`다. 검사 실행기 `Program.cs`와 `DrawingProviderProbe.cs`, `GpuLifetimeChecks.cs`를 통해 재현과 수명 검사를 보관한다. 이 보고서와 `AGENTS.md`, 통합 기획서, README, 실행·배포·반복 검사·정리 문서에 현재 상태를 반영했다. SDK·모델·패키지 의존성은 바꾸지 않았다.

## 미검증 범위

이 PC의 제한된 반복 검사로 모든 환경의 누수 부재나 최소 사양을 보장하지 않는다. 다음 독립 검증은 수 시간의 WPF 사용·별도 4GB VRAM/16GB RAM PC·실제 IME/DPI·깨끗한 Windows/네트워크 차단 최초 실행이다. EOS 설정 5개가 없어 실제 인증·다른 회선 2PC 이미지 송수신·3인 투표는 미검증이며 설정 준비 후 진행한다. GPU 오류/OOM 복구와 번역·이미지의 의미 보존 한계도 남아 있다.
