using System.IO.Compression;
using Harbor.Core;
using Harbor.Foundation;
using Harbor.Management;
using Xunit;

namespace Harbor.Tests;

public sealed class ManagementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Harbor management ü " + Guid.NewGuid().ToString("N"));
    public ManagementTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private ServerDefinition Server()
    {
        var server = new ServerDefinition { MinecraftVersion = "1.21.1", Directory = Path.Combine(root, "世界") };
        Directory.CreateDirectory(server.Directory); return server;
    }
    [Fact]
    public async Task BackupRestoreRoundTripRequiresConfirmationAndPreservesAcceptance()
    {
        var coordinator = new OperationCoordinator(); await using var lifecycle = new ServerLifecycleManager(coordinator);
        var backups = new BackupService(coordinator, lifecycle); var server = Server();
        Directory.CreateDirectory(Path.Combine(server.Directory, "world", "DIM-1"));
        var data = Path.Combine(server.Directory, "world", "DIM-1", "region.mca");
        await File.WriteAllTextAsync(data, "world content");
        await File.WriteAllTextAsync(Path.Combine(server.Directory,"eula.txt"), "local acceptance");
        var backup = await backups.CreateAsync(server);
        await File.WriteAllTextAsync(data, "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => backups.RestoreAsync(server, backup.Path, false));
        Assert.Equal("changed", await File.ReadAllTextAsync(data));
        await backups.RestoreAsync(server, backup.Path, true);
        Assert.Equal("world content", await File.ReadAllTextAsync(data));
        Assert.Equal("local acceptance", await File.ReadAllTextAsync(Path.Combine(server.Directory,"eula.txt")));
        Assert.Equal(2, (await backups.ListAsync(server)).Count);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("/tmp/escaped.txt")]
    public async Task BackupRestoreRejectsTraversalAndPreservesOriginal(string entry)
    {
        var coordinator = new OperationCoordinator(); await using var lifecycle = new ServerLifecycleManager(coordinator);
        var backups = new BackupService(coordinator, lifecycle); var server = Server();
        var original = Path.Combine(server.Directory,"level.dat"); await File.WriteAllTextAsync(original, "original");
        var backup = await backups.CreateAsync(server);
        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        { using var writer = new StreamWriter(archive.CreateEntry(entry).Open()); writer.Write("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(server, backup.Path, true));
        Assert.Equal("original", await File.ReadAllTextAsync(original));
    }

    [Fact]
    public async Task BackupRestoreRejectsCorruptContentAndSymlinks()
    {
        var coordinator = new OperationCoordinator(); await using var lifecycle = new ServerLifecycleManager(coordinator);
        var backups = new BackupService(coordinator, lifecycle); var server = Server();
        var original = Path.Combine(server.Directory,"level.dat"); await File.WriteAllTextAsync(original, "original");
        var backup = await backups.CreateAsync(server);
        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        { archive.GetEntry("level.dat")!.Delete(); using var writer = new StreamWriter(archive.CreateEntry("level.dat").Open()); writer.Write("corrupt"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(server, backup.Path, true));
        Assert.Equal("original", await File.ReadAllTextAsync(original));
        File.CreateSymbolicLink(Path.Combine(server.Directory,"unsafe"), original);
        await Assert.ThrowsAsync<IOException>(() => backups.CreateAsync(server));
    }

    [Fact]
    public async Task ModsRejectVanillaAndRoundTripFabricMetadataEnableDisable()
    {
        var coordinator = new OperationCoordinator(); await using var lifecycle = new ServerLifecycleManager(coordinator);
        var mods = new ModService(coordinator, lifecycle); var server = Server();
        var jar = Path.Combine(root,"example.jar");
        using(var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()); writer.Write("{\"id\":\"example\",\"version\":\"1.0\",\"environment\":\"client\"}"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => mods.ImportAsync(server,[jar]));
        server = server with { Loader = ServerLoader.Fabric };
        await mods.ImportAsync(server,[jar]);
        var imported = Assert.Single(await mods.ListAsync(server));
        Assert.Equal("example", imported.Id); Assert.True(imported.Enabled); Assert.Contains(imported.Warnings,w => w.Contains("client-only"));
        await Assert.ThrowsAsync<IOException>(() => mods.ImportAsync(server,[jar]));
        await mods.SetEnabledAsync(server, imported.Name, false);
        Assert.False(Assert.Single(await mods.ListAsync(server)).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mods.RemoveAsync(server,"example.jar.disabled",false));
        await mods.RemoveAsync(server,"example.jar.disabled",true);
        Assert.Empty(await mods.ListAsync(server));
    }
}
