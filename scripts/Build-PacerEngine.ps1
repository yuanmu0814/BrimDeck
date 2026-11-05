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
$brimBuildSource = $brimSource
if ($brimLock.patch) {
    $brimPatch = Join-Path $PSScriptRoot $brimLock.patch
    if ((Get-FileHash -LiteralPath $brimPatch -Algorithm SHA256).Hash.ToLowerInvariant() -ne $brimLock.patchSha256) { throw 'Pacer patch checksum mismatch.' }
    $brimBuildSource = Join-Path $brimRoot ('.artifacts/pacer-work/' + $brimLock.commit + '-' + $brimLock.patchSha256.Substring(0, 12))
    if (!(Test-Path -LiteralPath (Join-Path $brimBuildSource '.git'))) {
        git clone --config core.autocrlf=true --quiet --shared --no-checkout $brimSource $brimBuildSource
        if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the patched Pacer source.' }
        git -C $brimBuildSource -c core.autocrlf=true checkout --detach --quiet $brimLock.commit
        if ($LASTEXITCODE -ne 0) { throw 'Could not check out the patch base.' }
        git -C $brimBuildSource apply --whitespace=error-all $brimPatch
        if ($LASTEXITCODE -ne 0) { throw 'Could not apply the pinned Pacer patch.' }
    }
    $brimPatchRevision = git -C $brimBuildSource rev-parse HEAD
    $brimApplied = ((git -C $brimBuildSource diff --binary --no-ext-diff) -join "`n") + "`n"
    $brimAppliedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($brimApplied))).ToLowerInvariant()
    if ($brimPatchRevision -ne $brimLock.commit -or $brimAppliedHash -ne $brimLock.patchSha256) { throw 'Patched Pacer cache does not match the lock.' }
}
if (!$env:PACER_SWIFT_ROOT) { & (Join-Path $brimBuildSource 'scripts/windows/setup-swift.ps1') -Architecture x64 }
$brimScratchBefore = $env:PACER_WINDOWS_SCRATCH_PATH
try {
    if (!$env:PACER_WINDOWS_SCRATCH_PATH) {
        $env:PACER_WINDOWS_SCRATCH_PATH = Join-Path ([IO.Path]::GetTempPath()) ('brimdeck-pacer-' + $brimLock.commit.Substring(0, 12) + '-' + $brimLock.patchSha256.Substring(0, 12))
    }
    & (Join-Path $brimBuildSource 'scripts/windows/build-engine.ps1') -Configuration release -Architecture x64 -Products $Products
    $brimReceipt = Get-Content -LiteralPath (Join-Path $brimBuildSource 'output/windows/engine-location.json') -Raw | ConvertFrom-Json
    $brimBinary = Join-Path $brimReceipt.scratch 'x86_64-unknown-windows-msvc/release'
    # Ship the bridge from the same patched Core, never an older prebuilt DLL.
    if (!$brimReceipt.prebuilt -or !(Test-Path -LiteralPath (Join-Path $brimBinary 'PacerWindowsBridge.dll'))) {
        . (Join-Path $brimBuildSource 'scripts/windows/environment.ps1')
        swift build --build-system native @taskSwiftFlags @taskSwiftTargetFlags --package-path $brimReceipt.source --scratch-path $brimReceipt.scratch --configuration release --product PacerWindowsBridge
        if ($LASTEXITCODE -ne 0) { throw 'Windows engine bridge build failed.' }
    }
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
    Copy-Item -LiteralPath (Join-Path $brimBuildSource 'LICENSE') -Destination (Join-Path $brimLicenses 'Codex-Pacer-MIT.txt') -Force
    Get-ChildItem -LiteralPath (Join-Path $brimBuildSource 'windows/ThirdParty/Swift') -File | Copy-Item -Destination $brimLicenses -Force
    Copy-Item -LiteralPath (Join-Path $brimReceipt.source 'source-receipt.json') -Destination $brimOutput -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pacer-engine.lock.json') -Destination $brimOutput -Force
    Write-Output ('Pacer engine ready: ' + $brimOutput)
} finally { $env:PACER_WINDOWS_SCRATCH_PATH = $brimScratchBefore }
