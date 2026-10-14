param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$ModelDirectory,
    [switch]$RealAi,
    [switch]$OnlineHost
)
$ErrorActionPreference='Stop'
$packagePath=(Resolve-Path -LiteralPath $PackageDirectory).Path
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new output directory' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$processes=@()
try {
    for ($index=0; $index -lt 3; $index++) {
        $arguments=@('--local-process-smoke', ('"'+$outputPath+'"'), $index)
        if ($ModelDirectory) { $arguments+=@('--models', ('"'+(Resolve-Path -LiteralPath $ModelDirectory).Path+'"')) }
        if ($RealAi) { $arguments+='--real-ai' }
        if ($OnlineHost) { $arguments+='--online-host' }
        $processes+=Start-Process -FilePath (Join-Path $packagePath 'AIrtist.exe') -ArgumentList $arguments -WorkingDirectory $packagePath -WindowStyle Hidden -PassThru
    }
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while (@($processes | Where-Object { -not $_.HasExited }).Count -gt 0) {
        if ($clock.Elapsed.TotalSeconds -gt 420) { throw 'Local diagnostic timeout' }
        Start-Sleep -Milliseconds 300
    }
    $reports=@(0..2 | ForEach-Object { Get-Content -LiteralPath (Join-Path $outputPath "player-$_/status.json") -Raw | ConvertFrom-Json })
    if (@($reports | Where-Object status -ne 'passed').Count -ne 0) { throw 'Local diagnostic failed; see player status files' }
    if (@($reports.processId | Sort-Object -Unique).Count -ne 3 -or @($reports.id | Sort-Object -Unique).Count -ne 3) { throw 'Independent processes/players not verified' }
    $expected=$reports[0].galleryHashes | ConvertTo-Json -Depth 6 -Compress
    foreach ($report in $reports) { if (($report.galleryHashes | ConvertTo-Json -Depth 6 -Compress) -ne $expected) { throw 'PNG hashes differ between processes' } }
    $summary=[ordered]@{status='passed';processes=3;players=3;rounds=$reports[0].rounds;realAi=[bool]$RealAi;tcpLoopback=$true;eos=[bool]$OnlineHost;identicalGalleryHashes=$true;hostLeave=$true}
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'status.json') -Encoding utf8
    $summary | ConvertTo-Json
} finally {
    foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id }; $process.Dispose() }
}
