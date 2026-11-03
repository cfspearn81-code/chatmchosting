# Dependencies and licenses

Versions are centrally pinned in `Directory.Packages.props`; SDK selection is in `global.json`. NuGet restore must retain normal TLS and package integrity checks.

| Dependency | Version | Role | License |
| --- | --- | --- | --- |
| .NET SDK | 10.0.100, latest patch roll-forward | Build toolchain | MIT and third-party notices |
| .NET runtime | .NET 10 | Included in self-contained publish | MIT and third-party notices |
| Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent | 11.3.9 | Cross-platform desktop controls and Linux backend | MIT |
| Microsoft.NET.Test.Sdk | 17.14.1 | Test discovery/runtime | MIT |
| xUnit | 2.9.3 | Offline unit/regression tests | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Test runner adapter | Apache-2.0 |

The SDK and runtime's upstream license and notices are available at https://github.com/dotnet/runtime and https://github.com/dotnet/sdk. Avalonia notices: https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md. xUnit notices: https://github.com/xunit/xunit. Release packages should retain license/third-party files produced by publish. No icon font is required by Core; the Harbor packaging SVG is project artwork.

## Native Linux prerequisites

A self-contained linux-x64 publish removes the .NET SDK/runtime requirement for end users, but does not remove Linux system library dependencies. Avalonia's X11 backend needs X11, ICE, SM, fontconfig and compatible native rendering/font libraries. The .NET runtime additionally requires standard glibc, libgcc, libstdc++, zlib, OpenSSL and ICU packages. Kubuntu normally supplies many through KDE; verify against the target LTS rather than assuming the build container proves compatibility. See https://docs.avaloniaui.net/docs/get-started/install and https://learn.microsoft.com/dotnet/core/install/linux-ubuntu.

Use available Noto Sans with normal Linux font fallback and an available monospace font for the console. Fonts are not downloaded or bundled by default; system font licenses remain with their providers.

## Runtime downloads and terms

Minecraft server artifacts are fetched from official Mojang metadata during server installation and are not bundled with Harbor. Minecraft's EULA requires explicit user acceptance before first launch: https://www.minecraft.net/eula. No build or automated test may imply acceptance.

Java discovery uses installed executables. Optional managed runtimes use Eclipse Adoptium's official API and published checksum; Temurin/OpenJDK licensing is GPLv2 with Classpath Exception and additional upstream notices. Fabric, Forge and NeoForge installers/libraries remain separate upstream artifacts with their own project and dependency licenses. Consult the selected version's distribution notices; installing a loader does not grant rights to redistribute mods or Minecraft binaries.

## Tested platform versus targets

The requested targets are Kubuntu 24.04 and 26.04 LTS, x86-64. The current build machine identifies itself as Debian GNU/Linux 13 (trixie); successful checks here are not Kubuntu compatibility certification. Record actual SDK, Java, loader and Minecraft versions and all desktop evidence in RELEASE-REPORT.md.
