param([string]$ModelsDirectory = 'models/app-stage3')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$modelsPath = (Resolve-Path -LiteralPath $ModelsDirectory).Path
$layout = Get-Content -LiteralPath (Join-Path $taskRoot 'data/runtime-assets.json') -Raw | ConvertFrom-Json
$paths = @{}
foreach ($relative in $layout.requiredFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $modelsPath $relative))
    if (-not $path.StartsWith($modelsPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $paths.ContainsKey($relative)) { throw 'Invalid runtime layout' }
    $paths[$relative] = $path
    if ($relative -ne 'licenses/CHANGES.txt' -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Prepared asset missing: $relative" }
}
# This explicit preparation step refreshes metadata after a model conversion/configuration change.
# Normal builds verify the existing manifest and never silently re-sign changed model files.
Copy-Item -LiteralPath (Join-Path $taskRoot 'data/MODEL_CHANGES.txt') -Destination $paths['licenses/CHANGES.txt']
$files = @(foreach ($relative in $layout.requiredFiles) {
    $path = $paths[$relative]
    [ordered]@{ path=$relative; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ version=$layout.version; profile=$layout.profile; files=$files }
[IO.File]::WriteAllText((Join-Path $modelsPath 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Output ("Prepared {0} manifest: {1} files" -f $layout.profile,$files.Count)
