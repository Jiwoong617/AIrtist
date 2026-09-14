param([Parameter(Mandatory = $true)][string]$Python, [switch]$Stage2)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$venvPath = Join-Path $projectRoot '.venv-tiny-sd'
$experimentPython = Join-Path $venvPath 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $experimentPython)) {
    & $Python -m venv $venvPath
    if ($LASTEXITCODE -ne 0) { throw 'Virtual environment creation failed' }
}
& $experimentPython -m pip install --disable-pip-version-check --no-cache-dir torch==2.8.0 --index-url https://download.pytorch.org/whl/cu126
if ($LASTEXITCODE -ne 0) { throw 'PyTorch installation failed' }
$requirements = Join-Path $PSScriptRoot $(if ($Stage2) { 'requirements-stage2-lock.txt' } else { 'requirements-lock.txt' })
if (-not (Test-Path -LiteralPath $requirements)) {
    $requirements = Join-Path $PSScriptRoot $(if ($Stage2) { 'requirements-stage2.txt' } else { 'requirements.txt' })
}
& $experimentPython -m pip install --disable-pip-version-check --no-cache-dir -r $requirements
if ($LASTEXITCODE -ne 0) { throw 'Experiment dependency installation failed' }
& $experimentPython -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Dependency check failed' }
