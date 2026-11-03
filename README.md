# Harbor

A local Minecraft Java server manager for Linux, written in C#/.NET 10 and Avalonia. All server data and Java processes stay on your computer. No hosting account is required.

## Build and run

Install the .NET SDK pinned by `global.json`, then:

```sh
dotnet restore Harbor.sln --locked-mode
dotnet build Harbor.sln -c Release
dotnet test Harbor.sln -c Release
dotnet run --project src/Harbor.App
```

Harbor targets linux-x64 Kubuntu 24.04/26.04. The initial build environment is Debian 13 x86-64, not a verified Kubuntu desktop. Avalonia uses its stable X11 backend, including XWayland in a Wayland session; native Wayland is not claimed. Linux desktop libraries include libX11, libICE, libSM, fontconfig, libxcb, libXext, libXi, libXrandr, libXcursor, libXrender and libGL. Install Java appropriate to your chosen Minecraft version or select a compatible runtime. Never run Harbor with sudo.

Data defaults to `$XDG_DATA_HOME/harbor` (otherwise `~/.local/share/harbor`). Configuration/cache/state follow XDG locations. Keep program installation and server data separate. Minecraft downloads are not redistributed. EULA acceptance is a deliberate user action at https://aka.ms/MinecraftEULA.

## Package and install

```sh
bash packaging/publish.sh
mkdir -p /tmp/harbor-release
tar -xzf artifacts/harbor-linux-x64.tar.gz -C /tmp/harbor-release
/tmp/harbor-release/harbor-linux-x64/install-user.sh
```

The archive includes the .NET runtime, so the SDK is only needed to build. The per-user installer requires Bash and Python 3; it installs a desktop launcher and icon. Close Harbor and stop all servers before updating. Extract the next archive and run its installer again. `uninstall-user.sh` removes application files and preserves server data. No firewall, router, authentication or power settings are changed.

See [architecture](docs/ARCHITECTURE.md), [progress](docs/PROGRESS.md), and [release report](docs/RELEASE-REPORT.md) for exact supported behavior and verification limits.

## Hosting safely

Connect locally using `localhost:port`; LAN clients use your laptop's LAN address. LAN connectivity does not prove public reachability. Internet hosting needs deliberate firewall/router configuration and may be blocked by CGNAT. IPv6 exposure also needs firewall review. Keep Minecraft online authentication enabled. Suspending, powering off or closing a lid configured to suspend interrupts hosting. Harbor does not disable sleep.

Mods require a matching loader and may require matching client installations and dependencies. Automated inspection cannot establish every mod's compatibility. Ordinary Java world imports do not convert Bedrock worlds. Stop a server before backups or data changes; retain independent backups before upgrades.
