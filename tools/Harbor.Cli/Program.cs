using Harbor.Core;
using Harbor.Foundation;
using Harbor.Installers;

namespace Harbor.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("Harbor backend verification harness\n  java [executable]\n  versions\n  list\n  install --version VERSION --java EXECUTABLE --directory PATH [--port 25565]\n  proof --version VERSION --java EXECUTABLE --directory PATH --accept-eula [--port 25565]\n  run --id GUID\n\nInstall downloads genuine Mojang artifacts and verifies the published SHA-1.\nProof explicitly accepts https://www.minecraft.net/eula, installs vanilla, waits for real readiness,\nsends list, waits for the actual response, then stops gracefully. Never run proof unless you accept the EULA.\nRun reads a persisted definition; console commands go to its owned process; EOF stops it.\nAll worlds and logs remain in the selected local directory. Data is never uploaded.");
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try { return await ExecuteAsync(args, cancellation.Token); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operation canceled or timed out. Inspect logs before retrying."); return 2; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    private static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var coordinator = new OperationCoordinator();
        var repository = new ServerRepository();
        var installer = new ServerInstaller(new VerifiedDownloader(), coordinator);
        if (args[0] == "java")
        {
            var discovery = new JavaDiscovery();
            var runtimes = args.Length > 1 ? new[] { await discovery.ProbeAsync(args[1], ct) } : await discovery.DiscoverAsync(cancellationToken: ct);
            foreach (var runtime in runtimes) Console.WriteLine($"Java {runtime.MajorVersion} {runtime.Architecture}: {runtime.Executable}");
            return runtimes.Count == 0 ? 1 : 0;
        }
        if (args[0] == "versions")
        {
            foreach (var version in await installer.GetReleasesAsync(ct)) Console.WriteLine($"{version.Id}\t{version.ReleaseTime:O}");
            return 0;
        }
        if (args[0] == "list")
        {
            foreach (var server in await repository.ListAsync(ct)) Console.WriteLine($"{server.Id}\t{server.Name}\t{server.MinecraftVersion}\t{server.Directory}");
            return 0;
        }
        if (args[0] is not ("install" or "proof" or "run")) throw new ArgumentException("Unknown command; use --help.");
        var proof = args[0] == "proof";
        if (proof && !args.Contains("--accept-eula", StringComparer.Ordinal)) throw new ArgumentException("Proof requires your explicit --accept-eula acceptance of https://www.minecraft.net/eula. No acceptance has been recorded.");
        ServerDefinition definition;
        LaunchPlan plan;
        if (args[0] == "run")
        {
            var id = Guid.Parse(Required(args, "--id"));
            definition = (await repository.ListAsync(ct)).Single(s => s.Id == id);
            if (!definition.EulaAccepted) throw new InvalidOperationException("This server has no recorded EULA acceptance. Use the desktop acceptance flow before running.");
            plan = ServerInstaller.CreateLaunchPlan(definition);
        }
        else
        {
            definition = new ServerDefinition
            {
                Name = "CLI vanilla server", MinecraftVersion = Required(args, "--version"),
                JavaPath = Required(args, "--java"), Directory = Path.GetFullPath(Required(args, "--directory")),
                Port = int.Parse(Optional(args, "--port") ?? "25565"), EulaAccepted = proof,
                EulaAcceptedAt = proof ? DateTimeOffset.UtcNow : null
            };
            ServerValidation.ThrowIfInvalid(definition);
            var installed = await installer.InstallAsync(new InstallRequest(definition), cancellationToken: ct);
            definition = installed.Server; plan = installed.LaunchPlan;
            await repository.SaveAsync(definition, ct);
            Console.WriteLine($"Installed and persisted {definition.Id} in {definition.Directory}");
            if (!proof) return 0;
        }
        await using var lifecycle = new ServerLifecycleManager(coordinator);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifecycle.StatusChanged += (_, status) =>
        {
            Console.WriteLine($"State: {status.State} {status.Error}");
            if (status.State == ServerState.Running) ready.TrySetResult();
            if (status.State == ServerState.Failed) ready.TrySetException(new IOException(status.Error));
        };
        var waitingForList = false;
        lifecycle.OutputReceived += (_, line) =>
        {
            Console.WriteLine($"{line.Timestamp:O} {(line.IsError ? "stderr" : "stdout")} {line.Text}");
            if (waitingForList && line.Text.Contains("players online", StringComparison.OrdinalIgnoreCase)) response.TrySetResult();
        };
        try
        {
            await lifecycle.StartAsync(definition, plan, ct);
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);
            if (proof)
            {
                waitingForList = true;
                await lifecycle.SendCommandAsync(definition.Id, "list", ct);
                await response.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                Console.WriteLine("Verified real readiness and list response. Stopping gracefully.");
            }
            else
            {
                Console.WriteLine("Enter Minecraft commands; EOF stops the server.");
                while (await Console.In.ReadLineAsync(ct) is { } command)
                {
                    if (command == "stop") break;
                    await lifecycle.SendCommandAsync(definition.Id, command, ct);
                }
            }
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await lifecycle.StopAsync(definition.Id, shutdown.Token);
        }
        if (lifecycle.GetStatus(definition.Id).State != ServerState.Stopped) throw new IOException("Server did not exit successfully; inspect persisted logs.");
        Console.WriteLine("Verified graceful exit; definition remains persisted for next launch.");
        return 0;
    }
    private static string Required(string[] args, string key) => Optional(args, key) ?? throw new ArgumentException($"Missing {key}.");
    private static string? Optional(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
