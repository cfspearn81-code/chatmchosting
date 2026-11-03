using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Harbor.Core;

namespace Harbor.Management;

public sealed record PlayerIdentity([property: JsonPropertyName("uuid")] Guid Uuid, [property: JsonPropertyName("name")] string Name);
public sealed record ConnectedPlayers(bool Available, IReadOnlyList<string> Names, DateTimeOffset ObservedAt, string? Explanation);
public sealed class PlayerService(IOperationCoordinator coordinator, IServerLifecycleManager lifecycle)
{
    private static readonly Regex PlayerName = new("^[A-Za-z0-9_]{1,16}$", RegexOptions.CultureInvariant);
    private static readonly Regex ListResponse = new(@"There are \d+ of a max of \d+ players online:\s*(.*)$", RegexOptions.CultureInvariant);
    private readonly StoppedOperations guard = new(coordinator, lifecycle);
    public async Task<IReadOnlyList<PlayerIdentity>> ReadWhitelistAsync(ServerDefinition server, CancellationToken ct = default)
    {
        await using var lease = await coordinator.AcquireAsync(server.Id, ct);
        var path = StoppedOperations.Child(server.Directory, "whitelist.json");
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<PlayerIdentity>>(stream, cancellationToken: ct) ?? [];
    }
    /// <summary>Stopped editing requires a genuine caller-resolved UUID. No offline UUID is invented.</summary>
    public async Task SetWhitelistAsync(ServerDefinition server, PlayerIdentity player, bool add, CancellationToken ct = default)
    {
        ValidateName(player.Name);
        if (player.Uuid == Guid.Empty) throw new ArgumentException("Supply the player's genuine Java identity UUID; offline identities must be handled explicitly.");
        await using var lease = await guard.EnterAsync(server, ct);
        var path = StoppedOperations.Child(server.Directory, "whitelist.json");
        var players = File.Exists(path) ? JsonSerializer.Deserialize<List<PlayerIdentity>>(await File.ReadAllTextAsync(path, ct)) ?? [] : new List<PlayerIdentity>();
        players.RemoveAll(p => p.Uuid == player.Uuid || p.Name.Equals(player.Name, StringComparison.OrdinalIgnoreCase));
        if (add) players.Add(player);
        await StoppedOperations.AtomicWriteAsync(path, JsonSerializer.Serialize(players, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
    public Task WhitelistCommandAsync(ServerDefinition server, string name, bool add, CancellationToken ct = default)
    { ValidateName(name); return lifecycle.SendCommandAsync(server.Id, "whitelist " + (add ? "add " : "remove ") + name, ct); }
    public Task OperatorCommandAsync(ServerDefinition server, string name, bool grant, bool confirmed, CancellationToken ct = default)
    {
        ValidateName(name); if (!confirmed) throw new InvalidOperationException("Confirm operator permission changes before continuing.");
        return lifecycle.SendCommandAsync(server.Id, (grant ? "op " : "deop ") + name, ct);
    }
    public Task KickAsync(ServerDefinition server, string name, bool confirmed, CancellationToken ct = default)
    {
        ValidateName(name); if (!confirmed) throw new InvalidOperationException("Confirm kicking this player before continuing.");
        return lifecycle.SendCommandAsync(server.Id, "kick " + name, ct);
    }
    public async Task<ConnectedPlayers> GetConnectedAsync(ServerDefinition server, CancellationToken ct = default)
    {
        if (lifecycle.GetStatus(server.Id).State != ServerState.Running) return new(false, [], DateTimeOffset.UtcNow, "Player count is unavailable while the server is not ready.");
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput(object? sender, ConsoleLine line)
        {
            if (line.ServerId != server.Id) return;
            var match = ListResponse.Match(line.Text); if (!match.Success) return;
            var names = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (names.All(PlayerName.IsMatch)) completion.TrySetResult(names);
        }
        lifecycle.OutputReceived += OnOutput;
        try
        {
            await lifecycle.SendCommandAsync(server.Id, "list", ct);
            try { return new(true, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), ct), DateTimeOffset.UtcNow, null); }
            catch (TimeoutException) { return new(false, [], DateTimeOffset.UtcNow, "No recognized player-list response was received; localized or modified server output may be unsupported."); }
        }
        finally { lifecycle.OutputReceived -= OnOutput; }
    }
    private static void ValidateName(string name)
    { if (!PlayerName.IsMatch(name)) throw new ArgumentException("Use a Java player name of 1–16 letters, digits, or underscores."); }
}
