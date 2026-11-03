using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Harbor.Core;
namespace Harbor.Foundation;

/// <summary>Authoritative process owner. Never adopts or signals processes from stale PID records.</summary>
public sealed class ServerLifecycleManager(IOperationCoordinator coordinator) : IServerLifecycleManager
{
    private sealed class Owned(Process process, FileStream ownership, StreamWriter log)
    {
        public Process Process { get; } = process;
        public FileStream Ownership { get; } = ownership;
        public StreamWriter Log { get; } = log;
        public SemaphoreSlim LogGate { get; } = new(1,1);
        public SemaphoreSlim InputGate { get; } = new(1,1);
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public Task Drain { get; set; } = Task.CompletedTask;
        public TimeSpan? LastCpu { get; set; }
        public DateTimeOffset LastSample { get; set; } = DateTimeOffset.UtcNow;
    }
    private readonly ConcurrentDictionary<Guid,Owned> processes = new();
    private readonly ConcurrentDictionary<Guid,ServerStatus> states = new();
    private readonly ConcurrentDictionary<Guid,int> ports = new();
    private readonly SemaphoreSlim starts = new(1,1);
    public event EventHandler<ConsoleLine>? OutputReceived;
    public event EventHandler<ServerStatus>? StatusChanged;
    public ServerStatus GetStatus(Guid serverId)
    {
        if (processes.ContainsKey(serverId)) return states.GetValueOrDefault(serverId) ?? new(serverId,ServerState.Starting);
        try { OperationCoordinator.AssertStopped(serverId); }
        catch (InvalidOperationException) { return new(serverId,ServerState.Running,Error:"Owned by another Harbor application; status and metrics unavailable here."); }
        return states.GetValueOrDefault(serverId) ?? new(serverId,ServerState.Stopped);
    }
    private void Set(Guid id,ServerState state,int? pid=null,string? error=null) { var value = new ServerStatus(id,state,pid,error); states[id]=value; StatusChanged?.Invoke(this,value); }
    public async Task StartAsync(ServerDefinition server, LaunchPlan plan, CancellationToken cancellationToken = default)
    {
        await using var operation = await coordinator.AcquireAsync(server.Id,cancellationToken);
        await starts.WaitAsync(cancellationToken);
        FileStream? ownership=null;
        try
        {
            ServerValidation.ThrowIfInvalid(server);
            if(processes.ContainsKey(server.Id)) throw new InvalidOperationException("Server already started.");
            if(!server.EulaAccepted) throw new InvalidOperationException("Explicit Minecraft EULA acceptance is required before starting. Read https://aka.ms/MinecraftEULA");
            if(server.Port is < 1 or > 65535 || server.MinimumMemoryMb < 128 || server.MaximumMemoryMb < server.MinimumMemoryMb) throw new ArgumentException("Invalid port or Java heap settings.");
            if(ports.Values.Contains(server.Port)) throw new InvalidOperationException("Another Harbor server uses this port.");
            var listener = new TcpListener(IPAddress.Any,server.Port);
            try { listener.Start(); } catch(SocketException e) { throw new InvalidOperationException("Minecraft port is already in use.",e); } finally { listener.Stop(); }
            Directory.CreateDirectory(Path.Combine(FoundationPaths.State,"locks"));
            try { ownership = new FileStream(Path.Combine(FoundationPaths.State,"locks",server.Id+".process"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch(IOException e) { throw new InvalidOperationException("This server is owned by another Harbor application.",e); }
            Directory.CreateDirectory(plan.WorkingDirectory);
            await File.WriteAllTextAsync(Path.Combine(plan.WorkingDirectory,"eula.txt"),"# Explicit acceptance recorded by Harbor\neula=true\n",cancellationToken);
            var info = new ProcessStartInfo(plan.Executable) { WorkingDirectory=plan.WorkingDirectory,UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
            foreach(var argument in plan.Arguments) info.ArgumentList.Add(argument);
            if(plan.Environment is not null) foreach(var pair in plan.Environment) info.Environment[pair.Key]=pair.Value;
            var process = new Process { StartInfo=info };
            Directory.CreateDirectory(Path.Combine(plan.WorkingDirectory,"logs"));
            var log = new StreamWriter(new FileStream(Path.Combine(plan.WorkingDirectory,"logs","harbor-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)) { AutoFlush=true };
            Set(server.Id,ServerState.Starting);
            try { process.Start(); } catch { log.Dispose();process.Dispose();throw; }
            var owned = new Owned(process,ownership,log); ownership=null;
            processes[server.Id]=owned; ports[server.Id]=server.Port;
            owned.Drain=Task.WhenAll(Pump(server.Id,owned,process.StandardOutput,false),Pump(server.Id,owned,process.StandardError,true));
            _ = ObserveExit(server.Id,owned);
        }
        catch(Exception e) { ownership?.Dispose();if(!processes.ContainsKey(server.Id)) Set(server.Id,ServerState.Failed,error:e.Message);throw; }
        finally { starts.Release(); }
    }
    private async Task Pump(Guid id,Owned owner,StreamReader stream,bool error)
    {
        while(await stream.ReadLineAsync() is { } text)
        {
            var line = new ConsoleLine(id,DateTimeOffset.UtcNow,text,error);
            await owner.LogGate.WaitAsync();
            try { await owner.Log.WriteLineAsync($"{line.Timestamp:O} {(error?"stderr":"stdout")} {text}"); } finally { owner.LogGate.Release(); }
            OutputReceived?.Invoke(this,line);
            if(text.Contains("Done (") && text.Contains("For help") && GetStatus(id).State==ServerState.Starting) Set(id,ServerState.Running,owner.Process.Id);
        }
    }
    private async Task ObserveExit(Guid id,Owned owner)
    {
        await owner.Process.WaitForExitAsync();
        try { await owner.Drain; } catch(Exception e) { Set(id,ServerState.Failed,error:"Log capture failed: "+e.Message); }
        var code=owner.Process.ExitCode;
        processes.TryRemove(id,out _);ports.TryRemove(id,out _);
        owner.Log.Dispose();owner.Ownership.Dispose();owner.Process.Dispose();
        Set(id,code==0?ServerState.Stopped:ServerState.Failed,error:code==0?null:$"Server exited with code {code}. Inspect logs for startup evidence.");
    }
    public async Task SendCommandAsync(Guid serverId,string command,CancellationToken cancellationToken=default)
    {
        ServerValidation.ValidateCommand(command);
        if(!processes.TryGetValue(serverId,out var owner)) throw new InvalidOperationException("Server is stopped.");
        await owner.InputGate.WaitAsync(cancellationToken);
        try { await owner.Process.StandardInput.WriteLineAsync(command.AsMemory(),cancellationToken); await owner.Process.StandardInput.FlushAsync(cancellationToken); }
        finally { owner.InputGate.Release(); }
    }
    public async Task StopAsync(Guid serverId,CancellationToken cancellationToken=default)
    {
        await using var operation=await coordinator.AcquireAsync(serverId,cancellationToken);
        if(!processes.TryGetValue(serverId,out var owner)) return;
        Set(serverId,ServerState.Stopping,owner.Process.Id);
        await SendCommandAsync(serverId,"stop",cancellationToken);
        await owner.Process.WaitForExitAsync(cancellationToken);
        await owner.Drain.WaitAsync(cancellationToken);
        // Ownership is released by ObserveExit; no unrelated PID is ever signalled.
        while(processes.ContainsKey(serverId)) await Task.Delay(10,cancellationToken);
    }
    public ProcessMetrics? GetMetrics(Guid serverId)
    {
        if(!processes.TryGetValue(serverId,out var owner)) return null;
        try
        {
            owner.Process.Refresh(); var now=DateTimeOffset.UtcNow;var cpu=owner.Process.TotalProcessorTime;
            var elapsed=(now-owner.LastSample).TotalSeconds; var percent=elapsed>0 && owner.LastCpu is { } previous ? (cpu-previous).TotalSeconds/elapsed*100:(double?)null;
            owner.LastSample=now;owner.LastCpu=cpu;
            return new(owner.Process.Id,now-owner.Started,owner.Process.WorkingSet64,percent);
        }
        catch(InvalidOperationException) { return null; }
    }
    public async ValueTask DisposeAsync()
    {
        foreach(var id in processes.Keys) { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));await StopAsync(id,timeout.Token); }
    }
}
