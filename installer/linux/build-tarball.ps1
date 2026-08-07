<#
.SYNOPSIS
  Builds a self-contained Linux deployment package for the DICOM Migration Server.

.DESCRIPTION
  Run this on a machine that HAS the .NET SDK (e.g. the Windows dev box with the repo).
  Cross-publishing to linux-x64 from Windows is supported and needs no Linux machine.

  It publishes a self-contained build (so the target server needs no .NET runtime) and
  assembles a package containing:
    app/                     -- the published binaries (DicomMigration + dependencies)
    install.sh               -- the systemd installer
    uninstall.sh             -- the uninstaller
    dicom-migration.service  -- systemd unit template

  Copy the resulting .tar.gz to the target server, unpack it, and run install.sh there.

  Shell scripts and the unit file are written with LF endings regardless of how this
  repo was checked out; CRLF would break both on Linux.

.EXAMPLE
  .\build-tarball.ps1
  .\build-tarball.ps1 -Runtime linux-arm64
#>

[CmdletBinding()]
param(
    [string] $OutputDir,
    [string] $Configuration = "Release",
    [string] $Runtime       = "linux-x64",
    [bool]   $SelfContained = $true,
    [switch] $NoArchive
)

$ErrorActionPreference = "Stop"
function Write-Step([string] $msg) { Write-Host ("[package] " + $msg) -ForegroundColor Cyan }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\..\src\DicomMigration\DicomMigration.csproj"))

if (-not (Test-Path $project)) { throw "Project not found: $project (run from the repo's installer\linux folder)." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET SDK (dotnet) is required to build the package." }

if (-not $OutputDir) { $OutputDir = Join-Path $scriptDir "..\..\dist\DicomMigration-linux" }
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

# Copy the Linux-side files with LF endings. Set-Content would apply the platform ending,
# so write the bytes directly.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
foreach ($name in @("install.sh", "uninstall.sh", "dicom-migration.service")) {
    $src = Join-Path $scriptDir $name
    if (-not (Test-Path $src)) { throw "Missing packaging input: $src" }
    $text = [System.IO.File]::ReadAllText($src)
    $text = $text.Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText((Join-Path $OutputDir $name), $text, $utf8NoBom)
}

Write-Step ("Package ready: " + $OutputDir)

if (-not $NoArchive) {
    if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
        Write-Step "tar not found; skipping archive (copy the folder as-is)."
    }
    else {
        $tarPath = $OutputDir + ".tar.gz"
        if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
        Write-Step "Creating tar.gz ..."
        $parent = Split-Path -Parent $OutputDir
        $leaf   = Split-Path -Leaf $OutputDir
        & tar -czf $tarPath -C $parent $leaf
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
        Write-Step ("Archive: " + $tarPath)
    }
}

Write-Host ""
Write-Step "Deploy: copy the .tar.gz to the target server, then run:"
Write-Step "  tar -xzf DicomMigration-linux.tar.gz"
Write-Step "  cd DicomMigration-linux"
Write-Step "  sudo bash install.sh --source-host <ip> --source-ae <AE> --dest-host <ip> --dest-ae <AE>"
Write-Host ""
Write-Step "Note: 'bash install.sh' is used because tar built on Windows does not carry the"
Write-Step "      executable bit. Alternatively: chmod +x install.sh && sudo ./install.sh"
