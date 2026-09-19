# 파일 정리와 실행 패키지

2026-09-15 시작한 정리·패키징 작업을 2026-09-20 재개해 검증을 완료했다. 소스·설정·잠금 파일·Git 이력과 검증 보고서를 보존했다. Git 커밋은 수행하지 않았으며, 아래 메시지는 현재 미스테이징 변경을 기준으로 작성했다.

## 실행

`D:\AIrtist\testbuild\AIrtist.exe`를 실행한다. **testbuild 폴더 전체**에 모델·토크나이저·벡터·.NET/WPF·ONNX Runtime·DirectML·고지가 포함된다. EXE만 복사하면 실행할 수 없다. Python이나 모델의 추가 다운로드는 필요하지 않다.

현재 크기는 **955,637,367바이트(955.64MB), 442개 파일**, 1,000,000,000바이트 기준 여유는 **44,362,633바이트**다. 압축 설치 파일이 아닌 실행 폴더의 파일 크기 합이다. [크기 기록](../outputs/cleanup/testbuild-size.json)

현재 소스 Release 빌드(경고·오류 0), 규칙 검사, 패키지 442개 파일 목록·해시, 빌드된 앱/Core DLL과 패키지 DLL 일치를 확인했다. `testbuild` EXE로 한국어 실제 생성·건너뛰기·영어 실제 생성·3라운드 최종 결과·새 경기 초기화를 통과했다. 진단 약 49.99초 동안 UI 타이머 418회, 합계 159.70/평균 53.23점으로 기록됐으며 .NET·ORT·DirectML은 모두 testbuild에서 로드했다. [경기 진단](../outputs/cleanup/testbuild-smoke-20260920/match-smoke.json), [실행·모듈 기록](../outputs/cleanup/testbuild-smoke-20260920/process.json)

번역 의미 보존과 채점 정확도는 개선 중이다. 이 정리 작업에서 모델 품질을 개선한 것은 아니며, 멀티플레이·4GB VRAM/16GB RAM·깨끗한 PC 검증은 아직 남았다.

## 정리한 파일

정리 전 전체 파일 합계 **18,955,171,866바이트(18.96GB)**에서 새 testbuild를 포함해 **약 3.44GB**가 남았다. 순 감소는 **약 15.51GB(약 82%)**다. 문서·진단 기록을 추가할 때 소량 변할 수 있으며 디스크 할당량과 구분한다. [전후 크기 기록](../outputs/cleanup/size-summary.json)

| 삭제한 범위 | 정리 전 크기 | 이유 |
|---|---:|---|
| `.venv-tiny-sd` | 7.13GB | 앱 실행·C# 빌드에 쓰지 않는 Python/PyTorch/CUDA 실험 환경 |
| `dist/AIrtist-preview*` 4개 | 3.79GB | 중복 배포본을 testbuild 하나로 교체 |
| Tiny-SD 원본·변환 중간물, 기존 TAESD 원본, MobileCLIP, 미사용 번역·채점 ONNX | 약 4.60GB | 최종 실행 모델과 중복되거나 탈락한 후보 |
| Python 캐시·이전 publish·bin/obj·루트의 중복 ORT 프로파일 | 약 0.96GB | 다시 생성 가능한 산출물 |

정리 전 31개 대상의 절대 경로가 D:\AIrtist 안인지, Git 추적 파일이 없는지, 재분석 지점이 없는지 확인한 뒤 PowerShell의 `Remove-Item -LiteralPath`로 제거했다. 검증용 재빌드 후 생긴 bin/obj/publish도 제거해 중복 산출물을 남기지 않았다. [최초 삭제 목록](../outputs/cleanup/deleted.json), [재빌드 후 삭제 목록](../outputs/cleanup/postbuild-deleted.json)

## 보존한 파일

| 범위 | 대략적 크기 | 용도 |
|---|---:|---|
| `testbuild` | 955.64MB | 사용자가 실행·복사할 독립 패키지 |
| `models/app-stage3` | 772MB | C# 검사·재패키징에 필요한 최종 모델·manifest·라이선스 |
| OPUS-MT와 TinyCLIP 원본 | 412MB | 다음 번역·채점 개선에서 사용할 원본과 설정 |
| `.cache/nuget` | 980MB | .NET·WPF·ORT 패키지 복원·빌드를 위한 캐시 |
| `outputs` | 321MB | 참조 이미지·검증 수치·프로파일·출처·정리 기록 |
| 소스·문서·Git | 소량 | 실제 개발 결과와 이력 |

앱 실행만 하려면 testbuild만 필요하다. 나머지는 개발·재현·품질 개선용이며 모두 배포 파일에 포함하는 것이 아니다. 정리로 Python 변환 환경과 Tiny-SD 실험 원본은 없어졌으므로 해당 과거 실험을 다시 실행하려면 재설치·재다운로드가 필요하다. 버전 고정 요구사항·준비 스크립트와 삭제 전 Python 패키지 목록은 보존했다.

## 이후 빌드

준비된 모델이 있는 현재 PC에서 Python 없이 다음 명령으로 새 폴더에 패키징한다.

```powershell
.\scripts\build-app.ps1 -CheckRules -PackageDirectory testbuild-new
```

기존 폴더는 덮어쓰지 않는다. `scripts/package-app.ps1`이 모델 manifest의 크기·해시를 검사하고 필요한 파일과 고지만 복사한다. 새 모델을 변환한 경우에는 개발용 Python 도구로 manifest·라이선스도 다시 준비한다. 원본·변환 환경 재현은 [개발 안내](APP_DEVELOPMENT.md)를 따른다.

`testbuild/`, 모델, 캐시, 실행 로그는 `.gitignore`에서 제외한다. 재현에 쓰는 검사 소스와 빌드 스크립트는 보관한다.

## 커밋 메시지

```text
[Feat] 로컬 AI 이미지 생성과 오프라인 경기 구현

- C#·WPF 앱에 한국어 번역, Tiny-SD 생성, TinyCLIP 평가 연동
- 작성 마감, 생성 1회 제한, 여러 라운드와 최종 결과 구현
- Python 없이 자체 포함 testbuild를 만드는 PowerShell 패키징 추가
- 빌드 산출물 Git 제외와 중복 파일 정리·재현 방법 문서화
- 반복 생성·취소·시간 초과 복구·경기 완주 검증 및 품질 한계 기록
```
