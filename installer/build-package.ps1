<#
.SYNOPSIS
  Builds a self-contained deployment package for the DICOM Migration Server.

.DESCRIPTION
  Run this on a machine that HAS the .NET SDK (e.g. a dev box with the repo). It publishes a
  self-contained build (so the target server needs no .NET runtime) and assembles a package
  folder containing:
    app\            -- the published binaries (DicomMigration.exe + dependencies)
    install.ps1     -- the installer
    uninstall.ps1   -- the uninstaller

  Copy the package folder (or the optional .zip) to the target server and run install.ps1
  there from an elevated PowerShell prompt.

.EXAMPLE
  .\build-package.ps1 -Zip
#>

[CmdletBinding()]
param(
    [string] $OutputDir,
    [string] $Configuration = "Release",
    [string] $Runtime       = "win-x64",
    [bool]   $SelfContained = $true,
    [switch] $Zip
)

$ErrorActionPreference = "Stop"
function Write-Step([string] $msg) { Write-Host ("[package] " + $msg) -ForegroundColor Cyan }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\src\DicomMigration\DicomMigration.csproj"))

if (-not (Test-Path $project)) { throw "Project not found: $project (run from the repo's installer folder)." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET SDK (dotnet) is required to build the package." }

if (-not $OutputDir) { $OutputDir = Join-Path $scriptDir "..\dist\DicomMigration" }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$appDir = Join-Path $OutputDir "app"

Write-Step ("Project: " + $project)
Write-Step ("Output:  " + $OutputDir)

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appDir | Out-Null

$scArg = "false"
if ($SelfContained) { $scArg = "true" }
Write-Step ("Publishing (" + $Configuration + ", " + $Runtime + ", self-contained=" + $scArg + ") ...")
& dotnet publish $project -c $Configuration -r $Runtime --self-contained $scArg -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $scriptDir "install.ps1")   (Join-Path $OutputDir "install.ps1")   -Force
Copy-Item (Join-Path $scriptDir "uninstall.ps1") (Join-Path $OutputDir "uninstall.ps1") -Force

Write-Step ("Package ready: " + $OutputDir)
Write-Step "Contents: app\ (published binaries), install.ps1, uninstall.ps1"

if ($Zip) {
    $zipPath = $OutputDir + ".zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Step "Creating zip ..."
    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath
    Write-Step ("Zip: " + $zipPath)
}

Write-Host ""
Write-Step "Deploy: copy the package (or zip) to the target server, then from an elevated prompt run:"
Write-Step "  .\install.ps1 -SourceHost <ip> -SourceAe <AE> -DestHost <ip> -DestAe <AE> -Port 5080"
