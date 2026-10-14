param([string]$Source = 'config/eos.local.json')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $taskRoot $Source
if ((Get-Item -LiteralPath $sourcePath).Length -gt 16384) { throw 'Online client configuration is too large' }
try { $value = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json } catch { throw 'Invalid online client configuration JSON' }
$payload = [ordered]@{ SdkDirectory = '.' }
foreach ($key in @('ProductId','SandboxId','DeploymentId','ClientId','ClientSecret')) {
    $entry = $value.$key
    if ($entry -isnot [string] -or [string]::IsNullOrWhiteSpace($entry) -or $entry.Length -gt 1024 -or $entry -ne $entry.Trim() -or $entry -match '[\x00-\x1f\x7f]') { throw "Online client field is missing or invalid: $key" }
    $payload[$key] = $entry
}
$destination = Join-Path $taskRoot '.cache/online/eos-client.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
[IO.File]::WriteAllText($destination, ($payload | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output 'Online game-client configuration prepared (values omitted).'
