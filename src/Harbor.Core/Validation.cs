using System.Text.RegularExpressions;

namespace Harbor.Core;

public static partial class ServerValidation
{
    public static IReadOnlyList<string> Validate(ServerDefinition server)
    {
        var errors = new List<string>();
        if (server.Id == Guid.Empty) errors.Add("A stable server ID is required.");
        if (string.IsNullOrWhiteSpace(server.Name) || server.Name.Length > 100 || server.Name.Any(char.IsControl))
            errors.Add("Choose a server name of 1–100 characters without control characters.");
        if (string.IsNullOrWhiteSpace(server.Directory) || !Path.IsPathFullyQualified(server.Directory))
            errors.Add("Choose an absolute server data directory.");
        if (server.Port is < 1024 or > 65535) errors.Add("Server port must be between 1024 and 65535 for unprivileged hosting.");
        if (server.MinimumMemoryMb < 256 || server.MaximumMemoryMb < server.MinimumMemoryMb || server.MaximumMemoryMb > 1048576)
            errors.Add("Memory must be at least 256 MiB; maximum must be at least minimum and no more than 1 TiB.");
        if (string.IsNullOrWhiteSpace(server.JavaPath)) errors.Add("Select an executable Java runtime.");
        if (string.IsNullOrWhiteSpace(server.MinecraftVersion) || !VersionIdPattern().IsMatch(server.MinecraftVersion))
            errors.Add("Select a valid Minecraft version from the official catalog.");
        return errors;
    }

    public static void ThrowIfInvalid(ServerDefinition server)
    {
        var errors = Validate(server);
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(server));
    }

    public static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32767 || command.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("Enter a single console command without line breaks.", nameof(command));
    }

    public static string ValidateWorldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', '\0', '\r', '\n' }) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("World name must be a single safe directory name.", nameof(name));
        return name;
    }

    public static bool IsPlayerName(string name) => PlayerNamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9._+\\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdPattern();
    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerNamePattern();
}

/// <summary>XDG roots; environment variables are accepted only when absolute, per the XDG specification.</summary>
public static class HarborPaths
{
    public static string DataDirectory => Path.Combine(XdgRoot("XDG_DATA_HOME", ".local/share"), "harbor");
    public static string ConfigDirectory => Path.Combine(XdgRoot("XDG_CONFIG_HOME", ".config"), "harbor");
    public static string CacheDirectory => Path.Combine(XdgRoot("XDG_CACHE_HOME", ".cache"), "harbor");
    public static string StateDirectory => Path.Combine(XdgRoot("XDG_STATE_HOME", ".local/state"), "harbor");
    private static string XdgRoot(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
    }
}
