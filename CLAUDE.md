# dicom-migration-server — Claude Code context

## Workspace memory

All THCI project knowledge lives in the **workspace-memory** repo (`jeffthci/workspace-memory`; Windows
`C:\Users\jbrady\Documents\GitHub\workspace-memory`, Linux box `/srv/projects/workspace-memory`). This project's knowledge
file there is **`projects/dicom-migration-server.md`**.
- Read it, and workspace-memory's `CLAUDE.md`, at the start of a session (clone the repo next to this one if it is missing).
- Update it in the same step as every meaningful change here (a deploy, a host or URL, a decision, a feature, an open issue),
  then commit and push both repos (fetch and rebase first).
- Keep this repo's `handover.md` current during the session. No secrets, no PHI.

This file is auto-loaded for Claude Code sessions in this repo. Cross-project
context (company info, glossary, project index) is auto-loaded from
`../workspace-memory/` via the parent `GitHub/CLAUDE.md`, so it is not repeated
here. Keep this file for **this project's** memory: what it is, how to run it,
and durable gotchas/decisions worth remembering.

## What this is

Controller service that migrates studies between two DICOM nodes (PACS) using
**C-MOVE**. It inventories the source with a STUDY-level **C-FIND** into SQLite,
then issues a **C-MOVE-RQ** per study (the source performs the actual C-STORE to
the destination AE). Includes a live web dashboard. It does **not** receive pixel
data itself. Repo: https://github.com/jeffthci/dicommigrationserver.git

## Deployment

Not live anywhere yet (as of 2026-08-07). Two supported targets, one codebase:

- **Windows** — WiX v5 MSI or `installer\build-package.ps1` + `install.ps1`; installs to
  `%ProgramFiles%\DICOM Migration Server` (or wherever an existing service already lives) as the
  `DicomMigration` service. Re-running upgrades in place and PRESERVES stored settings; pass
  `-ReseedSettings` to overwrite them from parameters.
- **Linux (the intended target)** — `installer\linux\build-tarball.ps1` cross-publishes
  `linux-x64` from the Windows dev box; `install.sh` on the server installs to
  `/opt/dicom-migration` as the `dicom-migration` systemd unit.

`Program.cs` calls both `UseWindowsService()` and `UseSystemd()`; each no-ops off its own
platform, so do not fork the entry point per OS.

## Notes

- **The unit must stay `Type=notify`.** `UseSystemd()` signals readiness via `sd_notify`.
  Switching to `Type=simple` makes systemd report the unit active before Kestrel is listening,
  which hides a failed start.
- **`.gitattributes` pins `*.sh` and `*.service` to LF.** This box has `core.autocrlf=true`;
  without the pin those files check out with CRLF and fail on Linux (`bad interpreter:
  /bin/bash^M`). Do not remove it.
- **The dashboard has no authentication** and its POST endpoints start migrations against live
  PACS. The Linux installer binds `127.0.0.1` by default for that reason; reach it via SSH
  tunnel rather than widening the bind.
- **Disk is not a constraint.** The server never receives pixel data (C-MOVE is three-party —
  the source C-STOREs straight to the destination). Only binaries (~99 MB), the SQLite
  inventory, and logs land on disk; 40 GB is generous.
- **Moving the host is expensive, not the OS.** The source PACS must have this server's AE
  *and IP* registered for C-FIND and C-MOVE, plus the destination AE registered as a move
  destination. Pick the host before registering.
- Add source/destination AE config, run commands, and migration gotchas here.
