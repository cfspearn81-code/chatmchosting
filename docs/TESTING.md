# Verification and Kubuntu acceptance checklist

Run commands from the repository root. The SDK is pinned by `global.json`. Normal application and CLI operation must run as your user, without sudo. The current build host is Debian 13 x86-64, not Kubuntu; a virtual display does not establish KDE compatibility.

## Automated backend checks

```sh
dotnet restore Harbor.sln
dotnet build Harbor.sln -c Release --no-restore -m:1
dotnet test tests/Harbor.Tests/Harbor.Tests.csproj -c Release --no-build --no-restore -m:1
dotnet run --project tools/Harbor.Cli -- --help
```

For a read-only-home cloud machine, point SDK state and NuGet packages to writable locations before running those commands:

```sh
export DOTNET_CLI_HOME=/workspace/.dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
export PATH=/workspace/.dotnet:$PATH
```

Tests create temporary data with spaces and Unicode filenames, staged fake HTTP downloads, synthetic Java executables, and real `/bin/sh` process fixtures. Those fixtures exercise ownership, console pipes and graceful exits; they do **not** establish that Minecraft or a loader launches. Tests never download Minecraft or accept its EULA. Crash evidence tests start a test-owned `/bin/sleep`, write matching PID/start-time/command evidence, assert mutation/restart is blocked, and terminate only that test-owned process.

Coverage includes properties parsing and escaping; persisted identity/schema handling; checksums; cancellation; concurrent process isolation; duplicate starts and bound ports; EULA rejection; backup manifests, corruption, traversal and symlinks; stopped mod mutation; server duplication/edit/delete; and world recovery exports. Read the actual runner summary for current passed/failed/skipped counts; the release report records the delivery run.

## Real vanilla proof — optional and explicit

This requires network access to official Mojang metadata and artifact hosts, and a working Java executable matching the release metadata. Harbor does not bundle Java or Minecraft. Use a fresh data directory and a free port. Inspect the EULA at <https://www.minecraft.net/eula> yourself first. Do not run the acceptance command unless you agree to it.

```sh
dotnet run --project tools/Harbor.Cli -- java
dotnet run --project tools/Harbor.Cli -- java /usr/lib/jvm/java-21-openjdk-amd64/bin/java
dotnet run --project tools/Harbor.Cli -- versions
```

The following downloads and verifies a genuine vanilla 1.21.1 artifact, persists a definition, and does not accept the EULA or start the server:

```sh
dotnet run --project tools/Harbor.Cli -- install --version 1.21.1 --java /usr/lib/jvm/java-21-openjdk-amd64/bin/java --directory "$HOME/.local/share/harbor/proof-install" --port 25575
```

After explicit acceptance, use a **different fresh directory** for the complete proof:

```sh
dotnet run --project tools/Harbor.Cli -- proof --version 1.21.1 --java /usr/lib/jvm/java-21-openjdk-amd64/bin/java --directory "$HOME/.local/share/harbor/proof-live" --port 25576 --accept-eula
```

Successful proof must show real server output, transition from Starting to Running after Minecraft's `Done` message, an actual response to `list`, then graceful exit and persisted definition. Server output is saved in that instance's `logs/harbor-*.log`. Failures return a nonzero exit status. Keep raw output as evidence; never substitute fixture output. No proof has been run on the build host because the user has not accepted the EULA.

```sh
dotnet run --project tools/Harbor.Cli -- list
# Substitute the actual persisted ID returned by list:
dotnet run --project tools/Harbor.Cli -- run --id YOUR-SERVER-GUID
```

Interactive `run` sends real console commands; `stop` or EOF requests graceful shutdown. A definition created by `install` without acceptance must first go through the desktop EULA acceptance flow. Do not edit EULA records to bypass acceptance.

## Kubuntu manual release gate

Perform separately on Kubuntu 24.04 LTS and 26.04 LTS x86-64. Record OS version (`cat /etc/os-release`), architecture (`uname -m`), session (`echo "$XDG_SESSION_TYPE"`), Java output, Minecraft and loader version, Harbor commit, Avalonia version and backend. An XWayland application in a Wayland session is not a native Wayland application. Native Wayland is not claimed.

- Run the published launcher without the SDK and without administrator privileges. Test the per-user desktop entry and removal while confirming worlds remain intact.
- Test Plasma X11 and Plasma Wayland through XWayland, keyboard navigation and visible focus, resizing, long names, clipboard/file picker, and 100%, 125%, 150%, 200% display scaling. Verify all tabs remain usable on a laptop screen.
- Create an empty-state screenshot, then explicitly accept the EULA and install a genuine vanilla server. Capture dashboard, overview and console after real readiness and a real command response. Capture Mods and Worlds & Backups using genuine data or honest empty states.
- Verify localhost connection with a matching Minecraft Java client, then a separate LAN client. Record firewall configuration; do not infer public reachability. Confirm online authentication remains enabled.
- Start two servers with distinct ports; verify worlds, logs, commands and stop actions stay isolated. Attempt duplicate starts in a second Harbor process, configured port collisions and an already-bound port. Verify useful errors and safe recovery.
- Exercise cancellation during downloads, wrong Java, unavailable upstreams, checksum failure fixtures, missing loader argument files, invalid memory values, and corrupt archives. Confirm original files remain unchanged after failed restore.
- Exercise Fabric, Forge and NeoForge individually using exact official catalog versions and the required Java. Preserve installer logs, Unix argument files and genuine ready/command/stop evidence. These paths are unverified until each actual launch passes.
- Import real mods using the native picker; test enable/disable/removal while stopped, reject edits while running, and verify detected metadata warnings. Automatic compatibility detection is limited.
- Edit labeled server properties and confirm they survive restart. Whitelist/operator actions need real commands and verified UUIDs; do not fabricate identities or conflate a whitelist with connected players.
- Export/import a recognizable Java world including Nether/End or modded dimensions. Create, corrupt and restore backups; verify recovery archives, confirmation, free-space errors, traversal/symlink rejection and cancellation.
- Close Harbor with running servers, cancel closure, then request graceful stop and quit. Verify no owned process remains. Test a crash and verify surviving-process evidence blocks unsafe restart/data edits. Do not kill arbitrary Java PIDs.
- Explain that suspend, poweroff, logout or lid policies can interrupt hosting; Harbor does not alter power management or firewall/router rules.

Unexecuted items remain release limitations, even if a related button or service exists. See `RELEASE-REPORT.md` for the actual delivery status.
