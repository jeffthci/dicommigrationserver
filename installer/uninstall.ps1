<#
.SYNOPSIS
  Stops and removes the DICOM Migration Server Windows service.

.DESCRIPTION
  By default the install directory (including the SQLite database) is left in place so the
  inventory and migration history are preserved. Pass -RemoveFiles to delete it as well.

.EXAMPLE
  .\uninstall.ps1
  .\uninstall.ps1 -RemoveFiles
#>

[CmdletBinding()]
param(
    # Resolved from the installed service below, so -RemoveFiles cannot delete the wrong directory
    # or -- worse -- report success while leaving the PHI-bearing inventory database on disk.
    [string] $InstallDir  = "",
    [string] $ServiceName = "DicomMigration",
    [switch] $RemoveFiles
)

$ErrorActionPreference = "Stop"

function Write-Step([string] $msg) { Write-Host ("[uninstall] " + $msg) -ForegroundColor Cyan }

# Resolve the install directory from the service's own ImagePath. install.ps1 places the service
# where an existing one already lives (or under Program Files for a fresh install), so a hardcoded
# default here would silently miss it.
if (-not $InstallDir) {
    $svcInfo = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($svcInfo -and $svcInfo.PathName) {
        $exeFromSvc = ([regex]::Match($svcInfo.PathName, '^\s*"([^"]+)"|^\s*(\S+)')).Groups |
            Where-Object { $_.Success -and $_.Value -and $_.Value -notmatch '^\s*$' } |
            Select-Object -Last 1 -ExpandProperty Value
        if ($exeFromSvc) { $InstallDir = Split-Path -Parent $exeFromSvc }
    }
    if (-not $InstallDir) {
        $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
        $InstallDir = Join-Path $programFiles "DICOM Migration Server"
    }
}

Write-Step ("Install directory: " + $InstallDir)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    throw "This uninstaller must be run from an elevated (Administrator) PowerShell session."
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    $svcPid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).ProcessId
    if ($svc.Status -ne "Stopped") {
        Write-Step "Stopping service ..."
        Stop-Service -Name $ServiceName -Force
    }
    # Wait for the host process to exit so its files are unlocked (needed for -RemoveFiles).
    if ($svcPid -and $svcPid -ne 0) {
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Process -Id $svcPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Step "Removing service ..."
    & sc.exe delete $ServiceName | Out-Null
    # Wait until the SCM actually drops the service so a later reinstall starts clean.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
} else {
    Write-Step ("Service '" + $ServiceName + "' is not installed.")
}

if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Write-Step ("Removing files at " + $InstallDir + " ...")
        Remove-Item -Path $InstallDir -Recurse -Force
    }
} else {
    Write-Step ("Install directory preserved: " + $InstallDir)
}

Write-Step "Done."
