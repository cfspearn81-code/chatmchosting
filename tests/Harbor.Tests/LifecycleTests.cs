using System.Net;
using System.Net.Sockets;
using Harbor.Core;
using Harbor.Foundation;
using Harbor.Management;
using Xunit;

namespace Harbor.Tests;

/// <summary>Real OS-process fixtures; these are NOT Minecraft integration evidence.</summary>
public sealed class LifecycleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Harbor process tests 世界 " + Guid.NewGuid().ToString("N"));
    public LifecycleTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private ServerDefinition Definition(string name) => new() { Name = name, MinecraftVersion = "1.21.1", Directory = Path.Combine(directory, name), Port = FreePort(), EulaAccepted = true };
    private static LaunchPlan Fixture(ServerDefinition server) => new("/bin/sh", ["-c", "echo 'Done (0.1s)! For help, type help'; while IFS= read -r command; do if [ \"$command\" = stop ]; then exit 0; fi; echo \"fixture received: $command\"; done"], server.Directory);
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(20, deadline.Token);
    }

    [Fact]
    public async Task ConcurrentOwnedProcessesHaveIndependentCommandsAndGracefulExit()
    {
        await using var lifecycle = new ServerLifecycleManager(new OperationCoordinator());
        var first = Definition("one"); var second = Definition("two");
        while (second.Port == first.Port) second = second with { Port = FreePort() };
        var echoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifecycle.OutputReceived += (_, line) => { if (line.ServerId == first.Id && line.Text == "fixture received: list") echoed.TrySetResult(); };
        await lifecycle.StartAsync(first, Fixture(first)); await lifecycle.StartAsync(second, Fixture(second));
        await WaitForAsync(() => lifecycle.GetStatus(first.Id).State == ServerState.Running && lifecycle.GetStatus(second.Id).State == ServerState.Running);
        await lifecycle.SendCommandAsync(first.Id, "list");
        await echoed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(lifecycle.GetMetrics(first.Id)!.ProcessId, lifecycle.GetMetrics(second.Id)!.ProcessId);
        await using (var otherApplication = new ServerLifecycleManager(new OperationCoordinator()))
        {
            Assert.Equal(ServerState.Running, otherApplication.GetStatus(first.Id).State);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new PropertiesService(new OperationCoordinator(), otherApplication).SaveAsync(first, new Dictionary<string,string>{{"motd","blocked"}}));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => otherApplication.StartAsync(first, Fixture(first)));
            Assert.Equal(ServerState.Running,lifecycle.GetStatus(first.Id).State);
        }
        await lifecycle.StopAsync(first.Id);
        Assert.Equal(ServerState.Stopped, lifecycle.GetStatus(first.Id).State);
        Assert.Equal(ServerState.Running, lifecycle.GetStatus(second.Id).State);
        await lifecycle.StopAsync(second.Id);
        Assert.Equal(ServerState.Stopped, lifecycle.GetStatus(second.Id).State);
    }

    [Fact]
    public async Task RejectsDoubleStartAndConfiguredPortCollision()
    {
        await using var lifecycle = new ServerLifecycleManager(new OperationCoordinator());
        var first = Definition("one"); await lifecycle.StartAsync(first, Fixture(first));
        await WaitForAsync(() => lifecycle.GetStatus(first.Id).State == ServerState.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(first, Fixture(first)));
        Assert.Equal(ServerState.Running, lifecycle.GetStatus(first.Id).State);
        var collision = Definition("two") with { Port = first.Port };
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(collision, Fixture(collision)));
        await lifecycle.StopAsync(first.Id);
    }

    [Fact]
    public async Task RejectsEulaWithoutSpawningOrRecordingAcceptance()
    {
        await using var lifecycle = new ServerLifecycleManager(new OperationCoordinator());
        var server = Definition("unaccepted") with { EulaAccepted = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(server, Fixture(server)));
        Assert.False(File.Exists(Path.Combine(server.Directory,"eula.txt")));
        Assert.Null(lifecycle.GetMetrics(server.Id));
    }

    [Fact]
    public async Task RejectsActualBoundPort()
    {
        var listener = new TcpListener(IPAddress.Any, 0); listener.Start();
        try
        {
            await using var lifecycle = new ServerLifecycleManager(new OperationCoordinator());
            var server = Definition("collision") with { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(server, Fixture(server)));
        }
        finally { listener.Stop(); }
    }
}
