param([string]$ConfigPath = 'config/eos.local.json')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskConfig = if ([IO.Path]::IsPathRooted($ConfigPath)) { $ConfigPath } else { Join-Path $taskRoot $ConfigPath }

# Report field names and readiness only. Never echo the configuration or credentials.
if (-not (Test-Path -LiteralPath $taskConfig -PathType Leaf)) {
    Write-Output 'EOS 준비 전: config/eos.example.json을 config/eos.local.json으로 복사한 뒤 개발자 포털 설정을 입력하세요.'
    Write-Output '준비 절차: docs/EOS_SETUP.md'
    exit 2
}
try { $taskSettings = Get-Content -LiteralPath $taskConfig -Raw | ConvertFrom-Json }
catch { Write-Output 'EOS 설정 JSON을 읽을 수 없습니다. 파일 형식을 확인하세요.'; exit 2 }
$taskRequired = @('sdkDirectory', 'productId', 'sandboxId', 'deploymentId', 'clientId', 'clientSecret')
$taskMissing = @($taskRequired | Where-Object {
    $taskValue = $taskSettings.$_
    $taskValue -isnot [string] -or [string]::IsNullOrWhiteSpace($taskValue) -or $taskValue -ne $taskValue.Trim()
})
if ($taskMissing.Count -gt 0) {
    Write-Output ('EOS 설정 누락/형식 오류: ' + ($taskMissing -join ', '))
    exit 2
}
try {
    $taskSdk = if ([IO.Path]::IsPathRooted($taskSettings.sdkDirectory)) { $taskSettings.sdkDirectory } else { Join-Path $taskRoot $taskSettings.sdkDirectory }
    if (-not (Test-Path -LiteralPath $taskSdk -PathType Container)) {
        Write-Output 'EOS SDK 디렉터리가 없습니다. sdkDirectory를 확인하세요.'
        exit 2
    }
    $taskNames = @(Get-ChildItem -LiteralPath $taskSdk -File -Recurse | Select-Object -ExpandProperty Name)
    $taskNative = $taskNames -contains 'EOSSDK-Win64-Shipping.dll'
    $taskManaged = ($taskNames -contains 'ConnectInterface.cs' -and $taskNames -contains 'PlatformInterface.cs') -or ($taskNames -contains 'Epic.OnlineServices.dll')
} catch {
    Write-Output 'EOS SDK 파일 목록을 읽을 수 없습니다. 경로와 읽기 권한을 확인하세요.'
    exit 2
}
if (-not $taskNative -or -not $taskManaged) {
    Write-Output "EOS SDK 구성 부족: Win64 native=$taskNative, C# wrapper=$taskManaged"
    exit 2
}
Write-Output 'EOS 로컬 준비 항목 확인 완료. 실제 자격 증명 유효성·SDK 버전·서비스 권한·온라인 접속은 아직 검사하지 않았습니다.'
Write-Output '다음: SDK 버전·해시 고정 → Connect 기기 ID 인증 → Lobby/P2P 연결·실측.'
