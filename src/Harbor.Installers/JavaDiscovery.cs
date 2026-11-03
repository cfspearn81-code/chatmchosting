using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Harbor.Installers;

public sealed record JavaInstallation(string Executable, int MajorVersion, string Architecture, string VersionOutput);

public sealed class JavaDiscovery
{
    public async Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-XshowSettings:properties");
        info.ArgumentList.Add("-version");
        using var process = Process.Start(info) ?? throw new IOException("Could not launch Java.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + "\n" + await stderr;
        if (process.ExitCode != 0) throw new IOException($"Java exited with code {process.ExitCode}: {output}");
        var match = Regex.Match(output, @"(?:java\.version\s*=\s*|version\s+""?)(?<major>\d+)(?:\.(?<minor>\d+))?");
        if (!match.Success) throw new InvalidDataException("Java did not report a recognized version.");
        var major = int.Parse(match.Groups["major"].Value);
        if (major == 1) major = int.Parse(match.Groups["minor"].Value);
        var architecture = Regex.Match(output, @"os\.arch\s*=\s*(\S+)").Groups[1].Value;
        if (architecture is not ("amd64" or "x86_64")) throw new NotSupportedException($"Java architecture '{architecture}' is not supported; choose Linux x86-64 Java.");
        return new(executable, major, architecture, output.Trim());
    }

    public async Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(string? managedDirectory = null, CancellationToken cancellationToken = default)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir, "java"));
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { } home) candidates.Add(Path.Combine(home, "bin", "java"));
        foreach (var root in new[] { "/usr/lib/jvm", managedDirectory }.Where(x => x is not null && Directory.Exists(x)))
        {
            foreach (var dir in Directory.EnumerateDirectories(root!))
            {
                candidates.Add(Path.Combine(dir, "bin", "java"));
                foreach (var nested in Directory.EnumerateDirectories(dir)) candidates.Add(Path.Combine(nested, "bin", "java"));
            }
        }
        var results = new List<JavaInstallation>();
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in candidates.Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var real = File.ResolveLinkTarget(path, true)?.FullName ?? Path.GetFullPath(path);
            if (!resolved.Add(real)) continue;
            try { results.Add(await ProbeAsync(real, cancellationToken)); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidDataException or NotSupportedException or TimeoutException) { /* Other discovered runtimes remain usable. */ }
        }
        return results;
    }
}
