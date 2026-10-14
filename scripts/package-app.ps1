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
AIrtist 테스트 빌드

이 폴더 전체를 함께 보관하고 AIrtist.exe를 실행하세요.
Windows x64용 .NET·WPF·AI 런타임과 모델을 포함합니다. 별도 Python 설치나 모델 다운로드 없이 실행합니다.
메인 화면에서 그림 그리기, 멀티 플레이, 설정, 종료를 선택합니다.
그림 그리기에서 원하는 그림을 2~160자로 자유롭게 묘사한 뒤 그림 그리기를 누르세요. 사과·토끼처럼 대상 이름을 직접 써도 됩니다.
좌측에서 CFG(1~20)·Steps(1~50)·Seed(무작위 또는 0~2147483647)를 정하고 하단에 묘사를 입력하세요. 기본값은 CFG 7·25 Steps·무작위 Seed입니다.
방 만들기에서 방 파라미터 설정을 체크하면 CFG·Steps·Seed가 모두 고정됩니다. 해제하면 참가자마다 자유롭게 정하며 최종 제출 승인 후에는 바뀌지 않습니다.
한국어는 내부에서 번역한 뒤 자동으로 생성합니다. 번역 확인 화면은 표시하지 않습니다. Enter는 줄바꿈입니다.
완성 후 그림 그리기를 다시 누르면 새 그림을 만듭니다. 원문과 파라미터를 바로 수정할 수 있습니다.
제시어·점수·라운드·작성 제한 시간은 없습니다. 번역·생성은 한 번에 한 작업씩 처리합니다.
AI 처리 한 번의 기술적 제한 시간은 180초입니다. 실패·취소·시간 초과 후 다시 시도할 수 있습니다.
자동 번역의 의미 왜곡과 이미지 품질에는 한계가 있습니다.
설정에서는 창 해상도와 BGM·SFX 음량을 저장합니다. 실제 사운드는 추후 추가됩니다.
멀티 플레이에서는 방 생성 또는 방 참가를 선택합니다. 방 만들기·참가를 누르면 앱이 자동으로 접속합니다.
방 생성 후 오른쪽 위 방 코드를 클릭해 복사하고 친구에게 보내세요. 친구는 방 참가에서 10자리 코드를 입력합니다.
같은 PC에서는 앱을 여러 개 실행한 뒤 같은 방 코드로 참가하세요. 온라인/로컬 선택 없이 자동으로 연결합니다.
같은 Windows 사용자에서 실행한 앱들이 대상이며, 로컬 방은 인터넷·EOS 인증 없이 사용할 수 있습니다.
첫 참가자가 같은 PC이면 로컬 방, 다른 PC이면 EOS 방으로 연결합니다. 두 연결 방식을 한 방에 섞는 것은 지원하지 않습니다.
한 PC의 이미지 생성은 순서대로 처리하며 대기 시간도 생성 제한 시간에 포함됩니다.
모두 모이면 각자 게임 준비를 누르세요. 연결이 준비되면 게임 대기방으로 이동합니다.
묘사 입력 후 그림 그리기를 누르면 내부 번역과 방장 승인을 거쳐 한 장을 생성합니다.
자기 그림은 선택할 수 없으며 확정한 표는 변경할 수 없습니다. 임시 규칙은 60초·전원 투표 시 종료입니다.
싱글·멀티 화면의 메인 화면 버튼으로 돌아갈 수 있습니다. 멀티에서는 방 접속을 종료합니다.
EOS 게임 클라이언트 연결 설정은 앱에 포함되어 별도 파일을 선택하지 않습니다. 다른 PC와의 온라인 연결에는 인터넷이 필요합니다.
독립 앱 3개의 실제 로컬 생성·PNG 전송·투표와 방장 종료 처리를 확인했습니다.
실제 EOS 단일 참가자의 자동 인증·방 코드 생성·나가기·재접속·닫힌 코드 거절을 확인했습니다. 다른 PC 참가·P2P 그림 전송·온라인 투표는 아직 검증 전입니다.
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
