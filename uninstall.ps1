#Requires -Version 5.1
<#
    RoutRunner uninstaller - removes the app, the elevated logon task and the shortcuts.
    .\uninstall.ps1 [-Purge]     -Purge also deletes %APPDATA%\RoutRunner (settings + logs)
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'RoutRunner'),
    [switch]$Purge,
    [switch]$NoElevate
)

$ErrorActionPreference = 'Stop'
$AppName = 'RoutRunner'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Ok($t)    { Write-Host "  [ok] $t" -ForegroundColor Green }
function Write-Warn2($t) { Write-Host "  [!!] $t" -ForegroundColor Yellow }

Write-Host ""
Write-Host " RoutRunner uninstaller" -ForegroundColor Cyan
Write-Host " ----------------------" -ForegroundColor DarkGray

if (-not (Test-Admin) -and -not $NoElevate) {
    $argList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-InstallDir', "`"$InstallDir`""
    )
    if ($Purge) { $argList += '-Purge' }

    try {
        $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -Wait -PassThru
        exit $p.ExitCode
    }
    catch {
        Write-Warn2 "elevation was cancelled, nothing removed"
        exit 1
    }
}

# ---- logon task ------------------------------------------------------------
$exePath = Join-Path $InstallDir "$AppName.exe"
if (Test-Path -LiteralPath $exePath) {
    $u = Start-Process -FilePath $exePath -ArgumentList '--unregister-startup' -Wait -PassThru
    if ($u.ExitCode -eq 0) {
        Write-Ok "logon task removed"
    }
    else {
        Write-Warn2 "logon task removal returned $($u.ExitCode)"
    }
}
else {
    & schtasks.exe /Delete /TN $AppName /F 2>$null | Out-Null
    Write-Ok "logon task removed (schtasks)"
}

# ---- processes -------------------------------------------------------------
Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(3000) } catch { }
}
Start-Sleep -Milliseconds 600

# ---- shortcuts -------------------------------------------------------------
foreach ($base in @($env:ProgramData, $env:APPDATA)) {
    $lnk = Join-Path $base "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
    if (Test-Path -LiteralPath $lnk) {
        Remove-Item -LiteralPath $lnk -Force
        Write-Ok "removed $lnk"
    }
}

# ---- add/remove programs ---------------------------------------------------
$arp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RoutRunner'
if (Test-Path $arp) {
    Remove-Item -Path $arp -Recurse -Force
    Write-Ok "removed the Add/Remove Programs entry"
}

# ---- files -----------------------------------------------------------------
if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Ok "removed $InstallDir"
}

if ($Purge) {
    $data = Join-Path $env:APPDATA $AppName
    if (Test-Path -LiteralPath $data) {
        Remove-Item -LiteralPath $data -Recurse -Force
        Write-Ok "removed $data (settings and logs)"
    }
}

Write-Host ""
Write-Host " RoutRunner removed." -ForegroundColor Green
Write-Host ""
exit 0
