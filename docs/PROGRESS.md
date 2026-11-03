# Progress

Repository: cfspearn81-code/chatmchosting; branch feature/harbor-kubuntu.

Completed: solution/toolchain and shared contracts; local lifecycle/persistence/locking; official-source staged installers and Java management; stopped-state management and safe archives; connected Avalonia desktop; CLI and automated regression suite; packaging/CI/documentation; actual X11 virtual-display capture.

Verified: .NET 10 Release solution build zero warnings/errors, real local Java21 discovery, unit/integration fixtures (not Minecraft), portable self-contained publish, pushed branch. Final Release test run: 31 passed, 0 failed, 0 skipped. The suite exposed a shutdown cleanup race; fixed and strict regression passed. Commands are in TESTING.md / RELEASE-REPORT.md.

Blocked: official game/loader metadata HTTP403 until saved network settings are applied; genuine live server proof also needs deliberate user EULA acceptance. No Kubuntu desktop available. PR API forbidden.

Next concrete task: review/save and publish the environment configuration, retry `dotnet run --project tools/Harbor.Cli -- versions`, then perform explicit opt-in vanilla proof with compatible Java. Exercise every loader and the Kubuntu checklist before treating the portable artifact as a verified end-user release. Implement remaining typed properties UI, force-stop confirmation and exhaustive resource/auxiliary-port validation. Full limitations are recorded in RELEASE-REPORT.md.

Do not replace actual validation with design fixtures or accept the EULA automatically.
