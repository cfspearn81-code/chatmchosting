using System.Collections.Concurrent;
namespace Harbor.Foundation;

/// <summary>One coordinator shared by all services; locks are deliberately non-recursive.</summary>
public sealed class OperationCoordinator : Harbor.Core.IOperationCoordinator
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    /// <summary>Call while holding the operation lease before any operation requiring a stopped server.
    /// Detects ownership in every Harbor process, not merely this window's in-memory state.</summary>
    public static void AssertStopped(Guid serverId)
    {
        var directory = Path.Combine(FoundationPaths.State, "locks"); Directory.CreateDirectory(directory);
        try { using var probe = new FileStream(Path.Combine(directory,serverId+".process"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); ProcessOwnership.AssertNoSurvivingProcess(serverId); }
        catch (IOException e) { throw new InvalidOperationException("Stop this server in its owning Harbor application before editing its data.",e); }
    }
    public async Task<IAsyncDisposable> AcquireAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        var gate = gates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(FoundationPaths.State, "locks"); Directory.CreateDirectory(directory);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return new Lease(gate, new FileStream(Path.Combine(directory, serverId + ".operation"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                catch (IOException) { await Task.Delay(100, cancellationToken); }
            }
        }
        catch { gate.Release(); throw; }
    }
    private sealed class Lease(SemaphoreSlim gate, FileStream file) : IAsyncDisposable
    {
        private int released;
        public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref released, 1) == 0) { file.Dispose(); gate.Release(); } return ValueTask.CompletedTask; }
    }
}
