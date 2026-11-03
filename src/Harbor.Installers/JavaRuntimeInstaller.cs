using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Harbor.Core;

namespace Harbor.Installers;

public sealed record RuntimeOffer(int Major, string Name, Uri DownloadUrl, string Sha256, Uri ReleaseNotes, string License = "GPLv2 with Classpath Exception");

/// <summary>Opt-in per-user Temurin installation; never changes system Java or environment settings.</summary>
public sealed class JavaRuntimeInstaller(IVerifiedDownloader downloader, HttpClient? client = null)
{
    private readonly HttpClient http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    public async Task<RuntimeOffer> ResolveAsync(int major, CancellationToken cancellationToken = default)
    {
        if (major < 8 || major > 100) throw new ArgumentOutOfRangeException(nameof(major));
        var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jre&os=linux&vendor=eclipse";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var asset = json.RootElement.EnumerateArray().FirstOrDefault();
        if (asset.ValueKind == JsonValueKind.Undefined) throw new NotSupportedException($"No Linux x64 Temurin Java {major} runtime is published.");
        var package = asset.GetProperty("binary").GetProperty("package");
        return new(major, asset.GetProperty("release_name").GetString()!, new Uri(package.GetProperty("link").GetString()!), package.GetProperty("checksum").GetString()!, new Uri(asset.GetProperty("release_link").GetString()!));
    }

    public async Task<JavaInstallation> InstallAsync(RuntimeOffer offer, string managedDirectory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(managedDirectory);
        var stage = Path.Combine(managedDirectory, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var archive = stage + ".tar.gz";
        try
        {
            await downloader.DownloadAsync(new(offer.DownloadUrl, archive, offer.Sha256, "SHA256"), progress, cancellationToken);
            await using var input = File.OpenRead(archive);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            var root = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            long total = 0;
            var links = new List<(string Path, string Target)>();
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: cancellationToken)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(stage, entry.Name));
                if (!destination.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Runtime archive path escapes destination.");
                if ((total += entry.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Runtime archive exceeds 2 GiB limit.");
                if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(destination);
                else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var file = new FileStream(destination, FileMode.CreateNew))
                        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(file, cancellationToken);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
                }
                else if (entry.EntryType == TarEntryType.SymbolicLink)
                {
                    var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(destination)!, entry.LinkName));
                    if (Path.IsPathFullyQualified(entry.LinkName) || !target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Unsafe runtime symlink.");
                    links.Add((destination, entry.LinkName));
                }
                else throw new InvalidDataException($"Unsupported runtime archive entry: {entry.EntryType}");
            }
            // Do not follow links while writing archive files; create validated internal links only afterwards.
            foreach (var link in links)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link.Path)!);
                File.CreateSymbolicLink(link.Path, link.Target);
            }
            var java = Directory.GetFiles(stage, "java", SearchOption.AllDirectories).FirstOrDefault(x => Path.GetFileName(Path.GetDirectoryName(x)) == "bin") ?? throw new InvalidDataException("Runtime contains no bin/java.");
            var probed = await new JavaDiscovery().ProbeAsync(java, cancellationToken);
            if (probed.MajorVersion != offer.Major) throw new InvalidDataException("Downloaded Java major does not match selection.");
            var destinationRoot = Path.Combine(managedDirectory, "temurin-" + offer.Major + "-" + offer.Sha256[..12]);
            var relativeJava = Path.GetRelativePath(stage, java);
            if (Directory.Exists(destinationRoot)) throw new IOException("This runtime already exists; select it from Java discovery.");
            Directory.Move(stage, destinationRoot);
            return probed with { Executable = Path.Combine(destinationRoot, relativeJava) };
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
