using Harbor.Core;
using Harbor.Foundation;
using Harbor.Management;
using Xunit;

namespace Harbor.Tests;

public sealed class ServerServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Harbor server copies ü " + Guid.NewGuid().ToString("N"));
    public ServerServiceTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private ServerDefinition Definition() => new() { Name="Original", Directory=Path.Combine(root,"original 世界"), MinecraftVersion="1.21.1", EulaAccepted=true, EulaAcceptedAt=DateTimeOffset.UtcNow };
    [Fact]
    public async Task DuplicateHasIndependentIdentityPortWorldAndRequiresFreshEulaAcceptance()
    {
        var repository = new ServerRepository(Path.Combine(root,"definitions")); var coordinator = new OperationCoordinator();
        await using var lifecycle = new ServerLifecycleManager(coordinator);
        var service = new ServerService(repository,coordinator,lifecycle); var original=Definition();
        Directory.CreateDirectory(Path.Combine(original.Directory,"world")); Directory.CreateDirectory(Path.Combine(original.Directory,"logs"));
        await File.WriteAllTextAsync(Path.Combine(original.Directory,"world","level.dat"),"world");
        await File.WriteAllTextAsync(Path.Combine(original.Directory,"server.properties"),"# comment\nserver-port=25565\nunknown=keep\n");
        await File.WriteAllTextAsync(Path.Combine(original.Directory,"eula.txt"),"accepted");
        await File.WriteAllTextAsync(Path.Combine(original.Directory,"logs","latest.log"),"private");
        await service.SaveAsync(original);
        var duplicate = await service.DuplicateAsync(original,"Copy",Path.Combine(root,"copy café"),25566);
        Assert.NotEqual(original.Id,duplicate.Id); Assert.Equal(25566,duplicate.Port); Assert.False(duplicate.EulaAccepted); Assert.Null(duplicate.EulaAcceptedAt);
        Assert.False(File.Exists(Path.Combine(duplicate.Directory,"eula.txt"))); Assert.False(Directory.Exists(Path.Combine(duplicate.Directory,"logs")));
        Assert.Equal("world",await File.ReadAllTextAsync(Path.Combine(duplicate.Directory,"world","level.dat")));
        var properties=JavaProperties.Parse(await File.ReadAllTextAsync(Path.Combine(duplicate.Directory,"server.properties")));
        Assert.Equal("25566",properties.Values["server-port"]); Assert.Equal("keep",properties.Values["unknown"]);
        Assert.Equal(2,(await repository.ListAsync()).Count);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.DuplicateAsync(original,"Bad",Path.Combine(root,"collision"),25566));
        Assert.False(Directory.Exists(Path.Combine(root,"collision")));
    }
    [Fact]
    public async Task EditKeepsPropertiesAndDefinitionConsistentAndRejectsMigration()
    {
        var repository=new ServerRepository(Path.Combine(root,"definitions"));var coordinator=new OperationCoordinator(); await using var lifecycle=new ServerLifecycleManager(coordinator);
        var service=new ServerService(repository,coordinator,lifecycle);var server=Definition();Directory.CreateDirectory(server.Directory);
        await File.WriteAllTextAsync(Path.Combine(server.Directory,"server.properties"),"# retained\nserver-port=25565\nunknown=keep\n");await service.SaveAsync(server);
        await service.SaveAsync(server with {Port=25577,Name="Renamed"});
        Assert.Equal(25577,Assert.Single(await repository.ListAsync()).Port);
        Assert.Contains("# retained",await File.ReadAllTextAsync(Path.Combine(server.Directory,"server.properties")));
        Assert.Equal("25577",JavaProperties.Parse(await File.ReadAllTextAsync(Path.Combine(server.Directory,"server.properties"))).Values["server-port"]);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SaveAsync(server with {MinecraftVersion="1.21.2"}));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SaveAsync(server with {Directory=Path.Combine(root,"other")}));
    }
    [Fact]
    public async Task DeleteRequiresConfirmationAndRetainsDataUnlessRequested()
    {
        var repository=new ServerRepository(Path.Combine(root,"definitions"));var coordinator=new OperationCoordinator();await using var lifecycle=new ServerLifecycleManager(coordinator);
        var service=new ServerService(repository,coordinator,lifecycle);var server=Definition();Directory.CreateDirectory(server.Directory);await File.WriteAllTextAsync(Path.Combine(server.Directory,"world.dat"),"safe");await service.SaveAsync(server);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.DeleteAsync(server,true,false));Assert.Single(await repository.ListAsync());
        await service.DeleteAsync(server,false,true);Assert.Empty(await repository.ListAsync());Assert.True(Directory.Exists(server.Directory));
        await service.SaveAsync(server);await service.DeleteAsync(server,true,true);Assert.Empty(await repository.ListAsync());Assert.False(Directory.Exists(server.Directory));
    }
}
