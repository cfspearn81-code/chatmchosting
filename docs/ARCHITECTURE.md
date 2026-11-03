# Harbor architecture

Harbor is a local, signed-in-user desktop application. The desktop and CLI consume the same backend; Minecraft worlds and Java processes run on the local machine. Downloads require upstream access, but hosting does not require a cloud account. This document describes source boundaries; verification results belong in RELEASE-REPORT.md.

## Projects and dependencies

| Project | Responsibility |
| --- | --- |
| Harbor.Core | Immutable server definitions, lifecycle states, launch plans, typed contracts, validation and XDG root resolution. No UI references. |
| Harbor.Foundation | Atomic JSON persistence, one per-server operation coordinator, verified staged downloads, owned process lifecycle and measurements. |
| Harbor.Installers | Official release metadata, Java executable probing/discovery, runtime and server loader installation. Uses Foundation's downloader. |
| Harbor.Management | Stopped-state mods, Java properties, player commands, world operations and backups. Uses the shared coordinator and lifecycle. |
| Harbor.App | Avalonia desktop, MVVM presentation and composition root. |
| Harbor.Cli | Backend troubleshooting and opt-in real server proof. |
| Harbor.Tests | Offline fixtures and backend regression checks; live Minecraft use requires separate explicit EULA acceptance. |

Services are concrete constructor-injected instances. The composition root must share one `OperationCoordinator` and one lifecycle manager across installers, management and UI. A view must never construct a competing registry or acquire a recursive operation lease.

## Ownership and operation ordering

`IOperationCoordinator.AcquireAsync(serverId)` first obtains a process-local semaphore, then an exclusive file handle in the XDG state directory. A lease is asynchronous-disposable and intentionally nonrecursive. Management obtains the lease before checking that a server is stopped. Callers must not check state and then act without the lease: another request could start the server between those steps.

Operation leases serialize mutations, not the entire running lifetime. The lifecycle manager needs a separate retained ownership handle for the lifetime of each started server to reject another app process starting the same identity. A process handle is authoritative while owned; a bare saved PID must never authorize killing a process. Stale metadata is diagnostic evidence, not permission to terminate arbitrary Java processes. App crash and stale-process recovery behavior must be assessed against the final implementation and release report.

The state vocabulary is `Stopped`, `Installing`, `Starting`, `Running`, `Stopping`, `Failed`. Starting a process does not make Minecraft ready: only recognized actual server readiness output permits `Running`. Shutdown sends `stop` to the owned process and waits for actual exit. Forced termination, where offered, requires separate confirmation and targets an owned process tree. Failure evidence must be shown rather than replaced by simulated logs.

## Launch and download boundaries

`LaunchPlan` keeps executable, argument list, working directory and optional environment separate. Process arguments are passed through `ProcessStartInfo.ArgumentList`; there is no shell command concatenation. Memory is expressed in MiB and is configured heap, not measured resident memory. CPU and RSS measurements must come from the owned process.

Downloads use HTTPS with normal TLS verification, temporary sibling files, optional published SHA-1/SHA-256/SHA-512 verification, and replacement only after completion. Minecraft's official metadata publishes SHA-1 for server artifacts. A missing published hash must be disclosed rather than invented. Installer argument files, loader library layout and Java major compatibility belong to installers, not the UI.

## Persistence and local data

Server definitions are JSON envelopes with `SchemaVersion: 1` and `Server`, one file per stable GUID in the XDG configuration directory's `servers` folder. Writes go to a unique temporary sibling then replace the destination. Unknown schema versions are rejected rather than silently reinterpreted; no earlier schema migration is currently needed. A server definition records user EULA acceptance, but that record and server data must never enter source control.

Default XDG locations append `harbor` to `$XDG_DATA_HOME`, `$XDG_CONFIG_HOME`, `$XDG_CACHE_HOME` and `$XDG_STATE_HOME`; fallbacks are `~/.local/share`, `~/.config`, `~/.cache` and `~/.local/state`. Relative XDG variables are invalid per specification. Server-specific data directories are distinct from program binaries and may contain spaces or Unicode. Backups must not replace stable server identity or active process metadata.

## Trust boundaries

Worlds, archives, mod JAR metadata and downloaded content are untrusted input. Archive extraction must reject traversal, absolute paths and unsafe links and bound extraction size. Mod inspection must never execute mod code. Restores and world replacement require a stopped server, staged data and recovery preservation. Generic console commands are intentional administrative capabilities; structured player controls validate player arguments before emitting commands. Harbor must not silently change firewall, router, authentication or laptop power settings.

## Desktop backend

Avalonia Desktop uses its stable Linux X11 path; a Wayland session can run through XWayland. Merely detecting a Wayland session does not establish use of a native Wayland backend. Real Kubuntu/KDE, fractional scaling and desktop-launcher checks remain distinct from container or virtual-display evidence.

## Backup format

Full-server backups use a ZIP and `harbor-backup-manifest.json` (`SchemaVersion: 1`, `Kind: full-server`, creation timestamp and SHA-256 per file). They include server artifacts/configuration/mods/worlds but exclude logs, nested backups, EULA acceptance, `.harbor*`, lock and PID files. They are local personal recovery data, not redistributable release artifacts. Backups live beside the server root at `.harbor-backups/<server-id>/`.

Restore validates the archive and hashes in a staged sibling, creates a separate recovery backup, preserves current EULA acceptance and replaces the server folder through a recoverable directory move. Extraction limits are 64 GiB and 200,000 files, with a 32 MiB manifest cap. Unsafe entries and symlinks are rejected. Ordinary ZIP checksums do not replace manifest SHA-256 verification. Cancellation is honored during staging; the short directory commit deliberately completes atomically with respect to cancellation.
