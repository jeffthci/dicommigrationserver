<#
.SYNOPSIS
  Builds a Windows Installer (.msi) for the DICOM Migration Server.

.DESCRIPTION
  Run on a machine with the .NET SDK and the WiX v5 toolset. It publishes a self-contained
  build and packages it into an MSI that installs to Program Files and registers the Windows
  service. The resulting MSI can be deployed to other machines by double-click, or silently:
      msiexec /i DicomMigration.msi /qn WEBPORT=5080

  WiX v5 setup (one time):
      dotnet tool install --global wix --version 5.0.2
      wix extension add -g WixToolset.Util.wixext/5.0.2

  WiX v6/v7 are intentionally avoided: they require accepting the Open Source Maintenance
  Fee EULA. v5 is MIT-licensed and free.

.EXAMPLE
  .\build-msi.ps1 -Version 1.0.0.0
#>

[CmdletBinding()]
param(
    [string] $Version       = "1.0.0.0",
    [string] $PublishDir,
    [string] $OutputDir,
    [string] $Runtime       = "win-x64",
    [bool]   $SelfContained = $true
)

$ErrorActionPreference = "Stop"
function Write-Step([string] $msg) { Write-Host ("[msi] " + $msg) -ForegroundColor Cyan }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\src\DicomMigration\DicomMigration.csproj"))
$wxs = Join-Path $scriptDir "wix\Package.wxs"

if (-not (Test-Path $project)) { throw "Project not found: $project" }
if (-not (Test-Path $wxs)) { throw "WiX source not found: $wxs" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET SDK (dotnet) is required." }

# Make sure global dotnet tools (wix) are on PATH for this session.
$toolsDir = Join-Path $env:USERPROFILE ".dotnet\tools"
if (Test-Path $toolsDir) { $env:Path = $env:Path + ";" + $toolsDir }
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX not found. Install it with: dotnet tool install --global wix --version 5.0.2 ; wix extension add -g WixToolset.Util.wixext/5.0.2"
}

if (-not $OutputDir) { $OutputDir = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\dist")) }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $PublishDir) { $PublishDir = Join-Path $OutputDir "msi-publish" }
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)

# 1. Publish self-contained ----------------------------------------------------------------
Write-Step ("Publishing (" + $Runtime + ", self-contained=" + $SelfContained + ") ...")
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
$sc = "false"
if ($SelfContained) { $sc = "true" }
& dotnet publish $project -c Release -r $Runtime --self-contained $sc -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Never ship a stale dev database/logs inside the installer payload.
Remove-Item (Join-Path $PublishDir "data") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $PublishDir "logs") -Recurse -Force -ErrorAction SilentlyContinue

# Stage the main exe outside PublishDir so the harvest (Files) covers everything else and the
# exe (which carries the service definition) is added once via its own component.
$exeDir = Join-Path $OutputDir "msi-exe"
if (Test-Path $exeDir) { Remove-Item $exeDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $exeDir | Out-Null
Move-Item (Join-Path $PublishDir "DicomMigration.exe") (Join-Path $exeDir "DicomMigration.exe") -Force

# 2. Build the MSI -------------------------------------------------------------------------
$msi = Join-Path $OutputDir "DicomMigration.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }
Write-Step ("Building MSI (version " + $Version + ") ...")
& wix build $wxs -arch x64 -ext WixToolset.Util.wixext -d "PublishDir=$PublishDir" -d "ExeDir=$exeDir" -d "Version=$Version" -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Step ("MSI ready: " + $msi)
Write-Step "Install (double-click) or silently:  msiexec /i DicomMigration.msi /qn WEBPORT=5080"
Write-Step "Uninstall:                           msiexec /x DicomMigration.msi /qn"
Write-Step "After install browse to http://localhost:5080/ and set the AE/host/port from the dashboard."
