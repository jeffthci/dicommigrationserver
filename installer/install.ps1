<#
.SYNOPSIS
  Installs the DICOM Migration Server as a Windows service, creating and preconfiguring
  the SQLite database with no user intervention.

.DESCRIPTION
  Designed to run on a target server that has NO .NET SDK and no source. It installs from
  pre-published, self-contained binaries shipped alongside this script in an "app" folder
  (produced by build-package.ps1). Building from source is only attempted as a fallback when
  the SDK and project are present (dev-box convenience).

  Steps performed:
    1. Stops and removes any existing instance of the service.
    2. Stages the application binaries into the install directory (from the bundled payload,
       or by publishing from source if an SDK + project are available).
    3. Writes appsettings.Production.json (UTF-8, no BOM) with the supplied DICOM config.
    4. Runs "DicomMigration.exe init-db" to create the schema and seed the settings table.
    5. Registers the Windows service (auto-start) and starts it.

  All configuration is provided via parameters with sensible defaults, so the installer
  runs unattended. Re-running it performs an in-place upgrade.

.EXAMPLE
  # On the target server, after copying the build-package.ps1 output here:
  .\install.ps1 -SourceHost 10.0.0.10 -SourceAe SRC_PACS -DestHost 10.0.0.20 -DestAe DST_PACS
#>

[CmdletBinding()]
param(
    # Under Program Files rather than a folder off the root of C:. A directory created directly
    # under C:\ inherits an inherit-only Modify ACE for Authenticated Users, so any logged-on user
    # could replace the service binary -- and this service runs as LocalSystem with auto-restart,
    # making that a straight local privilege escalation to SYSTEM. The ACL is also hardened
    # explicitly below, so a caller who overrides this path is still protected.
    [string] $InstallDir          = (Join-Path $env:ProgramFiles "DICOM Migration Server"),
    [string] $ServiceName         = "DicomMigration",
    # Overwrite the settings table from the parameters above even when a database already exists.
    # Off by default so a binaries-only upgrade cannot silently repoint the live AE configuration.
    [switch] $ReseedSettings,
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

    # Folder of pre-published binaries to install (contains DicomMigration.exe). Defaults to
    # an "app" folder next to this script. When present, no .NET SDK is needed on this server.
    [string] $PayloadDir,

    # Fallback build-from-source options (only used when no payload is found and an SDK exists).
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

# --- 2. Stage application binaries -------------------------------------------------------
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

# Lock down the install directory regardless of where it was placed. Inheritance is removed and
# an explicit DACL applied: SYSTEM and Administrators get Full, and no entry is granted to Users
# or Authenticated Users. Without this, a directory under a permissive parent lets an unprivileged
# local user overwrite DicomMigration.exe (or a runtime DLL beside it) and have the SCM execute it
# as LocalSystem on the next restart. It also keeps the PHI in data\ off general local read.
Write-Step "Hardening ACL on $InstallDir ..."
& icacls $InstallDir /inheritance:r /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to set ACL on $InstallDir (icacls exit $LASTEXITCODE)." }

# Prefer a pre-published payload shipped next to this script (an "app" folder).
if (-not $PayloadDir) {
    $candidate = Join-Path $scriptDir "app"
    if (Test-Path (Join-Path $candidate "DicomMigration.exe")) { $PayloadDir = $candidate }
}

if ($PayloadDir) {
    if (-not (Test-Path (Join-Path $PayloadDir "DicomMigration.exe"))) {
        throw "Payload directory '$PayloadDir' does not contain DicomMigration.exe."
    }
    Write-Step ("Installing published files from " + $PayloadDir + " ...")
    # /E recurse; no /MIR so an existing data/ and logs/ (and the DB) are preserved on upgrade.
    & robocopy $PayloadDir $InstallDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy of published files failed (robocopy exit $LASTEXITCODE)." }
}
elseif (-not $SkipPublish -and (Test-Path $ProjectPath) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Step ("No payload found; publishing from source " + $ProjectPath + " ...")
    $scArg = "false"
    if ($SelfContained) { $scArg = "true" }
    & dotnet publish $ProjectPath -c Release -r win-x64 --self-contained $scArg -o $InstallDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
else {
    throw ("No application binaries to install. This server has no .NET SDK and no published " +
        "payload was found next to install.ps1. On a machine WITH the .NET SDK, run " +
        "build-package.ps1, copy the resulting package folder (or zip) here, and re-run install.ps1.")
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
# init-db force-reseeds the settings table from the parameters above. On an upgrade that would
# silently repoint the live source/destination AEs back to this script's defaults, so an operator
# re-running the installer just to drop in new binaries could send the next migration to the wrong
# PACS. Skip the reseed when a database already exists unless -ReseedSettings is passed.
$dbPath = Join-Path $InstallDir "data\migration.db"
if ((Test-Path $dbPath) -and -not $ReseedSettings) {
    # Skipping init-db entirely is safe: the service calls Database.Initialize() on every start,
    # so the schema is still created/updated without touching the settings table.
    Write-Step "Existing database found; keeping its settings (pass -ReseedSettings to overwrite)."
}
else {
    Write-Step "Initializing database and seeding settings (init-db) ..."
    $env:DOTNET_ENVIRONMENT = "Production"
    & $exePath init-db
    if ($LASTEXITCODE -ne 0) { throw "init-db failed with exit code $LASTEXITCODE" }
}

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
