# Server data operations

Harbor.Management uses the same operation coordinator and lifecycle manager as installation and launching. Mutation checks happen after the exclusive server operation lease is held. Foundation reports ownership by other Harbor processes as running, so stopped-only edits cannot race a start through a second window. A failed process with no owner is editable.

## Properties and server definitions

`PropertiesService` reads and writes stopped servers only. `JavaProperties` supports escaped separators, keys, Unicode escapes and continuation lines, preserves untouched logical lines, comments and unknown properties, and validates the supported numeric, boolean, world-name and enum settings. Saving writes a temporary file followed by replacement. The generic editor rejects changing `server-port` to a different value than the saved definition.

`ServerService.SaveAsync` serializes the server catalog across processes, checks unique ports and nonoverlapping data directories, and rejects changing the directory, Minecraft version or loader of an existing definition. Such changes need a separately implemented reinstall or migration. A port change updates server.properties with rollback if saving the definition fails. The lifecycle validates actual listeners when starting; availability at edit time cannot guarantee a future bind succeeds.

Duplication requires stopped data, creates a new stable ID, requires a nonconflicting port and new directory, copies files through a staging directory, changes the copied server port, and resets explicit EULA acceptance. Deletion requires confirmation and supports retaining data. When deleting data, the directory is renamed to a recoverable sibling before removing the definition; failure to remove the definition restores its directory. External backups remain on disk.

## Mods

Batch JAR imports validate ZIP containers without executing code, stage the batch, refuse filename collisions, and rollback moved files on failure. Enabling/disabling renames `.jar` to/from `.jar.disabled`. Mutations and confirmed deletion require a stopped server. Vanilla imports are rejected. Inspection surfaces Fabric IDs, versions, dependencies and client-only declarations and recognizable Forge/NeoForge metadata. This does not prove dependency completeness or compatibility. Metadata-less libraries and unrecognized formats are labeled uncertain.

## Backups

A full backup contains server files, worlds, dimensions, mods, libraries and configuration. It excludes logs, eula.txt, app-internal `.harbor*` data, lock/PID files and nested backups. Java runtimes managed outside the server directory and the application definition catalog are not included. The ZIP's `harbor-backup-manifest.json` uses schema version 1, kind `full-server`, UTC creation time and a path-to-SHA256 map for every payload file. SHA256 detects corruption, not authenticity; local backups are not signed.

Archives live beside the server directory under `.harbor-backups/<server-id-without-hyphens>`. Restoring requires confirmation and stopped state, verifies all paths, file lengths and checksums into staging, and creates another full recovery backup before replacing the directory. Current EULA consent is preserved and the saved server port is reapplied. Server identity and process ownership stay outside the replaced directory. Failed verification leaves original files untouched. Cancellation before the final rename leaves originals untouched; the brief rename commit is deliberately noncancellable. Backup deletion is confirmed and limited to this server's backup directory.

ZIP extraction rejects absolute/traversal paths, duplicate paths, symbolic links and special Unix file types. It is limited to 64 GiB and 200,000 entries, verifies actual extracted lengths and checks available filesystem capacity. Symbolic links anywhere in managed source paths are refused. A user-selected source must be trusted against concurrent modification by unrelated programs; the lock coordinates Harbor, not external tools editing worlds while backup is underway.

## Worlds and players

World exports contain the configured Java world directory and all its dimensions. They differ from full server backups. Import accepts a directory or ZIP with level.dat directly at its root; it does not convert Bedrock worlds. A stopped server and confirmation are required. Replacement stages data and exports the previous world to the external backup directory's `world-exports` subdirectory before committing. World-export ZIPs have ordinary ZIP integrity rather than the full-backup SHA256 manifest. Harbor does not parse or prove the semantic correctness of NBT world contents.

Whitelist file editing requires stopped state and a genuine supplied UUID; Harbor never fabricates offline identities. Running whitelist, operator and kick operations send actual validated console commands. Operator changes and kicks require confirmation. Operator file editing and automatic online UUID resolution are not implemented. Connected-player reads send `list` and parse recognized English output; unavailable or modified/localized responses remain unavailable rather than reporting zero. Listing whitelist entries does not establish connected players.

Networking information enumerates currently up local interfaces, flags common VPN-like adapters and supplies plain local/LAN guidance. Addresses are observations, not public reachability tests. Harbor changes no firewall, router or laptop power settings.
