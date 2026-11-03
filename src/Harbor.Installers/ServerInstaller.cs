using System.Diagnostics;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Harbor.Core;

namespace Harbor.Installers;

/// <summary>Official metadata is authoritative. Installers execute only explicitly chosen upstream loader code.</summary>
public sealed class ServerInstaller(IVerifiedDownloader downloader, IOperationCoordinator coordinator, HttpClient? httpClient = null) : IServerInstaller
{
    private readonly HttpClient http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    public const string EulaUrl = "https://www.minecraft.net/eula";
    public async Task<IReadOnlyList<MinecraftRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var json = await GetJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", cancellationToken);
        return json.RootElement.GetProperty("versions").EnumerateArray().Where(v => v.GetProperty("type").GetString() == "release")
            .Select(v => new MinecraftRelease(v.GetProperty("id").GetString()!, "release", new Uri(v.GetProperty("url").GetString()!), v.GetProperty("releaseTime").GetDateTimeOffset())).ToArray();
    }

    public async Task<InstallResult> InstallAsync(InstallRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var server = request.Server;
        ServerValidation.ThrowIfInvalid(server);
        await using var lease = await coordinator.AcquireAsync(server.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(server.Directory) || !Path.IsPathFullyQualified(server.Directory)) throw new ArgumentException("Choose an absolute server directory.");
        if (Directory.Exists(server.Directory) && Directory.EnumerateFileSystemEntries(server.Directory).Any()) throw new IOException("Install into an empty directory to preserve existing data.");
        var release = (await GetReleasesAsync(cancellationToken)).FirstOrDefault(r => r.Id == server.MinecraftVersion)
            ?? throw new NotSupportedException("Choose a Minecraft release returned by official Mojang metadata.");
        using var metadata = await GetJsonAsync(release.MetadataUrl.AbsoluteUri, cancellationToken);
        var requiredJava = metadata.RootElement.TryGetProperty("javaVersion", out var java) ? java.GetProperty("majorVersion").GetInt32() : 8;
        var runtime = await new JavaDiscovery().ProbeAsync(server.JavaPath, cancellationToken);
        if (runtime.MajorVersion != requiredJava) throw new InvalidOperationException($"Minecraft {server.MinecraftVersion} requires Java {requiredJava}; selected executable reports Java {runtime.MajorVersion}.");
        server = server with { JavaMajor = requiredJava, LoaderVersion = request.LoaderVersion ?? server.LoaderVersion };
        var stage = server.Directory.TrimEnd(Path.DirectorySeparatorChar) + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        string? notice = null;
        try
        {
            switch (server.Loader)
            {
                case ServerLoader.Vanilla:
                    var artifact = metadata.RootElement.GetProperty("downloads").GetProperty("server");
                    await downloader.DownloadAsync(new(new Uri(artifact.GetProperty("url").GetString()!), Path.Combine(stage, "server.jar"), artifact.GetProperty("sha1").GetString(), "SHA1"), progress, cancellationToken);
                    break;
                case ServerLoader.Fabric:
                    await InstallFabricAsync(server, stage, progress, cancellationToken);
                    notice = "Fabric server launcher has no published checksum in its API; downloaded over verified HTTPS.";
                    break;
                case ServerLoader.Forge:
                case ServerLoader.NeoForge:
                    notice = await InstallForgeFamilyAsync(server, stage, progress, cancellationToken);
                    break;
                default: throw new NotSupportedException("Unsupported loader.");
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "server.properties"), $"server-port={server.Port}\nonline-mode=true\n", cancellationToken);
            if (server.EulaAccepted)
            {
                server = server with { EulaAcceptedAt = server.EulaAcceptedAt ?? DateTimeOffset.UtcNow };
                await File.WriteAllTextAsync(Path.Combine(stage, "eula.txt"), $"# Explicit Harbor user acceptance: {server.EulaAcceptedAt:O}\n# {EulaUrl}\neula=true\n", cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(server.Directory)) Directory.Delete(server.Directory);
            Directory.Move(stage, server.Directory);
            return new(server, CreateLaunchPlan(server), notice);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static LaunchPlan CreateLaunchPlan(ServerDefinition server)
    {
        var args = new List<string> { $"-Xms{server.MinimumMemoryMb}M", $"-Xmx{server.MaximumMemoryMb}M" };
        if (server.Loader is ServerLoader.Forge or ServerLoader.NeoForge)
        {
            var libraryRoot = Path.Combine(server.Directory, "libraries", server.Loader == ServerLoader.Forge ? "net/minecraftforge/forge" : "net/neoforged/neoforge");
            var files = Directory.Exists(libraryRoot) ? Directory.GetFiles(libraryRoot, "unix_args.txt", SearchOption.AllDirectories) : [];
            if (files.Length != 1) throw new FileNotFoundException("Loader installer did not generate exactly one Unix argument file. Reinstall the selected modern loader.");
            args.Add("@" + Path.GetRelativePath(server.Directory, files[0]));
        }
        else { args.Add("-jar"); args.Add(server.Loader == ServerLoader.Fabric ? "fabric-server-launch.jar" : "server.jar"); }
        args.Add("nogui");
        return new(server.JavaPath, args, server.Directory);
    }

    public async Task<int> GetRequiredJavaAsync(string minecraftVersion, CancellationToken cancellationToken = default)
    {
        var release = (await GetReleasesAsync(cancellationToken)).First(r => r.Id == minecraftVersion);
        using var metadata = await GetJsonAsync(release.MetadataUrl.AbsoluteUri, cancellationToken);
        return metadata.RootElement.TryGetProperty("javaVersion", out var java) ? java.GetProperty("majorVersion").GetInt32() : 8;
    }

    public async Task<IReadOnlyList<string>> GetLoaderVersionsAsync(ServerLoader loader, string minecraftVersion, CancellationToken cancellationToken = default)
    {
        if (loader == ServerLoader.Vanilla) return [];
        if (loader == ServerLoader.Fabric)
        {
            using var list = await GetJsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}", cancellationToken);
            return list.RootElement.EnumerateArray().Select(v => v.GetProperty("loader").GetProperty("version").GetString()!).ToArray();
        }
        var endpoint = loader == ServerLoader.Forge ? "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml" : "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
        var xml = XDocument.Parse(await http.GetStringAsync(endpoint, cancellationToken));
        return xml.Descendants("version").Select(x => x.Value).Where(v => loader != ServerLoader.Forge || v.StartsWith(minecraftVersion + "-", StringComparison.Ordinal)).Reverse().ToArray();
    }

    private async Task InstallFabricAsync(ServerDefinition server, string stage, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var loaders = await GetLoaderVersionsAsync(ServerLoader.Fabric, server.MinecraftVersion, ct);
        var loader = server.LoaderVersion ?? throw new ArgumentException("Select a Fabric loader version.");
        if (!loaders.Contains(loader)) throw new ArgumentException("Fabric does not list that loader for the Minecraft release.");
        using var installers = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/installer", ct);
        var installer = installers.RootElement.EnumerateArray().First(v => v.GetProperty("stable").GetBoolean()).GetProperty("version").GetString();
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(server.MinecraftVersion)}/{Uri.EscapeDataString(loader)}/{Uri.EscapeDataString(installer!)}/server/jar";
        await downloader.DownloadAsync(new(new Uri(url), Path.Combine(stage, "fabric-server-launch.jar")), progress, ct);
    }

    private async Task<string?> InstallForgeFamilyAsync(ServerDefinition server, string stage, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var version = server.LoaderVersion ?? throw new ArgumentException("Select an upstream loader artifact version.");
        if (!(await GetLoaderVersionsAsync(server.Loader, server.MinecraftVersion, ct)).Contains(version)) throw new ArgumentException("Unknown loader version.");
        var root = server.Loader == ServerLoader.Forge ? "https://maven.minecraftforge.net/net/minecraftforge/forge" : "https://maven.neoforged.net/releases/net/neoforged/neoforge";
        var name = server.Loader == ServerLoader.Forge ? "forge" : "neoforge";
        var url = $"{root}/{Uri.EscapeDataString(version)}/{name}-{Uri.EscapeDataString(version)}-installer.jar";
        var checksum = (await http.GetStringAsync(url + ".sha1", ct)).Trim().Split(' ', '\t', '\n')[0];
        await downloader.DownloadAsync(new(new Uri(url), Path.Combine(stage, "installer.jar"), checksum, "SHA1"), progress, ct);
        using (var archive = ZipFile.OpenRead(Path.Combine(stage, "installer.jar")))
        {
            var profile = archive.GetEntry("install_profile.json") ?? throw new InvalidDataException("Loader installer has no install profile.");
            using var profileStream = profile.Open();
            using var profileJson = await JsonDocument.ParseAsync(profileStream, cancellationToken: ct);
            if (!profileJson.RootElement.TryGetProperty("minecraft", out var targetMinecraft) || targetMinecraft.GetString() != server.MinecraftVersion)
                throw new NotSupportedException("Loader installer does not target the selected Minecraft version.");
        }
        var info = new ProcessStartInfo(server.JavaPath) { WorkingDirectory = stage, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-jar", "installer.jar", "--installServer" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not launch loader installer.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var combined = await output + "\n" + await error;
        await File.WriteAllTextAsync(Path.Combine(stage, "loader-install.log"), combined, ct);
        if (process.ExitCode != 0) throw new IOException($"Loader installer failed ({process.ExitCode}): {combined[^Math.Min(4000, combined.Length)..]}");
        _ = CreateLaunchPlan(server with { Directory = stage });
        File.Delete(Path.Combine(stage, "installer.jar"));
        return "Installer SHA-1 verified; loader installer manages its own libraries. Only modern Unix-argument-file releases are supported.";
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
