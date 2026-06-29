# DICOM Migration Server

A controller service that migrates studies between two DICOM nodes (PACS) using **C-MOVE**, with a SQLite inventory and a live web dashboard.

It does **not** receive pixel data itself. It:

1. **Inventories** the source PACS with a STUDY-level **C-FIND** into SQLite.
2. **Migrates** each study by issuing a **C-MOVE-RQ** to the source, naming the destination AE. The source performs the actual C-STORE.
3. **Verifies** each study by **C-FIND**ing the destination and comparing instance/series counts against the source.

## Prerequisites — PACS configuration

C-MOVE is a three-party operation, so the network trust has to be set up on the PACS side first:

| Node | Must be configured with |
|------|------------------------|
| **Source PACS** | This server's AE Title (`LocalAeTitle`) allowed for C-FIND **and** C-MOVE. **Plus** the destination AE registered with its IP/port so the source knows where to C-STORE. |
| **Destination PACS** | This server's AE Title allowed for C-FIND (verification). **Plus** it must accept C-STORE associations from the **source** PACS. |

If the destination AE is not registered on the source, every C-MOVE returns a "Move Destination unknown" failure.

## Configuration

Operational configuration lives in the SQLite database (a `settings` table), not just in files. `appsettings.json` provides **bootstrap defaults**; on first run (or via the installer's `init-db` step) those values are seeded into the database, which then becomes the source of truth.

Everything can be edited **live from the dashboard** (or `POST /api/settings`) without a restart:
- **Connections** panel — the migration server's AE Title, and the source/destination AE Title + Host + Port. (The server is a C-MOVE SCU and never listens, so it has no DICOM port of its own.) Avoid changing these mid-migration.
- **Throttle** panel — parallelism, inter-move delay, and verify level (see [Throttling](#throttling-the-migration)).

The only file-only settings are the dashboard `Urls` (web port) and the database path, which need a service restart.

`appsettings.json` (defaults / bootstrap):

```jsonc
"Dicom": {
  "LocalAeTitle": "MIGRATE",
  "Source":      { "AeTitle": "SOURCE_PACS", "Host": "10.0.0.10", "Port": 104 },
  "Destination": { "AeTitle": "DEST_PACS",   "Host": "10.0.0.20", "Port": 104 },
  "Move": {
    "MaxParallelMoves": 1,        // many PACS allow only one C-MOVE association at a time
    "MoveTimeoutMinutes": 120,
    "MaxAttempts": 2,             // failed studies are retried up to this many times
    "DelayBetweenMovesMs": 0
  },
  "Verify": { "Level": "Study" }, // Study | Series | Instance
  "Inventory": { "FetchSeriesCounts": false }
}
```

**Verify levels:**
- `Study` — compare `NumberOfStudyRelatedInstances` (fast; relies on the PACS populating it).
- `Series` — compare series counts.
- `Instance` — IMAGE-level C-FIND on both sides and compare the actual SOP Instance UID counts (definitive, slowest).

## Run

```powershell
cd src/DicomMigration
dotnet run
```

Open the dashboard at the URL printed on startup (e.g. `http://localhost:5000`). Use the three phase buttons in order, or drive the API directly:

| Method | Route | Purpose |
|--------|-------|---------|
| `GET`  | `/api/status` | Current phase progress + status counts |
| `GET`  | `/api/studies?status=&limit=&offset=` | Inventory rows |
| `GET`  | `/api/events?limit=` | Recent event log |
| `GET`  | `/api/config` | Effective AE/endpoint config |
| `GET`  | `/api/settings` | Full settings snapshot (structured) |
| `POST` | `/api/settings` | Update settings live, e.g. `{"Move.DelayBetweenMovesMs":"500"}` |
| `POST` | `/api/inventory` | Start inventory (optional JSON `InventoryFilter` body) |
| `POST` | `/api/migrate` | Start C-MOVE of inventoried + retryable studies |
| `POST` | `/api/retry` | Re-run C-MOVE on **all** `move_failed` studies (resets the attempt cap) |
| `POST` | `/api/verify` | Verify all moved studies |
| `POST` | `/api/cancel` | Cancel the running phase |

Inventory filter body (all optional):

```json
{ "patientId": "", "studyDateRange": "20200101-20231231", "accessionNumber": "", "modalitiesInStudy": "CT" }
```

## Retrying failures

Two ways failures get re-attempted:

- **Automatic** — `POST /api/migrate` re-picks `move_failed` studies whose `attempts` are still under `Move.MaxAttempts`.
- **Manual "Retry Failed" button** (`POST /api/retry`) — re-runs C-MOVE on **every** `move_failed` study and **resets the attempt counter first**, so studies that already hit the cap get a fresh set of tries. Useful after you've fixed the underlying cause (e.g. registered the destination AE on the source).

## Throttling the migration

C-MOVE pushes can be slow, and many PACS object to being hammered. Two live knobs control pace:

- **Parallel moves** (`Move.MaxParallelMoves`) — concurrent C-MOVE associations. Many PACS allow only one; start at 1.
- **Delay between moves** (`Move.DelayBetweenMovesMs`) — pause after each study completes.

Both are read **live** by the orchestrator: change them in the dashboard's throttle panel (or `POST /api/settings`) and the running migration adjusts on the next study. (Parallelism changes apply to the next migrate/retry run; the delay applies immediately.)

## Install as a Windows service

The target server needs **neither the .NET SDK nor the .NET runtime** — you ship a self-contained build. Two deployment options:

- **MSI** (below) — best for rolling out to multiple machines (double-click, `msiexec /qn`, GPO/SCCM).
- **Script package** ([further down](#script-package-alternative)) — a zipped folder + `install.ps1`.

### MSI (recommended for fleet deployment)

**Build the MSI once** on a machine with the .NET SDK and the WiX v5 toolset:

```powershell
# one-time WiX setup (MIT-licensed v5; v6/v7 require a paid maintenance-fee EULA)
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2

cd installer
.\build-msi.ps1 -Version 1.0.0.0     # -> dist\DicomMigration.msi
```

**Deploy `dist\DicomMigration.msi`** to any number of machines:

```powershell
# interactive
DicomMigration.msi

# silent, default port 5080
msiexec /i DicomMigration.msi /qn

# silent, custom dashboard port
msiexec /i DicomMigration.msi /qn WEBPORT=5090

# uninstall
msiexec /x DicomMigration.msi /qn
```

The MSI installs to `C:\Program Files\DICOM Migration Server`, registers the `DicomMigration` service (auto-start, LocalSystem, restart-on-failure), and starts it. On first start the app creates the SQLite schema and seeds settings. Then browse to `http://localhost:<port>/` and set the source/destination/local **AE titles, hosts, and ports from the dashboard**. Re-running a newer MSI performs a clean in-place upgrade (bump `-Version`); the database under the install folder is preserved.

### Script package (alternative)

Prefer a plain zip + script? Build it on a machine with the SDK:

#### 1. Build the package (on a machine with the .NET SDK)

From the repo's `installer` folder:

```powershell
.\build-package.ps1 -Zip
```

This publishes a self-contained build and assembles `dist\DicomMigration\` containing `app\` (the binaries), `install.ps1`, and `uninstall.ps1` — plus `dist\DicomMigration.zip`.

#### 2. Install on the target server (elevated PowerShell)

Copy the package folder (or the zip) to the server, extract it, then run:

```powershell
.\install.ps1 `
  -LocalAe MIGRATE `
  -SourceAe SRC_PACS -SourceHost 10.0.0.10 -SourcePort 104 `
  -DestAe   DST_PACS -DestHost   10.0.0.20 -DestPort   104 `
  -Port 5080
```

What `install.ps1` does:

1. Stops/removes any existing service instance (waits for the process to exit and the SCM to drop it, so re-running is a safe in-place upgrade).
2. Installs the binaries from the bundled `app\` folder into `C:\Services\DicomMigration` (no SDK/build on the server). If no payload is present but the SDK + source are, it falls back to `dotnet publish` — handy for a dev-box install.
3. Writes `appsettings.Production.json` (UTF-8, no BOM) from the parameters.
4. Runs `DicomMigration.exe init-db` — creates the schema **and seeds the `settings` table** from that config.
5. Registers the `DicomMigration` service (auto-start, restart-on-failure) and starts it.

Then browse to `http://localhost:5080/`. After install, change AE config by re-running the installer; change throttle/verify live from the dashboard.

Uninstall (preserves the database unless `-RemoveFiles`):

```powershell
.\uninstall.ps1            # stop + remove service, keep data
.\uninstall.ps1 -RemoveFiles
```

The SQLite database lives at `Database:Path` (default `data/migration.db`); under the service it resolves to the install directory, and logs roll daily under `logs/`.

## State model

```
inventoried -> move_pending -> moved -------> verified
                            \-> move_failed     \-> mismatch
                                                \-> verify_failed
```

Re-running a phase is safe: inventory upserts (existing migration state is preserved), migrate picks up `inventoried` plus retryable `move_failed` studies, and verify re-checks `moved` studies.
