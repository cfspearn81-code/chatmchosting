namespace Harbor.Core;

public interface IServerRepository
{
    Task<IReadOnlyList<ServerDefinition>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ServerDefinition server, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>One cross-process exclusive lock per server; callers must not acquire recursively.</summary>
public interface IOperationCoordinator
{
    Task<IAsyncDisposable> AcquireAsync(Guid serverId, CancellationToken cancellationToken = default);
}
public interface IVerifiedDownloader
{
    Task DownloadAsync(DownloadArtifact artifact, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
public interface IServerLifecycleManager : IAsyncDisposable
{
    event EventHandler<ConsoleLine>? OutputReceived;
    event EventHandler<ServerStatus>? StatusChanged;
    ServerStatus GetStatus(Guid serverId);
    Task StartAsync(ServerDefinition server, LaunchPlan plan, CancellationToken cancellationToken = default);
    Task StopAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task SendCommandAsync(Guid serverId, string command, CancellationToken cancellationToken = default);
    ProcessMetrics? GetMetrics(Guid serverId);
}
public interface IServerInstaller
{
    Task<IReadOnlyList<MinecraftRelease>> GetReleasesAsync(CancellationToken cancellationToken = default);
    Task<InstallResult> InstallAsync(InstallRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
