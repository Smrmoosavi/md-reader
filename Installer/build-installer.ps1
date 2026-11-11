$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$root = Split-Path -Parent $installerDir
$payloadDir = Join-Path $installerDir 'payload'
$payloadZip = Join-Path $installerDir 'payload.zip'

Write-Host 'Publishing MD Reader...'
if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
dotnet publish (Join-Path $root 'MdReader.csproj') -c Release -r win-x64 --self-contained false -o $payloadDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Packing payload...'
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path (Join-Path $payloadDir '*') -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host 'Building installer...'
$distDir = Join-Path $installerDir 'dist'
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }

# Single file so the installer can copy itself into the install folder as Uninstall.exe.
dotnet publish (Join-Path $installerDir 'Installer.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $distDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$setup = Join-Path $distDir 'MdReaderSetup.exe'
Write-Host ''
Write-Host "Installer ready: $setup"
