using Harbor.Core;

namespace Harbor.Management;

/// <summary>Stopped-only changes to server identity and consistent duplication/deletion.</summary>
public sealed class ServerService(IServerRepository repository, IOperationCoordinator coordinator, IServerLifecycleManager lifecycle)
{
    private readonly StoppedOperations guard = new(coordinator, lifecycle);
    public async Task SaveAsync(ServerDefinition server, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(server, ct);
        ServerValidation.ThrowIfInvalid(server);
        await ValidateUniqueAsync(server, ct);
        await repository.SaveAsync(server, ct);
    }
    public async Task<ServerDefinition> DuplicateAsync(ServerDefinition source, string name, string destinationDirectory, int port, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(source, ct);
        var destination = Path.GetFullPath(destinationDirectory).TrimEnd('/');
        var original = Path.GetFullPath(source.Directory).TrimEnd('/');
        if (destination == original || destination.StartsWith(original + '/', StringComparison.Ordinal) || original.StartsWith(destination + '/', StringComparison.Ordinal))
            throw new ArgumentException("Duplicate destination must be outside the original server directory.");
        StoppedOperations.RejectLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Choose a new, nonexistent destination directory.");
        var duplicate = source with { Id = Guid.NewGuid(), Name = name, Directory = destination, Port = port, EulaAccepted = false, EulaAcceptedAt = null, CreatedAt = DateTimeOffset.UtcNow };
        ServerValidation.ThrowIfInvalid(duplicate); await ValidateUniqueAsync(duplicate, ct);
        await using var newLease = await coordinator.AcquireAsync(duplicate.Id, ct);
        var stage = destination + ".duplicate-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage);
        var moved = false;
        try
        {
            await CopyAsync(original, stage, ct);
            var propertiesPath = Path.Combine(stage, "server.properties");
            var properties = JavaProperties.Parse(File.Exists(propertiesPath) ? await File.ReadAllTextAsync(propertiesPath, ct) : "");
            properties.Set("server-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await StoppedOperations.AtomicWriteAsync(propertiesPath, properties.ToString(), ct);
            ct.ThrowIfCancellationRequested(); Directory.Move(stage, destination); moved = true;
            await repository.SaveAsync(duplicate, ct);
            return duplicate;
        }
        catch { if (moved && Directory.Exists(destination)) Directory.Delete(destination, true); throw; }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public async Task DeleteAsync(ServerDefinition server, bool deleteData, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit confirmation is required to remove a server. Retained data is preserved unless deletion is selected.");
        await using var lease = await guard.EnterAsync(server, ct);
        StoppedOperations.RejectLinks(server.Directory);
        // Rename into a recoverable sibling first. A failed definition deletion restores original data.
        var directory = Path.GetFullPath(server.Directory).TrimEnd('/');
        if (directory == Path.GetPathRoot(directory)) throw new ArgumentException("Cannot delete a filesystem root.");
        var tombstone = directory + ".deleted-" + Guid.NewGuid().ToString("N");
        var moved = false;
        if (deleteData && Directory.Exists(directory))
        { ValidateTree(directory); ct.ThrowIfCancellationRequested(); Directory.Move(directory, tombstone); moved = true; }
        try { await repository.DeleteAsync(server.Id, ct); }
        catch { if (moved) Directory.Move(tombstone, directory); throw; }
        if (moved) Directory.Delete(tombstone, true);
    }
    private async Task ValidateUniqueAsync(ServerDefinition server, CancellationToken ct)
    {
        var others = await repository.ListAsync(ct);
        if (others.Any(s => s.Id != server.Id && s.Port == server.Port)) throw new ArgumentException("Another server already uses this port.");
        var directory = Path.GetFullPath(server.Directory).TrimEnd('/');
        if (others.Any(s => s.Id != server.Id && (Path.GetFullPath(s.Directory).TrimEnd('/') == directory || directory.StartsWith(Path.GetFullPath(s.Directory).TrimEnd('/') + '/', StringComparison.Ordinal) || Path.GetFullPath(s.Directory).StartsWith(directory + '/', StringComparison.Ordinal))))
            throw new ArgumentException("Servers must have separate, non-overlapping data directories.");
    }
    private static void ValidateTree(string directory)
    {
        StoppedOperations.RejectLinks(directory);
        foreach (var item in Directory.EnumerateFileSystemEntries(directory))
        { StoppedOperations.RejectLinks(item); if (Directory.Exists(item)) ValidateTree(item); }
    }
    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        StoppedOperations.RejectLinks(source);
        foreach (var item in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested(); StoppedOperations.RejectLinks(item);
            var name = Path.GetFileName(item);
            if (name is "logs" or "backups" or "eula.txt" || name.StartsWith(".harbor", StringComparison.Ordinal) || name.EndsWith(".lock", StringComparison.Ordinal) || name.EndsWith(".pid", StringComparison.Ordinal)) continue;
            var target = Path.Combine(destination, name);
            if (Directory.Exists(item)) { Directory.CreateDirectory(target); await CopyAsync(item, target, ct); }
            else
            {
                await using var input = File.OpenRead(item); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); await input.CopyToAsync(output, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(item));
            }
        }
    }
}
