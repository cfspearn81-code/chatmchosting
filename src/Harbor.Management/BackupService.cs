using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Harbor.Core;

namespace Harbor.Management;

/// <summary>Stopped-only full server backups. Immutable ZIP manifests verify every file before restore.</summary>
public sealed class BackupService(IOperationCoordinator coordinator, IServerLifecycleManager lifecycle)
{
    private readonly StoppedOperations guard = new(coordinator, lifecycle);
    private const string ManifestName = "harbor-backup-manifest.json";
    private const long MaximumExpandedBytes = 64L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 200000;
    private sealed record Manifest(int SchemaVersion, string Kind, DateTimeOffset CreatedAt, Dictionary<string, string> Checksums);
    public async Task<IReadOnlyList<BackupInfo>> ListAsync(ServerDefinition server, CancellationToken ct = default)
    {
        await using var lease = await coordinator.AcquireAsync(server.Id, ct);
        var directory = BackupDirectory(server);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.zip").OrderDescending().Select(p => new BackupInfo(p, File.GetLastWriteTimeUtc(p), new FileInfo(p).Length, "Full server")).ToList();
    }
    public async Task<BackupInfo> CreateAsync(ServerDefinition server, CancellationToken ct = default)
    {
        await using var lease = await guard.EnterAsync(server, ct);
        return await CreateUnlockedAsync(server, ct);
    }
    public async Task RestoreAsync(ServerDefinition server, string archive, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit confirmation is required to restore a backup and replace server files.");
        await using var lease = await guard.EnterAsync(server, ct);
        ValidateOwnedArchive(server, archive);
        var target = Path.GetFullPath(server.Directory).TrimEnd(Path.DirectorySeparatorChar);
        StoppedOperations.RejectLinks(target);
        var stage = target + ".restore-" + Guid.NewGuid().ToString("N");
        var previous = target + ".recovery-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            await ExtractVerifiedAsync(archive, stage, ct);
            if (Directory.Exists(target))
            {
                // Refuse to replace files unless a separate recovery backup has completed.
                await CreateUnlockedAsync(server, ct);
                foreach (var filename in new[] { "eula.txt", "harbor-launch.json", "harbor-install.json" })
                {
                    var preserved = StoppedOperations.Child(target, filename);
                    if (File.Exists(preserved) && filename == "eula.txt") File.Copy(preserved, Path.Combine(stage, filename), true);
                }
            }
            ct.ThrowIfCancellationRequested();
            // Commit is deliberately non-cancellable, so original files always remain recoverable.
            if (Directory.Exists(target)) Directory.Move(target, previous);
            try { Directory.Move(stage, target); }
            catch { if (Directory.Exists(previous) && !Directory.Exists(target)) Directory.Move(previous, target); throw; }
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public async Task DeleteAsync(ServerDefinition server, string archive, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit confirmation is required to delete a backup.");
        await using var lease = await guard.EnterAsync(server, ct);
        ValidateOwnedArchive(server, archive);
        File.Delete(archive);
    }
    private static string BackupDirectory(ServerDefinition server)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(server.Directory)) ?? throw new ArgumentException("Server directory must have a parent.");
        var path = Path.Combine(parent, ".harbor-backups", server.Id.ToString("N"));
        StoppedOperations.RejectLinks(path); return path;
    }
    private static void ValidateOwnedArchive(ServerDefinition server, string archive)
    {
        StoppedOperations.RejectLinks(archive);
        if (Path.GetDirectoryName(Path.GetFullPath(archive)) != BackupDirectory(server) || !archive.EndsWith(".zip", StringComparison.Ordinal))
            throw new ArgumentException("Choose a backup belonging to this server.");
    }
    private static bool Excluded(string relative)
    {
        var top = relative.Split('/')[0];
        return top is "logs" or "backups" or "eula.txt" or ManifestName || top.StartsWith(".harbor", StringComparison.Ordinal) || top.EndsWith(".lock", StringComparison.Ordinal) || top.EndsWith(".pid", StringComparison.Ordinal);
    }
    private static IEnumerable<string> SafeFiles(string directory)
    {
        StoppedOperations.RejectLinks(directory);
        foreach (var item in Directory.EnumerateFileSystemEntries(directory))
        {
            StoppedOperations.RejectLinks(item);
            if (Directory.Exists(item)) { foreach (var file in SafeFiles(item)) yield return file; }
            else yield return item;
        }
    }
    private static async Task<BackupInfo> CreateUnlockedAsync(ServerDefinition server, CancellationToken ct)
    {
        StoppedOperations.RejectLinks(server.Directory);
        if (!Directory.Exists(server.Directory)) throw new DirectoryNotFoundException("Server data directory does not exist.");
        var directory = BackupDirectory(server); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.zip");
        var temporary = path + ".partial";
        try
        {
            var files = SafeFiles(server.Directory).Where(f => !Excluded(Path.GetRelativePath(server.Directory, f).Replace('\\', '/'))).ToArray();
            var size = files.Sum(f => new FileInfo(f).Length);
            if (size > MaximumExpandedBytes || files.Length > MaximumEntries) throw new IOException("Server exceeds the supported 64 GiB / 200,000-file backup limit.");
            CheckSpace(directory, size + 64 * 1024 * 1024);
            var manifest = new Manifest(1, "full-server", DateTimeOffset.UtcNow, new(StringComparer.Ordinal));
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, true);
                foreach (var source in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(server.Directory, source).Replace('\\', '/');
                    var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    manifest.Checksums[relative] = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
                    input.Position = 0;
                    await using var output = entry.Open(); await input.CopyToAsync(output, ct);
                }
                var metadata = zip.CreateEntry(ManifestName);
                await using var metadataStream = metadata.Open(); await JsonSerializer.SerializeAsync(metadataStream, manifest, cancellationToken: ct);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path);
            return new(path, manifest.CreatedAt, new FileInfo(path).Length, "Full server");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void CheckSpace(string path, long required)
    {
        // DriveInfo uses the filesystem containing the path on Linux.
        var full = Path.GetFullPath(path);
        var drive = DriveInfo.GetDrives().Where(d => full == d.Name.TrimEnd('/') || full.StartsWith(d.Name.EndsWith('/') ? d.Name : d.Name + '/', StringComparison.Ordinal))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault();
        if (drive is not null && drive.AvailableFreeSpace < required) throw new IOException($"Insufficient disk space: at least {required / (1024 * 1024)} MiB free is required.");
    }
    internal static string EntryDestination(string root, ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        var mode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (mode == 0xA000 || (mode != 0 && mode != 0x8000 && mode != 0x4000) || name.Contains('\\') || name.Contains('\0') || Path.IsPathRooted(name) || name.Split('/').Any(p => p is ".." or "."))
            throw new InvalidDataException($"Unsafe archive entry: {name}");
        var fullRoot = Path.GetFullPath(root).TrimEnd('/') + '/';
        var destination = Path.GetFullPath(Path.Combine(root, name));
        if (!destination.StartsWith(fullRoot, StringComparison.Ordinal)) throw new InvalidDataException("Archive entry escapes its destination.");
        return destination;
    }
    private static async Task ExtractVerifiedAsync(string path, string stage, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > MaximumEntries + 1) throw new InvalidDataException("Too many archive entries.");
        long size = 0;
        foreach (var entry in zip.Entries) { size = checked(size + entry.Length); if (size > MaximumExpandedBytes) throw new InvalidDataException("Archive exceeds the 64 GiB extraction limit."); }
        CheckSpace(stage, size + 64 * 1024 * 1024);
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Backup manifest is missing.");
        if (manifestEntry.Length > 32 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
        Manifest manifest;
        await using (var stream = manifestEntry.Open()) manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, cancellationToken: ct) ?? throw new InvalidDataException("Invalid backup manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Kind != "full-server") throw new InvalidDataException("Unsupported backup schema or kind.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var destination = EntryDestination(stage, entry);
            if (!seen.Add(entry.FullName)) throw new InvalidDataException("Duplicate archive entry.");
            if (entry.FullName == ManifestName) continue;
            if (Excluded(entry.FullName)) throw new InvalidDataException("Archive contains protected server files.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            if (!manifest.Checksums.TryGetValue(entry.FullName, out var checksum)) throw new InvalidDataException("File is missing from backup manifest.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, ct);
            await using var verification = File.OpenRead(destination);
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(verification, ct)), checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum failed for {entry.FullName}.");
        }
        if (manifest.Checksums.Keys.Any(name => !seen.Contains(name))) throw new InvalidDataException("Backup is missing files listed in its manifest.");
    }
}
