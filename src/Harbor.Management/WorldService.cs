using System.IO.Compression;
using Harbor.Core;

namespace Harbor.Management;

public sealed class WorldService(IOperationCoordinator coordinator, IServerLifecycleManager lifecycle)
{
    private readonly StoppedOperations guard = new(coordinator, lifecycle);
    public async Task ExportAsync(ServerDefinition server, string destinationArchive, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(server, ct);
        var world = await WorldPathAsync(server, ct);
        await ExportUnlockedAsync(world, destinationArchive, ct);
    }
    public async Task ImportAsync(ServerDefinition server, string sourceDirectoryOrZip, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm world replacement before importing. Java worlds only; Bedrock worlds are not converted.");
        await using var lease = await guard.EnterAsync(server, ct);
        var target = await WorldPathAsync(server, ct);
        var stage = target + ".import-" + Guid.NewGuid().ToString("N");
        var recovery = target + ".previous-" + Guid.NewGuid().ToString("N");
        StoppedOperations.RejectLinks(sourceDirectoryOrZip);
        if (Path.GetFullPath(sourceDirectoryOrZip).StartsWith(Path.GetFullPath(target).TrimEnd('/') + '/', StringComparison.Ordinal) || Path.GetFullPath(sourceDirectoryOrZip) == Path.GetFullPath(target))
            throw new ArgumentException("Import source must be outside the current world.");
        Directory.CreateDirectory(stage);
        try
        {
            if (Directory.Exists(sourceDirectoryOrZip))
                await CopyTreeAsync(sourceDirectoryOrZip, stage, ct);
            else await ExtractWorldAsync(sourceDirectoryOrZip, stage, ct);
            if (!File.Exists(Path.Combine(stage, "level.dat"))) throw new InvalidDataException("Select a Java world directory, or a ZIP with level.dat at its root. Bedrock worlds are not converted.");
            if (Directory.Exists(target))
            {
                var backupDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(server.Directory))!, ".harbor-backups", server.Id.ToString("N"), "world-exports");
                StoppedOperations.RejectLinks(backupDirectory); Directory.CreateDirectory(backupDirectory);
                await ExportUnlockedAsync(target, Path.Combine(backupDirectory, $"recovery-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip"), ct);
            }
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target)) Directory.Move(target, recovery);
            try { Directory.Move(stage, target); }
            catch { if (Directory.Exists(recovery) && !Directory.Exists(target)) Directory.Move(recovery, target); throw; }
            if (Directory.Exists(recovery)) Directory.Delete(recovery, true);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static async Task<string> WorldPathAsync(ServerDefinition server, CancellationToken ct)
    {
        var path = StoppedOperations.Child(server.Directory, "server.properties");
        var properties = JavaProperties.Parse(File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "");
        var name = properties.Values.GetValueOrDefault("level-name", "world"); JavaProperties.Validate("level-name", name);
        return StoppedOperations.Child(server.Directory, name);
    }
    private static IEnumerable<string> Files(string root)
    {
        StoppedOperations.RejectLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            StoppedOperations.RejectLinks(entry);
            if (Directory.Exists(entry)) { foreach (var file in Files(entry)) yield return file; }
            else yield return entry;
        }
    }
    private static async Task ExportUnlockedAsync(string world, string destinationArchive, CancellationToken ct)
    {
        StoppedOperations.RejectLinks(world); StoppedOperations.RejectLinks(destinationArchive);
        if (!File.Exists(Path.Combine(world, "level.dat"))) throw new InvalidDataException("World has no level.dat; it is not a recognizable Java world.");
        if (Path.GetFullPath(destinationArchive).StartsWith(Path.GetFullPath(world).TrimEnd('/') + '/', StringComparison.Ordinal)) throw new ArgumentException("Export destination must be outside the world.");
        var files = Files(world).ToArray();
        var size = files.Sum(f => new FileInfo(f).Length);
        if (size > 64L * 1024 * 1024 * 1024 || files.Length > 200000) throw new IOException("World exceeds the supported export limits (64 GiB / 200,000 files).");
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationArchive))!; Directory.CreateDirectory(parent); BackupService.CheckSpace(parent, size + 64 * 1024 * 1024);
        var temp = destinationArchive + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, true);
                foreach (var source in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(Path.GetRelativePath(world, source).Replace('\\', '/'), CompressionLevel.Fastest);
                    await using var input = File.OpenRead(source); await using var output = entry.Open(); await input.CopyToAsync(output, ct);
                }
            }
            ct.ThrowIfCancellationRequested(); File.Move(temp, destinationArchive, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task CopyTreeAsync(string source, string stage, CancellationToken ct)
    {
        var files = Files(source).ToArray(); var total = files.Sum(f => new FileInfo(f).Length);
        if (total > 64L * 1024 * 1024 * 1024 || files.Length > 200000) throw new IOException("World exceeds import limits.");
        BackupService.CheckSpace(stage, total + 64 * 1024 * 1024);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested(); var target = Path.Combine(stage, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); await input.CopyToAsync(output, ct);
        }
    }
    private static async Task ExtractWorldAsync(string source, string stage, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > 200000) throw new InvalidDataException("World archive has too many entries.");
        long size = 0; foreach (var entry in zip.Entries) { size = checked(size + entry.Length); if (size > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("World archive exceeds 64 GiB."); }
        BackupService.CheckSpace(stage, size + 64 * 1024 * 1024);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = BackupService.EntryDestination(stage, entry);
            if (!seen.Add(target)) throw new InvalidDataException("Duplicate world archive entry.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); await BackupService.CopyEntryAsync(entry, output, ct);
        }
    }
}
