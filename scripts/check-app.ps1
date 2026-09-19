param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('self-check', 'match-smoke')][string]$Mode = 'match-smoke'
)
$ErrorActionPreference = 'Stop'
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$executablePath = Join-Path $packagePath 'AIrtist.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw 'Package executable missing' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new diagnostic output directory' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$gpuCommand = Get-Command nvidia-smi -ErrorAction SilentlyContinue
$gpuInfo = if ($gpuCommand) { & $gpuCommand.Source --query-gpu=name,driver_version,memory.total --format=csv,noheader }
$samples = [Collections.Generic.List[object]]::new()
$modules = @{}
$timer = [Diagnostics.Stopwatch]::StartNew()
$appProcess = Start-Process -FilePath $executablePath -ArgumentList @("--$Mode", ('"' + $outputPath + '"')) -WorkingDirectory $packagePath -WindowStyle Hidden -PassThru
try {
    while (-not $appProcess.HasExited) {
        $appProcess.Refresh()
        $gpuMemory = if ($gpuCommand) { & $gpuCommand.Source --query-gpu=memory.used --format=csv,noheader,nounits }
        $samples.Add([PSCustomObject]@{ seconds=$timer.Elapsed.TotalSeconds; workingSet=$appProcess.WorkingSet64; privateBytes=$appProcess.PrivateMemorySize64; wholeGpuUsedMiB=$gpuMemory })
        try {
            foreach ($module in $appProcess.Modules) {
                if ($module.ModuleName -in @('coreclr.dll', 'clrjit.dll', 'onnxruntime.dll', 'DirectML.dll')) { $modules[$module.ModuleName] = $module.FileName }
            }
        } catch [System.ComponentModel.Win32Exception] { }
        if ($timer.Elapsed.TotalSeconds -gt 600) {
            # Only the process created above is stopped on a diagnostic timeout.
            Stop-Process -Id $appProcess.Id
            throw 'App diagnostic exceeded 600 seconds'
        }
        Start-Sleep -Milliseconds 500
    }
    $appProcess.WaitForExit()
    $exitCode = $appProcess.ExitCode
    $result = [PSCustomObject]@{
        mode=$Mode; package=$packagePath; exitCode=$exitCode; elapsedSeconds=$timer.Elapsed.TotalSeconds
        gpuInfo=$gpuInfo; gpuMemoryScope='Whole GPU, including other apps; not app-exclusive VRAM'
        peakWorkingSet=($samples | Measure-Object workingSet -Maximum).Maximum
        peakPrivateBytes=($samples | Measure-Object privateBytes -Maximum).Maximum
        runtimeModules=$modules; samples=$samples
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'process.json') -Encoding UTF8
    if ($exitCode -ne 0) { throw "App diagnostic failed with exit $exitCode; see status.json" }
    foreach ($name in @('coreclr.dll', 'clrjit.dll', 'onnxruntime.dll', 'DirectML.dll')) {
        if ($Mode -eq 'match-smoke' -and (-not $modules[$name] -or -not $modules[$name].StartsWith($packagePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
            throw "Runtime was not observed in package: $name"
        }
    }
    Get-Content -LiteralPath (Join-Path $outputPath 'status.json')
} finally { $appProcess.Dispose() }
