using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace Harbor.Foundation;

/// <summary>Crash evidence only: never adopts or signals a process from a saved PID.</summary>
internal static class ProcessOwnership
{
    private sealed record Record(int SchemaVersion,int Pid,long StartTimeUtcTicks,string CommandSha256);
    private static string Location(Guid id)=>Path.Combine(FoundationPaths.State,"process-records",id+".json");
    public static void Write(Guid id,Process process)
    {
        var path=Location(id);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var command=File.ReadAllBytes($"/proc/{process.Id}/cmdline");
        var record=new Record(1,process.Id,process.StartTime.ToUniversalTime().Ticks,Convert.ToHexString(SHA256.HashData(command)));
        var stage=path+".tmp-"+Guid.NewGuid().ToString("N");
        try { File.WriteAllText(stage,JsonSerializer.Serialize(record));File.Move(stage,path,true); }
        finally { File.Delete(stage); }
    }
    public static void Remove(Guid id) { File.Delete(Location(id)); }
    public static void AssertNoSurvivingProcess(Guid id)
    {
        var path=Location(id);if(!File.Exists(path)) return;
        Record record;
        try { record=JsonSerializer.Deserialize<Record>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty record."); }
        catch(Exception e) when(e is JsonException or IOException) { throw new InvalidOperationException("Harbor ownership evidence cannot be read; inspect the state/process-records file before changing server data.",e); }
        if(record.SchemaVersion!=1) throw new InvalidOperationException("Unsupported Harbor ownership record; cannot establish that this server is stopped.");
        try
        {
            using var process=Process.GetProcessById(record.Pid);
            if(process.HasExited || process.StartTime.ToUniversalTime().Ticks!=record.StartTimeUtcTicks) return;
            // PID + kernel start time and exact command identify the original launch. No signal is sent.
            var command=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes($"/proc/{record.Pid}/cmdline")));
            if(command!=record.CommandSha256) throw new InvalidOperationException("The saved process still exists but its command changed; stopped state cannot be established safely.");
            throw new SurvivingServerException($"Server process {record.Pid} survived a Harbor crash. Data changes and restart are blocked. Harbor cannot reconnect its console; stop it through a trusted external console or shut down the host cleanly, then retry.");
        }
        catch(ArgumentException) { }
        catch(InvalidOperationException e) when(e is not SurvivingServerException) { throw new InvalidOperationException("Cannot verify saved process ownership; server edits remain blocked.",e); }
        catch(IOException e) { throw new InvalidOperationException("Cannot verify saved process ownership; server edits remain blocked.",e); }
    }
    private sealed class SurvivingServerException(string message) : InvalidOperationException(message) { }
}
