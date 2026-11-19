# RoutRunner - package builder
#
#   .\make-package.ps1
#
# Produces:
#   dist\RoutRunner-<version>-win-x64\          (folder: exe + installer scripts + README)
#   dist\RoutRunner-<version>-win-x64.zip       (share this one)
#
# The payload is self-contained (win-x64, single file, compressed): target machines need
# nothing installed - no .NET runtime, no Node.js.
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$version = ([xml](Get-Content .\RoutRunner.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.1.0' }

$stage   = Join-Path $PSScriptRoot 'build\payload'
$package = Join-Path $PSScriptRoot "dist\RoutRunner-$version-win-x64"
$zip     = Join-Path $PSScriptRoot "dist\RoutRunner-$version-win-x64.zip"

Write-Host "publishing self-contained payload..." -ForegroundColor Cyan
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish .\RoutRunner.csproj `
    -c Release `
    -r win-x64 `
    --self-contained:true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    -o $stage

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

Write-Host "assembling package..." -ForegroundColor Cyan
Remove-Item -LiteralPath $package -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $package -Force | Out-Null

Copy-Item (Join-Path $stage 'RoutRunner.exe') $package
Copy-Item .\install.ps1, .\uninstall.ps1, .\README.md $package

$sizeMb = [math]::Round(((Get-Item (Join-Path $package 'RoutRunner.exe')).Length) / 1MB, 1)
Write-Host "  exe: $sizeMb MB"

Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
$zipMb = [math]::Round(((Get-Item $zip).Length) / 1MB, 1)

Write-Host ""
Write-Host " package: $zip  ($zipMb MB)" -ForegroundColor Green
Write-Host " folder : $package" -ForegroundColor DarkGray
Write-Host ""
