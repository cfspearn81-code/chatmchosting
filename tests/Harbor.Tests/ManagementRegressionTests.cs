using System.IO.Compression;
using Harbor.Core;
using Harbor.Foundation;
using Harbor.Management;
using Xunit;

namespace Harbor.Tests;

public sealed class ManagementRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Harbor management 世界 " + Guid.NewGuid().ToString("N"));
    private readonly OperationCoordinator coordinator = new();
    private readonly StubLifecycle lifecycle = new();
    public ManagementRegressionTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private ServerDefinition Server()
    {
        var server = new ServerDefinition { Directory = Path.Combine(root, "server café"), MinecraftVersion = "1.21.1", Port = 25570, Loader = ServerLoader.Fabric };
        Directory.CreateDirectory(server.Directory); return server;
    }
    [Fact]
    public async Task BackupRestoreVerifiesFilesAndPreservesCurrentConsentAndPort()
    {
        var server = Server();
        Directory.CreateDirectory(Path.Combine(server.Directory, "world", "DIM-1"));
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "world", "level.dat"), "realistic fixture, not a live Minecraft world");
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "world", "DIM-1", "region.mca"), "dimension fixture");
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "server.properties"), "# original\nserver-port=25565\nonline-mode=true\n");
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "eula.txt"), "eula=false\n");
        var backups = new BackupService(coordinator, lifecycle);
        var backup = await backups.CreateAsync(server);
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "world", "level.dat"), "changed");
        await backups.RestoreAsync(server, backup.Path, true);
        Assert.Equal("realistic fixture, not a live Minecraft world", await File.ReadAllTextAsync(Path.Combine(server.Directory, "world", "level.dat")));
        Assert.Equal("dimension fixture", await File.ReadAllTextAsync(Path.Combine(server.Directory, "world", "DIM-1", "region.mca")));
        Assert.Equal("eula=false\n", await File.ReadAllTextAsync(Path.Combine(server.Directory, "eula.txt")));
        Assert.Equal("25570", JavaProperties.Parse(await File.ReadAllTextAsync(Path.Combine(server.Directory, "server.properties"))).Values["server-port"]);
        Assert.Equal(2, (await backups.ListAsync(server)).Count); // Original and automatic recovery backup.
    }
    [Fact]
    public async Task CorruptBackupDoesNotReplaceOriginalData()
    {
        var server = Server(); var path = Path.Combine(server.Directory, "important.txt"); await File.WriteAllTextAsync(path, "original");
        var backups = new BackupService(coordinator, lifecycle); var backup = await backups.CreateAsync(server);
        using (var zip = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        {
            zip.GetEntry("important.txt")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("important.txt").Open()); writer.Write("corrupted");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(server, backup.Path, true));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task WorldTraversalIsRejectedWithoutChangingExistingWorld()
    {
        var server = Server(); Directory.CreateDirectory(Path.Combine(server.Directory, "world"));
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "world", "level.dat"), "original");
        var malicious = Path.Combine(root, "bad.zip");
        using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("escaped"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => new WorldService(coordinator, lifecycle).ImportAsync(server, malicious, true));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(server.Directory, "world", "level.dat")));
        Assert.False(File.Exists(Path.Combine(server.Directory, "escape.txt")));
    }
    [Fact]
    public async Task ModsUseRealJarMetadataAndCannotChangeRunningServer()
    {
        var server = Server(); var jar = Path.Combine(root, "example.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()); writer.Write("{\"id\":\"example\",\"version\":\"1.0\",\"environment\":\"client\"}"); }
        var mods = new ModService(coordinator, lifecycle); await mods.ImportAsync(server, [jar]);
        var mod = Assert.Single(await mods.ListAsync(server)); Assert.Equal("example", mod.Id); Assert.Contains(mod.Warnings, w => w.Contains("client-only"));
        await mods.SetEnabledAsync(server, mod.Name, false); Assert.False(Assert.Single(await mods.ListAsync(server)).Enabled);
        lifecycle.State = ServerState.Running;
        await Assert.ThrowsAsync<InvalidOperationException>(() => mods.SetEnabledAsync(server, "example.jar.disabled", true));
    }
    private sealed class StubLifecycle : IServerLifecycleManager
    {
        public ServerState State { get; set; } = ServerState.Stopped;
        public event EventHandler<ConsoleLine>? OutputReceived { add { } remove { } }
        public event EventHandler<ServerStatus>? StatusChanged { add { } remove { } }
        public ServerStatus GetStatus(Guid id) => new(id, State);
        public Task StartAsync(ServerDefinition server, LaunchPlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendCommandAsync(Guid id, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ProcessMetrics? GetMetrics(Guid id) => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
