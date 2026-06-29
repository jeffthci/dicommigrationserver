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
    [string] $InstallDir  = "C:\Services\DicomMigration",
    [string] $ServiceName = "DicomMigration",
    [switch] $RemoveFiles
)

$ErrorActionPreference = "Stop"

function Write-Step([string] $msg) { Write-Host ("[uninstall] " + $msg) -ForegroundColor Cyan }

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
