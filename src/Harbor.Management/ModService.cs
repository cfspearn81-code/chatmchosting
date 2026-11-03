using System.IO.Compression;
using System.Text.Json;
using Harbor.Core;

namespace Harbor.Management;

public sealed record ModInfo(string Name, bool Enabled, long SizeBytes, string? Id, string? Version, IReadOnlyList<string> Warnings);
public sealed class ModService(IOperationCoordinator coordinator, IServerLifecycleManager lifecycle)
{
    private readonly StoppedOperations guard = new(coordinator, lifecycle);
    public async Task<IReadOnlyList<ModInfo>> ListAsync(ServerDefinition server, CancellationToken ct = default)
    {
        await using var lease = await coordinator.AcquireAsync(server.Id, ct);
        var directory = Path.Combine(server.Directory, "mods");
        StoppedOperations.RejectLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var results = new List<ModInfo>();
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
            StoppedOperations.RejectLinks(path);
            results.Add(await Task.Run(() => Inspect(path, server.Loader), ct));
        }
        return results;
    }
    public async Task ImportAsync(ServerDefinition server, IEnumerable<string> paths, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(server, ct);
        if (server.Loader == ServerLoader.Vanilla) throw new InvalidOperationException("Vanilla servers cannot load loader-based mods. Install a compatible loader first.");
        var directory = Path.Combine(server.Directory, "mods"); StoppedOperations.RejectLinks(directory); Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var imported = new List<string>();
        try
        {
            foreach (var source in paths)
            {
                ct.ThrowIfCancellationRequested(); StoppedOperations.RejectLinks(source);
                var name = Path.GetFileName(source);
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only actual .jar files can be imported.");
                var destination = StoppedOperations.Child(directory, name);
                if (File.Exists(destination) || File.Exists(destination + ".disabled")) throw new IOException($"A mod named {name} already exists.");
                _ = Inspect(source, server.Loader); // Validate the ZIP container without executing mod code.
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, ct);
            }
            ct.ThrowIfCancellationRequested();
            foreach (var staged in Directory.EnumerateFiles(staging))
            { var destination = Path.Combine(directory, Path.GetFileName(staged)); File.Move(staged, destination); imported.Add(destination); }
        }
        catch { foreach (var path in imported) File.Delete(path); throw; }
        finally { Directory.Delete(staging, true); }
    }
    public async Task SetEnabledAsync(ServerDefinition server, string filename, bool enabled, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(server, ct);
        var directory = Path.Combine(server.Directory, "mods");
        var source = StoppedOperations.Child(directory, filename);
        if (!filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !filename.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not a mod filename.");
        var currentlyEnabled = filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        if (currentlyEnabled == enabled) return;
        File.Move(source, enabled ? source[..^9] : source + ".disabled", false);
    }
    public async Task RemoveAsync(ServerDefinition server, string filename, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit confirmation is required to remove a mod.");
        await using var lease = await guard.EnterAsync(server, ct);
        var path = StoppedOperations.Child(Path.Combine(server.Directory, "mods"), filename);
        if (!filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !filename.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not a mod filename.");
        File.Delete(path);
    }
    private static ModInfo Inspect(string path, ServerLoader loader)
    {
        using var archive = ZipFile.OpenRead(path);
        var warnings = new List<string> { "Compatibility and dependencies cannot be fully verified without launching. Content mods may also be required on every client." };
        string? id = null, version = null;
        var fabric = archive.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            if (fabric.Length > 1024 * 1024) throw new InvalidDataException("Mod metadata is unreasonably large.");
            using var stream = fabric.Open(); using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (loader != ServerLoader.Fabric) warnings.Add("Fabric metadata detected; this server does not use Fabric.");
            if (root.TryGetProperty("environment", out var e) && e.GetString() == "client") warnings.Add("This mod declares itself client-only.");
            if (root.TryGetProperty("depends", out var d)) warnings.Add("Declared dependencies: " + string.Join(", ", d.EnumerateObject().Select(p => $"{p.Name}: {p.Value}")));
        }
        else if (archive.GetEntry("META-INF/neoforge.mods.toml") is not null && loader != ServerLoader.NeoForge) warnings.Add("NeoForge metadata detected; loader mismatch is likely.");
        else if (archive.GetEntry("META-INF/mods.toml") is not null && loader is not (ServerLoader.Forge or ServerLoader.NeoForge)) warnings.Add("Forge metadata detected; loader mismatch is likely.");
        else if (archive.GetEntry("META-INF/mods.toml") is null && archive.GetEntry("META-INF/neoforge.mods.toml") is null) warnings.Add("No recognized loader metadata found; this may be a library or an incompatible JAR.");
        return new(Path.GetFileName(path), !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase), new FileInfo(path).Length, id, version, warnings);
    }
}
