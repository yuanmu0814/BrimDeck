[CmdletBinding()]
param([string]$OutputDirectory = '.artifacts/pacer-engine', [string]$Products)
$ErrorActionPreference = 'Stop'
# Swift's mimalloc redirect DLL cannot initialize under some process mitigations.
# This documented switch affects only this build and its child compiler processes.
$env:MIMALLOC_DISABLE_REDIRECT = '1'
$brimRoot = Split-Path $PSScriptRoot -Parent
$brimLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'pacer-engine.lock.json') -Raw | ConvertFrom-Json
$brimSource = Join-Path $brimRoot ('.artifacts/pacer-source/' + $brimLock.commit)
$brimOutput = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $brimRoot $OutputDirectory }))
if (!(Test-Path -LiteralPath (Join-Path $brimSource '.git'))) {
    New-Item -ItemType Directory -Path $brimSource -Force | Out-Null
    git -C $brimSource init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the Pacer source cache.' }
    git -C $brimSource remote add origin $brimLock.repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Pacer source.' }
    git -C $brimSource fetch --depth 1 origin $brimLock.commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the pinned Pacer source.' }
    git -C $brimSource checkout --detach --quiet FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out the pinned Pacer source.' }
}
$brimRevision = git -C $brimSource rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $brimRevision -ne $brimLock.commit) { throw 'Pacer source revision mismatch.' }
$brimChanges = git -C $brimSource status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $brimChanges) { throw 'The cached Pacer source has local changes; use a clean cache.' }
if (!$env:PACER_SWIFT_ROOT) { & (Join-Path $brimSource 'scripts/windows/setup-swift.ps1') -Architecture x64 }
$brimScratchBefore = $env:PACER_WINDOWS_SCRATCH_PATH
try {
    $env:PACER_WINDOWS_SCRATCH_PATH = Join-Path ([IO.Path]::GetTempPath()) ('brimdeck-pacer-' + $brimLock.commit.Substring(0, 12))
    & (Join-Path $brimSource 'scripts/windows/build-engine.ps1') -Configuration release -Architecture x64 -Products $Products
    $brimReceipt = Get-Content -LiteralPath (Join-Path $brimSource 'output/windows/engine-location.json') -Raw | ConvertFrom-Json
    $brimBinary = Join-Path $brimReceipt.scratch 'x86_64-unknown-windows-msvc/release'
    $brimSwiftRoot = if ($env:PACER_SWIFT_ROOT) { $env:PACER_SWIFT_ROOT } else { Join-Path $brimSource 'output/toolchains/swift' }
    $brimRuntime = Join-Path $brimSwiftRoot ('Runtimes/' + $brimLock.swiftVersion + '/usr/bin')
    if (!(Test-Path -LiteralPath $brimRuntime -PathType Container)) { throw 'The pinned Swift runtime is missing.' }
    New-Item -ItemType Directory -Path $brimOutput -Force | Out-Null
    foreach ($brimName in @('PacerEngine.exe', 'PacerWindowsBridge.dll')) {
        Copy-Item -LiteralPath (Join-Path $brimBinary $brimName) -Destination $brimOutput -Force
    }
    Get-ChildItem -LiteralPath $brimRuntime -File -Filter '*.dll' | Copy-Item -Destination $brimOutput -Force
    foreach ($brimBundle in Get-ChildItem -LiteralPath $brimBinary -Directory -Filter '*.resources') {
        Copy-Item -LiteralPath $brimBundle.FullName -Destination $brimOutput -Recurse -Force
    }
    $brimLicenses = Join-Path $brimOutput 'Licenses'
    New-Item -ItemType Directory -Path $brimLicenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $brimSource 'LICENSE') -Destination (Join-Path $brimLicenses 'Codex-Pacer-MIT.txt') -Force
    Get-ChildItem -LiteralPath (Join-Path $brimSource 'windows/ThirdParty/Swift') -File | Copy-Item -Destination $brimLicenses -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pacer-engine.lock.json') -Destination $brimOutput -Force
    Write-Output ('Pacer engine ready: ' + $brimOutput)
} finally { $env:PACER_WINDOWS_SCRATCH_PATH = $brimScratchBefore }
