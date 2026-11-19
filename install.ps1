#Requires -Version 5.1
<#
    RoutRunner installer - per-machine, elevates once, no external dependencies.

    .\install.ps1                 install to Program Files, register elevated autostart, launch
    .\install.ps1 -NoStartupTask  skip the logon task
    .\install.ps1 -NoLaunch       install only, do not start
    .\install.ps1 -InstallDir D:\Tools\RoutRunner
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'RoutRunner'),
    [switch]$NoStartupTask,
    [switch]$NoLaunch,
    [switch]$NoElevate
)

$ErrorActionPreference = 'Stop'
$AppName = 'RoutRunner'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step($text)  { Write-Host "  $text" -ForegroundColor DarkGray }
function Write-Ok($text)    { Write-Host "  [ok] $text" -ForegroundColor Green }
function Write-Warn2($text) { Write-Host "  [!!] $text" -ForegroundColor Yellow }

Write-Host ""
Write-Host " RoutRunner installer" -ForegroundColor Cyan
Write-Host " --------------------" -ForegroundColor DarkGray

# ---- elevation -------------------------------------------------------------
if (-not (Test-Admin) -and -not $NoElevate) {
    Write-Step "requesting administrator rights"
    $argList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-InstallDir', "`"$InstallDir`""
    )
    if ($NoStartupTask) { $argList += '-NoStartupTask' }
    if ($NoLaunch)      { $argList += '-NoLaunch' }

    try {
        $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -Wait -PassThru
        exit $p.ExitCode
    }
    catch {
        Write-Warn2 "elevation was cancelled, nothing installed"
        exit 1
    }
}

$isAdmin = Test-Admin
if (-not $isAdmin) {
    Write-Warn2 "running unelevated - Program Files, HKLM and the elevated task will be skipped"
}

# ---- payload ---------------------------------------------------------------
$payload = Join-Path $PSScriptRoot "$AppName.exe"
if (-not (Test-Path -LiteralPath $payload)) {
    Write-Warn2 "$AppName.exe is missing next to this script"
    exit 1
}

$version = (Get-Item -LiteralPath $payload).VersionInfo.ProductVersion
if (-not $version) { $version = '1.0.0' }

# ---- stop anything already running ----------------------------------------
Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Step "stopping running instance (pid $($_.Id))"
    try { $_.Kill() } catch { }
}
Start-Sleep -Milliseconds 800

# ---- files -----------------------------------------------------------------
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -LiteralPath $payload -Destination $InstallDir -Force
Write-Ok "installed to $InstallDir"

$uninstaller = Join-Path $PSScriptRoot 'uninstall.ps1'
if (Test-Path -LiteralPath $uninstaller) {
    Copy-Item -LiteralPath $uninstaller -Destination $InstallDir -Force
}

$exePath = Join-Path $InstallDir "$AppName.exe"
$uninstallCmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f (Join-Path $InstallDir 'uninstall.ps1')

# ---- start menu shortcut ---------------------------------------------------
if ($isAdmin) {
    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
}
else {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
}
$lnk = Join-Path $startMenu "$AppName.lnk"
try {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = "$exePath,0"
    $shortcut.Description = 'RoutRunner - 9Router / OmniRoute tray supervisor'
    $shortcut.Save()
    Write-Ok "start menu shortcut: $lnk"
}
catch {
    Write-Warn2 "could not create the start menu shortcut: $($_.Exception.Message)"
}

# ---- add/remove programs ---------------------------------------------------
if ($isAdmin) {
    $arp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RoutRunner'
    try {
        New-Item -Path $arp -Force | Out-Null
        $sizeKb = [int](((Get-Item -LiteralPath $exePath).Length) / 1KB)
        Set-ItemProperty -Path $arp -Name DisplayName          -Value $AppName
        Set-ItemProperty -Path $arp -Name DisplayVersion       -Value $version
        Set-ItemProperty -Path $arp -Name Publisher            -Value 'dj'
        Set-ItemProperty -Path $arp -Name InstallLocation      -Value $InstallDir
        Set-ItemProperty -Path $arp -Name DisplayIcon          -Value "$exePath,0"
        Set-ItemProperty -Path $arp -Name UninstallString      -Value $uninstallCmd
        Set-ItemProperty -Path $arp -Name QuietUninstallString -Value $uninstallCmd
        Set-ItemProperty -Path $arp -Name NoModify             -Value 1 -Type DWord
        Set-ItemProperty -Path $arp -Name NoRepair             -Value 1 -Type DWord
        Set-ItemProperty -Path $arp -Name EstimatedSize        -Value $sizeKb -Type DWord
        Write-Ok "registered in Add/Remove Programs"
    }
    catch {
        Write-Warn2 "could not write the uninstall entry: $($_.Exception.Message)"
    }

    # legacy 1.0 autostart value - the scheduled task replaces it
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'RoutRunner' -ErrorAction SilentlyContinue
}

# ---- elevated logon task ---------------------------------------------------
if (-not $NoStartupTask) {
    if (-not $isAdmin) {
        Write-Warn2 "startup task skipped - needs administrator rights"
    }
    else {
        $reg = Start-Process -FilePath $exePath -ArgumentList '--register-startup' -Wait -PassThru
        if ($reg.ExitCode -eq 0) {
            Write-Ok "logon task registered (runs elevated, no UAC prompt)"
        }
        else {
            Write-Warn2 "could not register the logon task (exit $($reg.ExitCode))"
        }
    }
}

# ---- go --------------------------------------------------------------------
if (-not $NoLaunch) {
    Start-Sleep -Milliseconds 400
    Start-Process -FilePath $exePath
    Write-Ok "RoutRunner started"
}

Write-Host ""
Write-Host " done - $InstallDir" -ForegroundColor Green
Write-Host ""
exit 0
