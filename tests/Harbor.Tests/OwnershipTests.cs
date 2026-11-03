using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Harbor.Core;
using Harbor.Foundation;
using Harbor.Management;
using Xunit;

namespace Harbor.Tests;

public sealed class OwnershipTests
{
    [Fact]
    public async Task SurvivingCrashEvidenceBlocksEditsAndStartWithoutSignalingProcess()
    {
        using var process=Process.Start(new ProcessStartInfo("/bin/sleep","60") {UseShellExecute=false})!;
        var id=Guid.NewGuid();var root=Path.Combine(Path.GetTempPath(),"Harbor crash fixture "+id);Directory.CreateDirectory(root);
        var records=Path.Combine(Harbor.Core.HarborPaths.StateDirectory,"process-records");Directory.CreateDirectory(records);
        var record=Path.Combine(records,id+".json");
        try
        {
            await File.WriteAllTextAsync(record,JsonSerializer.Serialize(new {SchemaVersion=1,Pid=process.Id,StartTimeUtcTicks=process.StartTime.ToUniversalTime().Ticks,CommandSha256=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync($"/proc/{process.Id}/cmdline")))}));
            var coordinator=new OperationCoordinator();await using var lifecycle=new ServerLifecycleManager(coordinator);
            var server=new ServerDefinition {Id=id,Directory=root,MinecraftVersion="1.21.1",EulaAccepted=true};
            Assert.Equal(ServerState.Running,lifecycle.GetStatus(id).State);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>new PropertiesService(coordinator,lifecycle).SaveAsync(server,new Dictionary<string,string>{{"motd","unsafe"}}));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>lifecycle.StartAsync(server,new LaunchPlan("/bin/true",[],root)));
            Assert.False(process.HasExited);Assert.False(File.Exists(Path.Combine(root,"server.properties")));
            // A stale record for an exited fixture must not keep the server locked or signal a reused PID.
            process.Kill();await process.WaitForExitAsync();
            Assert.NotEqual(ServerState.Running,lifecycle.GetStatus(id).State);
            await new PropertiesService(coordinator,lifecycle).SaveAsync(server,new Dictionary<string,string>{{"motd","safe after exit"}});
            Assert.Contains("safe after exit",await File.ReadAllTextAsync(Path.Combine(root,"server.properties")));
        }
        finally {if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}File.Delete(record);Directory.Delete(root,true);}
    }
}
