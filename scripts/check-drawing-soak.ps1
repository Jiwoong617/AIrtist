param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(10,300)][int]$Images = 30,
    [ValidateRange(1,240)][int]$MaxMinutes = 30
)
$ErrorActionPreference = 'Stop'
if ($Images % 10 -ne 0) { throw 'Images must be a multiple of 10' }
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checksPath = Join-Path $workspacePath 'tests/AIrtist.Checks/bin/Release/net10.0-windows/AIrtist.Checks.exe'
if (-not (Test-Path -LiteralPath $checksPath -PathType Leaf)) { throw 'Build AIrtist.Checks in Release first' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new diagnostic output directory' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$gpuCommand = Get-Command nvidia-smi -ErrorAction SilentlyContinue
$gpuInfo = if ($gpuCommand) { & $gpuCommand.Source --query-gpu=name,driver_version,memory.total --format=csv,noheader }
$samples = [Collections.Generic.List[object]]::new()
$modules = @{}
$timer = [Diagnostics.Stopwatch]::StartNew()
$stdoutPath = Join-Path $outputPath 'stdout.log'
# Use the apphost so Windows resolves DirectML beside the executable, as in the app.
$arguments = @('soak', ('"' + $workspacePath + '"'), $Images, ('"' + $outputPath + '"'))
$runProcess = Start-Process -FilePath $checksPath -ArgumentList $arguments -WorkingDirectory $workspacePath -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError (Join-Path $outputPath 'stderr.log')
$exitCode = $null
$lastLine = ''
try {
    while (-not $runProcess.HasExited) {
        $gpuMemory = if ($gpuCommand) { & $gpuCommand.Source --query-gpu=memory.used --format=csv,noheader,nounits }
        $samples.Add([PSCustomObject]@{ seconds=$timer.Elapsed.TotalSeconds; wholeGpuUsedMiB=$gpuMemory })
        try {
            foreach ($module in $runProcess.Modules) {
                if ($module.ModuleName -in @('coreclr.dll', 'clrjit.dll', 'onnxruntime.dll', 'DirectML.dll')) {
                    # Keep every full path; a later module must not hide an earlier one.
                    $modules[$module.FileName] = [PSCustomObject]@{ name=$module.ModuleName; path=$module.FileName; version=$module.FileVersionInfo.FileVersion }
                }
            }
        } catch [System.ComponentModel.Win32Exception] { }
        $line = Get-Content -LiteralPath $stdoutPath -Tail 1 -ErrorAction SilentlyContinue
        if ($line -and $line -ne $lastLine) { Write-Output $line; $lastLine = $line }
        if ($timer.Elapsed.TotalMinutes -gt $MaxMinutes) { throw 'Soak diagnostic exceeded MaxMinutes' }
        Start-Sleep -Seconds 2
        $runProcess.Refresh()
    }
    $runProcess.WaitForExit()
    $exitCode = $runProcess.ExitCode
    if ($exitCode -ne 0) { throw "Soak diagnostic failed with exit $exitCode; see stderr.log and results.json" }
    $result = Get-Content -LiteralPath (Join-Path $outputPath 'results.json') -Raw | ConvertFrom-Json
    if ($result.status -ne 'passed' -or $result.completed -ne $Images) { throw 'Soak diagnostic did not finish all images' }
    foreach ($name in @('coreclr.dll', 'clrjit.dll', 'onnxruntime.dll', 'DirectML.dll')) {
        if (-not @($modules.Values | Where-Object name -eq $name).Count) { throw "Runtime module was not observed: $name" }
    }
    $checksDirectory = Split-Path -Parent $checksPath
    foreach ($module in @($modules.Values | Where-Object name -in @('onnxruntime.dll', 'DirectML.dll'))) {
        if (-not $module.path.StartsWith($checksDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "AI runtime loaded outside the checks directory: $($module.path)"
        }
        $module | Add-Member -NotePropertyName sha256 -NotePropertyValue (Get-FileHash -LiteralPath $module.path -Algorithm SHA256).Hash
    }
    Write-Output "PASS: $Images images; $($result.cancelled) cancellations; $($result.timedOut) timeouts"
} finally {
    if (-not $runProcess.HasExited) {
        # Stop only this script's diagnostic child, including on monitor failure.
        Stop-Process -Id $runProcess.Id -ErrorAction SilentlyContinue
        $runProcess.WaitForExit()
    }
    [PSCustomObject]@{
        exitCode=$exitCode; elapsedSeconds=$timer.Elapsed.TotalSeconds; requestedImages=$Images
        maxMinutes=$MaxMinutes; gpuInfo=$gpuInfo; gpuMemoryScope='Whole GPU, including other apps; not app-exclusive VRAM'
        launcher=$checksPath; runtimeModules=@($modules.Values); samples=$samples
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'process.json') -Encoding UTF8
    $runProcess.Dispose()
}
