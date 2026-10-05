# EOS 로컬 테스트 패키지 구성 확인

확인일: 2026-10-01. 이 문서는 로컬 개발용 `testbuild`의 구성 근거이며 외부 제품 배포 완료 기록이 아니다.

2026-10-04 후속: 사용자가 계약 검토와 수락을 명시적으로 승인하여 AIrtist 조직의 EOS 개발자 계약 한국어 버전 4와 표준 서비스 부록을 수락했다. 포털 클라이언트 설정이 열렸고 실제 기기 인증·단일 참가자 로비 검사를 통과했다. 공개 배포용 최종 사용자 약관·깨끗한 PC 검증·다른 참가자 연결 검사는 남았다. 아래 10월 1일의 수락 미확인 문장은 당시 기록이다. [현재 설정·검사 범위](EOS_SETUP.md)

## 포함 구성

- 고정 C# SDK 1.19.2.1을 컴파일한 `AIrtist.EosSdk.dll`과 앱 연결부 `AIrtist.Eos.dll`.
- `EOSSDK-Win64-Shipping.dll`: 19,548,600바이트. `config/eos-sdk-lock.json`의 SHA256과 일치한다.
- 원본 `ThirdPartySoftwareNotice.txt`를 `licenses/EOS-ThirdPartySoftwareNotice.txt`로 복사했다. SDK 소스·예제·다른 플랫폼 바이너리와 로컬 접속 설정은 패키지에 넣지 않는다.

현재 코드는 Connect Device ID·Lobbies·P2P를 사용하고 소셜/계정 오버레이를 비활성화한다. 공식 문서는 별도 재배포 설치기·부트스트래퍼를 오버레이 기능과 연결한다. 이를 근거로 현재 로컬 검사 구성에는 해당 오버레이 설치기를 추가하지 않았다. 깨끗한 PC의 필수 의존성·실제 접속은 별도 검증 대상이다. [공식 재배포 설치기 안내](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/crossplay/redistributable-installer?lang=en-US)

## 배포 조건 확인과 남은 항목

공식 개발자 계약의 3절은 게임의 일부인 목적 코드 배포와 관련 최종 사용자 약관 조건을 두고 있다. 6.3.1·6.5절은 원래 고지와 SDK의 제삼자 라이선스 고지를 다룬다. 현재 테스트 패키지에는 컴파일된 실행 구성과 SDK 고지를 포함했으며, 공개 배포용 최종 사용자 약관과 계정 소유자의 계약 수락 상태는 확인하지 않았다. 계정 소유자를 대신한 계약 동의나 외부 게시를 수행하지 않았다. [Epic Online Services 개발자 계약](https://onlineservices.epicgames.com/services/terms/agreements)

외부 제품 배포 전에 최종 사용자 약관·서비스 설정·필수 의존성을 최종 배포 범위에 맞춰 확인한다. 현재 무료 이용 안내는 게임 및 관련 앱을 대상으로 한다. [공식 라이선스 안내](https://onlineservices.epicgames.com/licensing)
