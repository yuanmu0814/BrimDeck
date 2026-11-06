param([string]$Version = '1.2.5-alpha.12', [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$usbBuildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$usbBuildOutput = Join-Path $usbBuildRoot '.artifacts/usb-sensors'
& $Dotnet publish (Join-Path $usbBuildRoot 'src/BrimDeck.UsbSensors/BrimDeck.UsbSensors.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $usbBuildOutput | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'USB sensor service publish failed.' }
$usbBuildExe = Join-Path $usbBuildOutput 'BrimDeck.UsbSensors.exe'
[pscustomobject]@{ Exe = $usbBuildExe; Sha256 = (Get-FileHash -LiteralPath $usbBuildExe -Algorithm SHA256).Hash }
