# Harbor implementation and release report

Date: 2026-11-03. Repository: cfspearn81-code/chatmchosting. Branch: feature/harbor-kubuntu. This is a development implementation, not a Kubuntu-certified release.

## Implemented source

.NET 10.0.100, Avalonia 11.3.9 MVVM desktop with central package pinning and a UI-independent Core. Local XDG data, atomic versioned definitions, per-server cross-process operation and process ownership locking, multiple owned Java processes, real readiness recognition, console input/output and complete disk logs, bounded searchable UI console, command history, measured uptime/resident memory/CPU. Safe duplicate-start rejection and stopped-state administration. Stale PID evidence verifies process start and exact command digest, never signals an unrelated process.

Official-Mojang vanilla metadata and SHA-1-verified staged server downloads; exact metadata-required Java probing; real Fabric launcher API; modern Forge/NeoForge Maven installers, profile compatibility checks and generated Unix argument files. Java PATH/JAVA_HOME/common-location discovery and opt-in Adoptium Linux-x64 runtime download with SHA-256 verification. Fabric launcher API does not publish a checksum; that exception is disclosed rather than pretending verification occurred.

Per-server mod JAR import/inspection/enable/disable/removal; Java-properties parsing and atomic saves preserving comments/unknown properties in the backend; player commands and actual English vanilla list-response parsing; identity-aware stopped whitelist editing without invented UUIDs; world ZIP/directory import/export; full backups with manifests and SHA-256; archive path/symlink/size checks, stopped-state guard and recovery backup before replacement; edit/duplicate/remove services. Data retention is explicit.

UI includes genuine empty states, server cards, selected-server tabs, creation flow, native file pickers, progress/cancel, Java download confirmation, player actions, backup restore confirmation, world import/export, settings and graceful close handling. Packaging supplies a self-contained linux-x64 archive, launcher and per-user desktop entry/icon installer/uninstaller preserving data. CI builds/tests/packages on ubuntu-24.04 (workflow supplied, not claimed executed remotely).

## Observed evidence

- Host: Debian GNU/Linux 13 container, x86-64, OpenJDK 21. No actual Kubuntu or KDE session tested.
- Release solution build succeeds with zero warnings/errors. The initial Avalonia transitive D-Bus advisory was resolved by pinning Tmds.DBus.Protocol 0.21.3.
- Automated tests exercise fixture processes, not Minecraft. Final Release run: 31 passed, 0 failed, 0 skipped, including cross-app guards and shutdown cleanup regression. See TESTING.md for commands.
- CLI Java discovery actually found local Java 21 amd64. Official Minecraft catalog request actually failed HTTP 403 through environment policy.
- Portable self-contained linux-x64 publish and archive/checksum generation succeeded. Final artifact is regenerated after final tests. Per-user install/uninstall scripts passed an isolated XDG directory check, preserving a data sentinel. Native dependency availability on clean Kubuntu remains unverified.
- The self-contained executable was directly launched successfully without invoking the SDK. Real running desktop screenshots are under docs/screenshots. They use a virtual X11 display, not KDE, and contain genuine empty states. No Minecraft session/logs or server data were manufactured for screenshots.
- Feature branch pushed to the requested repository. GitHub PR creation failed with GraphQL HTTP Forbidden. No PR, merge, default-branch modification or completed GitHub Actions run is claimed.
- Environment install/start instructions and required official download hosts saved in configuration draft. Saving does not activate/publish those settings. Review and save environment settings, then publish; retry catalog/download checks afterward.

## Remaining limitations and unverified requirements

1. Real vanilla install/start/readiness/list/stop proof is unrun: official hosts are blocked and the user has not accepted the Minecraft EULA. Run the explicit CLI command in TESTING.md only if you accept it. No Minecraft artifacts bundled.
2. Real Fabric/Forge/NeoForge installations and server launches, managed Temurin download/extraction, upstream API schemas, loader library/authentication traffic, interrupted remote downloads and every historical version combination remain live-unverified. Forge-family support requires modern installers producing unix_args.txt; older legacy launch layouts are rejected. NeoForge catalog is not prefiltered by Minecraft, but installer profile is checked before execution.
3. Kubuntu 24.04/26.04, KDE desktop launcher startup, XWayland session, fractional scaling, keyboard/accessibility, long names/resizing, multi-adapter networking and long-running real hosting require the manual checklist. No native Wayland or ARM64 claim.
4. After an app crash, surviving Java is detected and edits/restart blocked using ownership evidence. Lost stdin cannot be reattached; use trusted external shutdown or clean host shutdown. No daemon, adopted-process force kill or fully automated crash recovery. Normal close waits for graceful exits; a separately confirmed force-stop UI is not implemented.
5. UI properties are currently a raw key/value editor rather than the full typed, labeled editor specified. Backend validates supported values; malformed lines must be corrected. Full Java-properties edge cases and non-ASCII encodings need broader real-server compatibility tests.
6. Mod metadata inspection is partial; dependency/conflict/client-server compatibility cannot be guaranteed. Drag/drop batch import is not implemented (native multiple-file picker is supported). Not every backend deletion/stopped whitelist capability is exposed in UI. Player identities must be genuine; online UUID resolution and stopped operator-file editing are not implemented. Localized/custom list output may be unavailable.
7. Main server-port conflicts are checked, but exhaustive auxiliary query/RCON port collision checks and CPU capacity planning remain incomplete. UI now warns using actual /proc/meminfo with aggregate configured heaps and a 2 GiB reserve, advises leaving capacity and does not promise CPU allocations. Public reachability is never inferred.
8. World import supports root-level Java world ZIPs/directories (level.dat required), not arbitrary archive formats or Bedrock conversion. Full backup/archive cancellation, disk-full and recovery tests cover selected paths, not every filesystem failure mode. Very large filesystem enumerations can still briefly block before streaming work.
9. Screenshots of selected-server overview/mods/worlds and a real Minecraft console are unavailable without legitimate installed server data. Provided screenshots are actual dashboard/onboarding empty states only. No mock screenshots substituted.
10. Dependencies and transitive lockfiles are pinned; SBOM and full license redistribution audit are not complete. Package artifacts are development artifacts; release signing, AppImage and real Kubuntu end-to-end verification are not provided.

Source and binary artifacts are separate: the source archive excludes worlds, downloaded runtimes/JARs, EULA records and build outputs. Consult SHA256SUMS before extracting release artifacts. Source license must be selected by the repository owner before broad redistribution; no ownership claim is made over Minecraft or mod binaries.
