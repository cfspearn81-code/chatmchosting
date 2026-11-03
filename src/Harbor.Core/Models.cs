namespace Harbor.Core;

public enum ServerLoader { Vanilla, Fabric, Forge, NeoForge }
public enum ServerState { Stopped, Installing, Starting, Running, Stopping, Failed }

public sealed record ServerDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Minecraft server";
    public string Directory { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public ServerLoader Loader { get; init; } = ServerLoader.Vanilla;
    public string? LoaderVersion { get; init; }
    public string JavaPath { get; init; } = "java";
    public int JavaMajor { get; init; } = 21;
    public int Port { get; init; } = 25565;
    public int MinimumMemoryMb { get; init; } = 512;
    public int MaximumMemoryMb { get; init; } = 2048;
    public bool EulaAccepted { get; init; }
    public DateTimeOffset? EulaAcceptedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LaunchPlan(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);
public sealed record ConsoleLine(Guid ServerId, DateTimeOffset Timestamp, string Text, bool IsError = false);
public sealed record ServerStatus(Guid ServerId, ServerState State, int? ProcessId = null, string? Error = null);
public sealed record ProcessMetrics(int ProcessId, TimeSpan Uptime, long ResidentMemoryBytes, double? CpuPercent);
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}
public sealed record DownloadArtifact(Uri Url, string DestinationPath, string? Checksum = null, string ChecksumAlgorithm = "SHA256");
public sealed record MinecraftRelease(string Id, string Type, Uri MetadataUrl, DateTimeOffset ReleaseTime);
public sealed record JavaRuntime(string Executable, int Major, string Version, string Architecture, bool IsManaged = false);
public sealed record InstallRequest(ServerDefinition Server, string? LoaderVersion = null);
public sealed record InstallResult(ServerDefinition Server, LaunchPlan LaunchPlan, string? ChecksumNotice = null);
public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long SizeBytes, string Kind);
