using System.IO.Compression;
using Harbor.Core;
using Harbor.Foundation;
using Harbor.Management;
using Xunit;
namespace Harbor.Tests;
public sealed class WorldTests
{
    [Fact]
    public async Task WorldExportImportIncludesDimensionsAndCreatesRecoveryExport()
    {
        var root=Path.Combine(Path.GetTempPath(),"Harbor world ü "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var server=new ServerDefinition {Directory=Path.Combine(root,"server"),MinecraftVersion="1.21.1"};
            var world=Path.Combine(server.Directory,"world");Directory.CreateDirectory(Path.Combine(world,"DIM-1","region"));
            await File.WriteAllTextAsync(Path.Combine(world,"level.dat"),"metadata");await File.WriteAllTextAsync(Path.Combine(world,"DIM-1","region","r.0.0.mca"),"nether");
            var coordinator=new OperationCoordinator();await using var lifecycle=new ServerLifecycleManager(coordinator);var service=new WorldService(coordinator,lifecycle);
            var export=Path.Combine(root,"export.zip");await service.ExportAsync(server,export);
            using(var zip=ZipFile.OpenRead(export))Assert.NotNull(zip.GetEntry("DIM-1/region/r.0.0.mca"));
            await File.WriteAllTextAsync(Path.Combine(world,"level.dat"),"changed");
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ImportAsync(server,export,false));
            Assert.Equal("changed",await File.ReadAllTextAsync(Path.Combine(world,"level.dat")));
            await service.ImportAsync(server,export,true);
            Assert.Equal("metadata",await File.ReadAllTextAsync(Path.Combine(world,"level.dat")));
            var recovery=Assert.Single(Directory.GetFiles(Path.Combine(root,".harbor-backups",server.Id.ToString("N"),"world-exports"),"*.zip"));
            using var recoveryZip=ZipFile.OpenRead(recovery);using var reader=new StreamReader(recoveryZip.GetEntry("level.dat")!.Open());Assert.Equal("changed",await reader.ReadToEndAsync());
        }
        finally {Directory.Delete(root,true);}
    }
}
