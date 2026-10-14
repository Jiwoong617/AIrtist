param(
    [string]$PackageDirectory,
    [string]$PackageReport = 'outputs/build/package-size.json',
    [switch]$CheckRules
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    if (Test-Path -LiteralPath (Join-Path $taskRoot 'config/eos.local.json')) { & (Join-Path $PSScriptRoot 'prepare-online-config.ps1') }
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.cache/dotnet'
    $env:NUGET_PACKAGES = Join-Path $taskRoot '.cache/nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    dotnet restore src/AIrtist.App --configfile NuGet.Config --locked-mode
    if ($LASTEXITCODE) { throw 'App restore failed' }
    if ($CheckRules) {
        dotnet restore tests/AIrtist.Checks --configfile NuGet.Config --locked-mode
        if ($LASTEXITCODE) { throw 'Checks restore failed' }
        dotnet run --project tests/AIrtist.Checks --no-restore -- unit $taskRoot
        if ($LASTEXITCODE) { throw 'Rule checks failed' }
    }
    dotnet publish src/AIrtist.App -c Release --no-restore --output .cache/stage4/publish
    if ($LASTEXITCODE) { throw 'Publish failed' }
    if ($PackageDirectory) {
        & (Join-Path $PSScriptRoot 'package-app.ps1') -PublishDirectory .cache/stage4/publish -OutputDirectory $PackageDirectory -ReportPath $PackageReport
    }
} finally {
    Pop-Location
}
