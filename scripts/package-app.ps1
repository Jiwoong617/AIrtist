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
foreach ($required in @('AIrtist.exe', 'coreclr.dll', 'onnxruntime.dll', 'DirectML.dll', 'AIrtist.Eos.dll', 'AIrtist.EosSdk.dll', 'EOSSDK-Win64-Shipping.dll', 'licenses/EOS-ThirdPartySoftwareNotice.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $required) -PathType Leaf)) { throw "Runtime missing: $required" }
}
$manifestPath = Join-Path $modelsPath 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$taskRoot = Split-Path -Parent $PSScriptRoot
$layout = Get-Content -LiteralPath (Join-Path $taskRoot 'data/runtime-assets.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $layout.version -or $manifest.profile -ne $layout.profile -or $manifest.files.Count -ne $layout.requiredFiles.Count) { throw 'Prepare the current drawing model manifest with scripts/prepare-runtime-assets.ps1' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ($entry.path -notin $layout.requiredFiles) { throw "Unexpected runtime asset: $($entry.path)" }
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $modelsPath $entry.path))
    if (-not $sourcePath.StartsWith($modelsPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($entry.path)) { throw 'Invalid or duplicate model path' }
    $item = Get-Item -LiteralPath $sourcePath
    if ($item.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Model integrity check failed: $($entry.path)" }
}
foreach ($required in $layout.requiredFiles) {
    if (-not $seen.Contains($required)) { throw "Required model is not in manifest: $required" }
}
# Multiplayer vocabulary is independent of AI model assets and evaluation vectors.
$wordsPath = Join-Path $taskRoot 'data/words.json'
$words = @(Get-Content -LiteralPath $wordsPath -Raw | ConvertFrom-Json)
$wordIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($words.Count -eq 0) { throw 'Empty word catalogue' }
foreach ($word in $words) {
    if ([string]::IsNullOrWhiteSpace($word.id) -or -not $wordIds.Add($word.id) -or [string]::IsNullOrWhiteSpace($word.korean) -or [string]::IsNullOrWhiteSpace($word.meaning) -or @($word.aliases).Count -eq 0) { throw 'Invalid multiplayer word catalogue' }
}
New-Item -ItemType Directory -Path $packagePath | Out-Null
foreach ($sourceFile in Get-ChildItem -LiteralPath $publishPath -File -Recurse) {
    if ($sourceFile.Extension -in @('.pdb', '.lib') -or $sourceFile.Name -like '*.local.json') { continue }
    $destination = Join-Path $packagePath $sourceFile.FullName.Substring($publishPath.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination
}
foreach ($relative in @($manifest.files.path) + @('manifest.json')) {
    $destination = Join-Path (Join-Path $packagePath 'models') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $modelsPath $relative) -Destination $destination
}
New-Item -ItemType Directory -Path (Join-Path $packagePath 'data') -Force | Out-Null
Copy-Item -LiteralPath $wordsPath -Destination (Join-Path $packagePath 'data/words.json')
$readme = @'
AIrtist 그림그리기 개발 미리보기

이 폴더 전체를 함께 보관하고 AIrtist.exe를 실행하세요.
Windows x64용 .NET·WPF·AI 런타임과 모델을 포함합니다. 별도 Python 설치나 모델 다운로드 없이 실행합니다.
원하는 그림을 2~160자로 자유롭게 묘사한 뒤 번역 확인을 누르세요. 사과·토끼처럼 대상 이름을 직접 써도 됩니다.
영어 묘사를 2~320자로 확인·수정한 뒤 확정하고 그리기를 누르세요. Enter는 줄바꿈입니다.
완성 후 다시 그리기를 누르면 같은 묘사로 새 그림을 만듭니다. 묘사 수정에서 영어를 고치거나 원문으로 돌아갈 수 있습니다.
제시어·점수·라운드·작성 제한 시간은 없습니다. 번역·생성은 한 번에 한 작업씩 처리합니다.
AI 처리 한 번의 기술적 제한 시간은 180초입니다. 실패·취소·시간 초과 후 다시 시도할 수 있습니다.
자동 번역에 의미 왜곡이 남아 있으므로 생성 전에 확인해 주세요.
상단 경기 미리보기에서는 한 PC에서 참가자 2~4명의 역할을 바꿔 실제 AI와 여러 라운드를 체험합니다.
자기 그림은 선택할 수 없으며 확정한 표는 변경할 수 없습니다. 임시 규칙은 60초·전원 투표 시 종료입니다.
미리보기의 연결은 메모리 안에서 처리합니다. 새 미리보기나 창 닫기는 현재 그림과 표를 초기화합니다.
온라인 방은 개발용입니다. 별도로 준비한 EOS 로컬 설정 파일을 선택하고 접속·방 생성/검색·참가·전원 연결 준비 후 경기 대기방을 여세요.
설정 파일은 이 패키지에 포함하지 않습니다. 실제 EOS 단일 참가자의 인증·방 검색/생성/나가기·재접속을 확인했습니다. 다른 PC 참가·P2P 그림 전송·온라인 투표는 아직 검증 전입니다.
모델과 라이브러리의 조건·고지는 models/licenses와 licenses 폴더에 있습니다.
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
$summary = [ordered]@{ path=$packagePath; installed_bytes=$bytes; file_count=$allFiles.Count; budget_bytes=1000000000; remaining_bytes=(1000000000-$bytes); within_budget=($bytes -le 1000000000); includes=@('app','.NET/WPF','ORT/DirectML','EOS managed/native runtime','generation/translation models/tokenizers','multiplayer word catalogue','licenses/manifests'); asset_profile=$layout.profile; not_yet_implemented=@('installer'); not_yet_verified=@('different-PC EOS lobby/P2P image transfer and voting','authentication cancellation/renewal','clean PC validation'); quality_not_passed=@('translation meaning preservation','low-spec hardware validation') }
$reportFullPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $reportFullPath) -Force | Out-Null
[IO.File]::WriteAllText($reportFullPath, ($summary | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 6
if ($bytes -gt 1000000000) { throw 'Package exceeds the 1 GB target' }
