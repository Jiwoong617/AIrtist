param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$ModelsDirectory = 'models/app-stage3',
    [string]$ReportPath = 'outputs/build/package-size.json'
)
$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$modelsPath = (Resolve-Path -LiteralPath $ModelsDirectory).Path
$packagePath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $packagePath) { throw 'Use a new output folder; existing packages are not overwritten' }
foreach ($sourceRoot in @($publishPath, $modelsPath)) {
    if ($packagePath.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output cannot be inside an input folder' }
}
foreach ($required in @('AIrtist.exe', 'coreclr.dll', 'onnxruntime.dll', 'DirectML.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $required) -PathType Leaf)) { throw "Runtime missing: $required" }
}
$manifestPath = Join-Path $modelsPath 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.files.Count -eq 0) { throw 'Prepared model manifest is missing or unsupported' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $modelsPath $entry.path))
    if (-not $sourcePath.StartsWith($modelsPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($entry.path)) { throw 'Invalid or duplicate model path' }
    $item = Get-Item -LiteralPath $sourcePath
    if ($item.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Model integrity check failed: $($entry.path)" }
}
foreach ($required in @('generation/unet-w8.onnx', 'generation/text_encoder-compact.onnx', 'generation/taesd_decoder-compact.onnx', 'generation/tokenizer/vocab.json', 'generation/tokenizer/merges.txt', 'generation/settings.json', 'generation/scheduler.json', 'translation/step-fp16-storage.onnx', 'translation/source.spm', 'translation/vocab.json', 'scoring/tinyclip-image.onnx', 'scoring/vectors.json')) {
    if (-not $seen.Contains($required)) { throw "Required model is not in manifest: $required" }
}
New-Item -ItemType Directory -Path $packagePath | Out-Null
foreach ($sourceFile in Get-ChildItem -LiteralPath $publishPath -File -Recurse) {
    if ($sourceFile.Extension -in @('.pdb', '.lib')) { continue }
    $destination = Join-Path $packagePath $sourceFile.FullName.Substring($publishPath.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination
}
foreach ($relative in @($manifest.files.path) + @('manifest.json')) {
    $destination = Join-Path (Join-Path $packagePath 'models') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $modelsPath $relative) -Destination $destination
}
$readme = @'
AIrtist 오프라인 경기 개발 미리보기

이 폴더 전체를 함께 보관하고 AIrtist.exe를 실행하세요.
Windows x64용 .NET·WPF·AI 런타임과 모델을 포함합니다. 별도 Python 설치나 모델 다운로드 없이 실행합니다.
기본 3라운드·작성 60초·AI 처리 180초이며 시작 화면에서 변경할 수 있습니다.
제시어를 쓰지 않고 2~160자로 묘사한 뒤 한 장 그리기를 누르세요. Enter는 줄바꿈입니다.
제출 승인 후 생성은 한 번입니다. 실패·취소·마감·건너뛰기는 0점이며 다음 라운드로 진행합니다.
번역 의미 왜곡과 무관한 이미지의 높은 평가점이 확인되어 품질 개선 중입니다. 점수는 정답 확률이 아닙니다.
멀티플레이는 아직 없습니다. 모델과 라이브러리의 조건·고지는 models/licenses 폴더에 있습니다.
일반 실행의 그림·입력은 앱 메모리에만 보관합니다. 진단 옵션은 지정한 폴더에 검사 자료를 저장합니다.
'@
[IO.File]::WriteAllText((Join-Path $packagePath 'README.txt'), $readme, [Text.UTF8Encoding]::new($false))
$files = @(Get-ChildItem -LiteralPath $packagePath -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=($_.FullName.Substring($packagePath.Length + 1).Replace('\','/')); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$packageManifest = [ordered]@{ files=$files; inventory_excludes_this_file=$true }
[IO.File]::WriteAllText((Join-Path $packagePath 'package-manifest.json'), ($packageManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$allFiles = @(Get-ChildItem -LiteralPath $packagePath -File -Recurse)
$bytes = [long]($allFiles | Measure-Object Length -Sum).Sum
$summary = [ordered]@{ path=$packagePath; installed_bytes=$bytes; file_count=$allFiles.Count; budget_bytes=1000000000; remaining_bytes=(1000000000-$bytes); within_budget=($bytes -le 1000000000); includes=@('app','.NET/WPF','ORT/DirectML','models/tokenizers/vectors','licenses/manifests'); not_yet_implemented=@('EOS/multiplayer','installer','clean PC validation'); quality_not_passed=@('translation meaning preservation','scoring discrimination','low-spec hardware validation') }
$reportFullPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $reportFullPath) -Force | Out-Null
[IO.File]::WriteAllText($reportFullPath, ($summary | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 6
if ($bytes -gt 1000000000) { throw 'Package exceeds the 1 GB target' }
