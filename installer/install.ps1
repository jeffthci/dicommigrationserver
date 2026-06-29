<#
.SYNOPSIS
  Installs the DICOM Migration Server as a Windows service, creating and preconfiguring
  the SQLite database with no user intervention.

.DESCRIPTION
  Steps performed:
    1. Stops and removes any existing instance of the service.
    2. Publishes the app to the install directory (or reuses an existing publish).
    3. Writes appsettings.Production.json (UTF-8, no BOM) with the supplied DICOM config.
    4. Runs "DicomMigration.exe init-db" to create the schema and seed the settings table.
    5. Registers the Windows service (auto-start) and starts it.

  All configuration is provided via parameters with sensible defaults, so the installer
  runs unattended. Re-running it performs an in-place upgrade.

.EXAMPLE
  .\install.ps1 -SourceHost 10.0.0.10 -SourceAe SRC_PACS -DestHost 10.0.0.20 -DestAe DST_PACS
#>

[CmdletBinding()]
param(
    [string] $InstallDir          = "C:\Services\DicomMigration",
    [string] $ServiceName         = "DicomMigration",
    [string] $ServiceDisplayName  = "DICOM Migration Server",
    [int]    $Port                = 5080,

    [string] $LocalAe             = "MIGRATE",
    [string] $SourceAe            = "SOURCE_PACS",
    [string] $SourceHost          = "127.0.0.1",
    [int]    $SourcePort          = 104,
    [string] $DestAe              = "DEST_PACS",
    [string] $DestHost            = "127.0.0.1",
    [int]    $DestPort            = 104,

    [int]    $MaxParallelMoves    = 1,
    [int]    $DelayBetweenMovesMs = 0,
    [int]    $MoveTimeoutMinutes  = 120,
    [int]    $MaxAttempts         = 2,
    [ValidateSet("Study","Series","Instance")]
    [string] $VerifyLevel         = "Study",

    [string] $ProjectPath,
    [bool]   $SelfContained       = $true,
    [switch] $SkipPublish
)

$ErrorActionPreference = "Stop"

function Write-Step([string] $msg) { Write-Host ("[install] " + $msg) -ForegroundColor Cyan }
function Write-Warn2([string] $msg) { Write-Host ("[install] WARNING: " + $msg) -ForegroundColor Yellow }

# --- Preconditions -----------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    throw "This installer must be run from an elevated (Administrator) PowerShell session."
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ProjectPath) {
    $ProjectPath = Join-Path $scriptDir "..\src\DicomMigration\DicomMigration.csproj"
}
$ProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$exePath = Join-Path $InstallDir "DicomMigration.exe"

Write-Step ("Install directory: " + $InstallDir)
Write-Step ("Service name:      " + $ServiceName)

# --- 1. Remove existing service ----------------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Step "Stopping and removing existing service..."

    # Capture the host process so we can wait for it to fully exit and release file locks
    # before publish overwrites the executable and DLLs.
    $svcPid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).ProcessId
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
    }
    if ($svcPid -and $svcPid -ne 0) {
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Process -Id $svcPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
    }

    # sc.exe delete only marks the service for deletion; wait until the SCM actually drops it
    # so the New-Service below cannot fail with error 1072 ("marked for deletion").
    & sc.exe delete $ServiceName | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        throw "Service '$ServiceName' is still present (marked for deletion). Close services.msc or any open handles to it, then re-run."
    }
}

# --- 2. Publish --------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if (-not $SkipPublish) {
    Write-Step ("Publishing " + $ProjectPath + " ...")
    $scArg = "false"
    if ($SelfContained) { $scArg = "true" }
    & dotnet publish $ProjectPath -c Release -r win-x64 --self-contained $scArg -o $InstallDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} else {
    Write-Step "Skipping publish (using existing files in install directory)."
}
if (-not (Test-Path $exePath)) { throw "Expected executable not found: $exePath" }

New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir "data") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir "logs") | Out-Null

# --- 3. Write appsettings.Production.json (no BOM) ----------------------------------------
Write-Step "Writing appsettings.Production.json ..."
$settings = [ordered]@{
    Urls    = ("http://localhost:" + $Port)
    Serilog = [ordered]@{
        MinimumLevel = [ordered]@{
            Default  = "Information"
            Override = [ordered]@{ "Microsoft.AspNetCore" = "Warning" }
        }
    }
    Dicom = [ordered]@{
        LocalAeTitle = $LocalAe
        Source       = [ordered]@{ AeTitle = $SourceAe; Host = $SourceHost; Port = $SourcePort }
        Destination  = [ordered]@{ AeTitle = $DestAe;   Host = $DestHost;   Port = $DestPort }
        Move         = [ordered]@{
            MaxParallelMoves          = $MaxParallelMoves
            DelayBetweenMovesMs       = $DelayBetweenMovesMs
            MoveTimeoutMinutes        = $MoveTimeoutMinutes
            MaxAttempts               = $MaxAttempts
            AssociationTimeoutSeconds = 60
        }
        Verify    = [ordered]@{ Level = $VerifyLevel }
        Inventory = [ordered]@{ FetchSeriesCounts = $false }
    }
    Database = [ordered]@{ Path = "data/migration.db" }
}
$json = $settings | ConvertTo-Json -Depth 8
$prodPath = Join-Path $InstallDir "appsettings.Production.json"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($prodPath, $json, $utf8NoBom)

# --- 4. Initialize and preconfigure the database -----------------------------------------
Write-Step "Initializing database and seeding settings (init-db) ..."
$env:DOTNET_ENVIRONMENT = "Production"
& $exePath init-db
if ($LASTEXITCODE -ne 0) { throw "init-db failed with exit code $LASTEXITCODE" }

# --- 5. Register and start the service ---------------------------------------------------
Write-Step "Registering Windows service ..."
$binPath = '"' + $exePath + '"'
New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $ServiceDisplayName -StartupType Automatic | Out-Null

# Restart on failure (reset count after 1 day; restart after 5s, three times).
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Step "Starting service ..."
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
$svc = Get-Service -Name $ServiceName

Write-Host ""
Write-Step ("Done. Service '" + $ServiceName + "' is " + $svc.Status + ".")
Write-Step ("Dashboard: http://localhost:" + $Port + "/")
Write-Step ("Config and inventory database: " + (Join-Path $InstallDir "data\migration.db"))
Write-Step "Adjust throttle/verify from the dashboard, or re-run this installer to change AE config."
