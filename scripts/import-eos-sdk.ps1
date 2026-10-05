param([Parameter(Mandatory = $true)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskLock = Get-Content -LiteralPath (Join-Path $taskRoot 'config/eos-sdk-lock.json') -Raw | ConvertFrom-Json
$taskArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ((Get-Item -LiteralPath $taskArchive).Length -ne $taskLock.archiveBytes -or
    (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskLock.archiveSha256) {
    throw '검증한 EOS C# 1.19.2.1 압축 파일과 다릅니다. 원본 버전·SHA256을 확인하세요.'
}
$taskDestination = [IO.Path]::GetFullPath((Join-Path $taskRoot 'sdks/eos'))
if (Test-Path -LiteralPath $taskDestination) { throw 'sdks/eos가 이미 있습니다. 기존 SDK를 덮어쓰지 않습니다.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::OpenRead($taskArchive)
try {
    $taskEntries = @($taskZip.Entries | Where-Object {
        $_.FullName.StartsWith('SDK/Source/') -or $_.FullName -eq $taskLock.nativeFile -or
        $_.FullName.StartsWith('ThirdPartyNotices/') -or $_.FullName.StartsWith('Samples/CSharp/Common/') -or $_.FullName -notmatch '/'
    })
    foreach ($taskEntry in $taskEntries) {
        $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskDestination $taskEntry.FullName))
        if (-not $taskTarget.StartsWith($taskDestination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw '압축 파일 경로 범위 오류'
        }
        if (-not $taskEntry.Name) { continue }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskTarget)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry, $taskTarget, $false)
    }
    Write-Output ('EOS Windows 개발용 구성 추출: ' + $taskEntries.Count + '개, ' + ($taskEntries | Measure-Object Length -Sum).Sum + '바이트')
} finally { $taskZip.Dispose() }
Write-Output '원본 압축 파일은 보존했습니다. SDK 설정·실제 접속은 docs/EOS_SETUP.md를 따르세요.'
