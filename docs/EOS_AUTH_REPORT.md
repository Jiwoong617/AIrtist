# EOS SDK와 기기 인증 연결 — 2026-09-29

2026-09-30 후속으로 P2P 어댑터와 SDK 경계 검사를 추가했다. 아래는 인증 준비 당시 기록이며 [후속 전송 구현·검증 범위](EOS_P2P_REPORT.md)를 함께 따른다. 실제 인증·온라인 접속은 여전히 미검증이다.

## 이번 결과

공식 C# SDK **1.19.2.1 / native 1.19.2.1-58105819**을 확보했다. Windows x64 DLL의 Authenticode 서명이 `Valid`, 서명자가 Epic Games Inc.임을 확인했다. SDK 버전·원본 URL·SHA256·고지 경로는 `config/eos-sdk-lock.json`에 기록했다. 버전 번호만으로 모든 EOS 서비스의 동작을 검증한 것은 아니다.

별도 `AIrtist.EosSdk`가 외부 SDK 소스를 컴파일하고 `AIrtist.Eos`가 SDK 초기화·기기 인증을 담당한다. 경기 Core와 WPF 앱은 아직 이 모듈을 참조하지 않는다. 기존 `testbuild`도 갱신하지 않았다.

## 구현

- `EosLibrary`: Windows x64·native/managed 버전 확인, 프로세스당 한 번 초기화, 같은 스레드에서 종료. 활성 인증 세션이 있으면 SDK 종료를 거절한다. 인증 세션 교체 동안에는 같은 library를 유지한다.
- `EosDeviceSession`: Platform 생성·Tick, Connect Device ID 생성/재사용 → Login → 최초 InvalidUser의 CreateUser. 인증된 Product User ID만 결과로 반환한다. 만료 알림의 재로그인, ID 변경/로그인 상실 실패, 임시 30초 제한과 늦은 콜백 무시, 알림 해제·Platform Release를 구현했다.
- 고정한 C# 래퍼는 완료 콜백이 와야 참조를 지우므로, Platform 해제 후에는 해당 세션 소유의 미완료 콜백/ClientData 참조도 정리한다. `CallbackLifetime.cs`는 내려받은 SDK 원본과 별도인 작은 partial 확장이며 SDK 교체 시 함께 검토한다. 다른 소유자의 참조를 유지하고 같은 소유자 정리를 반복해도 안전한지 managed 참조 카운트로 검사했다. 실제 네트워크 작업 도중 취소/해제는 자격 증명 준비 후 검사한다.
- overlay·social overlay·RTC·외부 플랫폼·스토리지 기능은 사용하지 않는다. 표시 이름은 사용자명이나 PC 이름을 자동 수집하지 않고 호출자가 제공하며 UTF-8 32바이트 이하로 제한한다.
- `EosSettings`: 16KiB 이하 로컬 설정 파일을 읽는다. 필수 문자열·공백·제어문자·알 수 없는 필드를 검사하고 오류에 JSON 원문이나 자격 증명을 포함하지 않는다. 검사 도구는 SDK 원시 로그, Client Secret, Product User ID를 출력하지 않는다.
- `AIrtist.EosChecks`: 자격 증명 없는 native 검사, 설정 검사, 별도의 명시적 `login` 실행 모드. 콘솔 소유 스레드에서 20ms마다 Tick하며 취소/시간 초과 시 정리한다. WPF UI 작업 루프는 후속 연결 대상이다.

SDK API의 근거는 내려받은 `PlatformInterface.cs`, `ConnectInterface.cs`, 각 옵션/콜백과 공식 [Connect 안내](https://dev.epicgames.com/docs/epic-online-services/eos-fundamentals/connect-interface)다. SDK의 `Shutdown` 뒤에는 재초기화를 포함한 SDK 호출이 금지된다. 최초 검사에서 재초기화 가정을 발견해 수명을 분리했으며 최종 검사에서는 재초기화를 managed 경계에서 거절했다.

## 검증 결과

| 검사 | 결과 |
|---|---|
| EOS 모듈·검사 도구 Release 빌드 | 경고 0 / 오류 0 |
| 실제 Win64 DLL 초기화·버전 조회·종료 | 통과 |
| 중복 SDK 소유권·다른 스레드 종료·Shutdown 이후 재초기화 거절, 소유자별 managed 콜백 참조 정리 | 통과 |
| 설정 누락·잘못된 타입·공백/제어문자·알 수 없는 키·깨진 JSON·크기 상한 | 통과, 테스트용 비밀값이 예외 문자열에 포함되지 않음 |
| 빈 로컬 설정으로 login 모드 실행 | ProductId 누락을 SDK 초기화 전에 거절, 예상 종료 코드 1 |
| SDK 추출 재현 | 1,093개 파일 각각 SHA256 일치, 기존 목적지 덮어쓰기 거절 |
| 기존 WPF 앱 Release 빌드 | 경고 0 / 오류 0 |

이 검사는 개발 PC에서 실행했다. 실제 Product/Sandbox/Deployment/Client 값이 없어 **온라인 기기 ID 로그인, 최초 사용자 생성, 인증 갱신, 네트워크 실패·취소 중 SDK 콜백/메모리 정리는 아직 실측하지 않았다.** 인증 코드의 컴파일 성공을 인증 성공으로 취급하지 않는다. Core/AI 변경이 없어 이번에는 기존 전체 경기·실제 이미지 생성 검사를 반복하지 않았다.

## 용량·재현

- 다운로드한 전체 플랫폼 압축: **587,320,398바이트**. 검증·필요 구성 추출 후 작업용 압축 파일을 삭제했고 부재를 확인했다. 추출 재현 검사용 SDK 복제본도 정리했다.
- 유지하는 `sdks/eos`: **23,203,168바이트 / 1,093개 파일**. C# 소스, Win64 DLL, Common 참조 예제와 ThirdPartyNotices다. 다른 OS·ARM64 바이너리와 SDK 도구는 추출하지 않았다.
- Win64 native DLL: **19,548,600바이트**. 빌드 산출물의 managed 래퍼·.NET·게임 자산까지 포함한 새 전체 패키지 용량은 아직 측정하지 않았다. 개발 빌드의 `bin/obj`는 위 SDK 폴더 수치와 별도다.
- SDK와 로컬 자격 증명, 검사 출력, EXE/DLL은 기존 `.gitignore`로 제외한다. 원본 다운로드와 작은 구성 추출은 [준비 절차](EOS_SETUP.md)를 따른다.
- SDK의 ThirdPartyNotices는 검사 출력에도 복사한다. 제품 재배포 구성요소·고지와 [개발자 계약](https://onlineservices.epicgames.com/services/terms/agreements) 확인은 실제 패키징 전 남은 작업이다. 계정 소유자 대신 계약에 동의하거나 포털 프로젝트/키를 생성하지 않았다.

## 다음 작업

1. 로컬 `config/eos.local.json`에 개발자 포털의 제품 설정 입력 후 `login` 검사. 아직 템플릿의 빈 값이다.
2. 실제 인증 성공/만료/연결 상실/취소·재시도와 SDK 자원 정리를 확인한다. 장시간 갱신과 중도 취소는 단기 로그인 검사만으로 검증되지 않는다.
3. 로비 생성·검색/참가·인증 명단과 P2P 전송을 `MultiplayerLobbySession`에 연결하고 온라인 화면을 붙인다.
4. 다른 회선의 2대 PC 이미지 송수신과 3명 투표/결과 일치, 전체 배포 용량을 검증한다.
