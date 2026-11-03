using Harbor.Core;

namespace Harbor.Management;

internal sealed class StoppedOperations(IOperationCoordinator coordinator, IServerLifecycleManager lifecycle)
{
    public async Task<IAsyncDisposable> EnterAsync(ServerDefinition server, CancellationToken ct)
    {
        var lease = await coordinator.AcquireAsync(server.Id, ct);
        if (lifecycle.GetStatus(server.Id).State is not (ServerState.Stopped or ServerState.Failed))
        { await lease.DisposeAsync(); throw new InvalidOperationException("Stop the server and wait for it to exit before editing its files."); }
        return lease;
    }
    public static string Child(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("A single safe filename is required.");
        var path = Path.Combine(directory, name);
        RejectLinks(directory); RejectLinks(path);
        return path;
    }
    public static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Symbolic links are not supported in managed server data: {current}");
            current = Path.GetDirectoryName(current)!;
        }
    }
    public static async Task AtomicWriteAsync(string path, string text, CancellationToken ct)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, text, ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class PropertiesService(IOperationCoordinator coordinator, IServerLifecycleManager lifecycle)
{
    private readonly StoppedOperations guard = new(coordinator, lifecycle);
    public async Task<JavaProperties> ReadAsync(ServerDefinition server, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(server, ct);
        return await ReadUnlockedAsync(server, ct);
    }
    public async Task SaveAsync(ServerDefinition server, IReadOnlyDictionary<string, string> changes, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(server, ct);
        var document = await ReadUnlockedAsync(server, ct);
        foreach (var (key, value) in changes) document.Set(key, value);
        if (changes.TryGetValue("server-port", out var port) && port != server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ArgumentException("Change the server's port through its server definition so launch checks stay consistent.");
        await StoppedOperations.AtomicWriteAsync(Path.Combine(server.Directory, "server.properties"), document.ToString(), ct);
    }
    private static async Task<JavaProperties> ReadUnlockedAsync(ServerDefinition server, CancellationToken ct)
    {
        var path = StoppedOperations.Child(server.Directory, "server.properties");
        return JavaProperties.Parse(File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "");
    }
}
