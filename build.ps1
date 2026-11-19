# RoutRunner - build / publish
#
#   .\build.ps1                  -> framework-dependent single exe in .\dist  (needs .NET 8 Desktop Runtime, already on this box)
#   .\build.ps1 -SelfContained   -> ~150 MB exe with the runtime baked in (portable to a machine without .NET)
#
param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$props = @(
    '-c', 'Release',
    '-r', 'win-x64',
    "--self-contained:$($SelfContained.IsPresent)",
    '/p:PublishSingleFile=true'
)

if ($SelfContained) {
    $props += '/p:IncludeNativeLibrariesForSelfExtract=true'
    $props += '/p:EnableCompressionInSingleFile=true'
}

dotnet publish .\RoutRunner.csproj @props -o .\dist

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "output: $PSScriptRoot\dist\RoutRunner.exe" -ForegroundColor Green
