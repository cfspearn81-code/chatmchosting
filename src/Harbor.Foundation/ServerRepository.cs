using System.Text.Json;
using Harbor.Core;
namespace Harbor.Foundation;
public sealed class ServerRepository : IServerRepository
{
    private readonly string directory;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public ServerRepository(string? directory = null) { this.directory = directory ?? Path.Combine(FoundationPaths.Config, "servers"); System.IO.Directory.CreateDirectory(this.directory); }
    private sealed record Document(int SchemaVersion, ServerDefinition Server);
    public async Task<IReadOnlyList<ServerDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ServerDefinition>();
        foreach(var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            await using var input = File.OpenRead(file);
            var document = await JsonSerializer.DeserializeAsync<Document>(input, Options, cancellationToken) ?? throw new InvalidDataException($"Invalid server definition: {file}");
            if(document.SchemaVersion != 1) throw new InvalidDataException($"Unsupported server schema {document.SchemaVersion}: {file}");
            ServerValidation.ThrowIfInvalid(document.Server);
            result.Add(document.Server);
        }
        return result.OrderBy(x => x.Name).ToArray();
    }
    public async Task SaveAsync(ServerDefinition server, CancellationToken cancellationToken = default)
    {
        ServerValidation.ThrowIfInvalid(server);
        var path = Path.Combine(directory, server.Id + ".json"); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Document(1,server),Options), cancellationToken);
            File.Move(temp,path,true);
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); File.Delete(Path.Combine(directory,id+".json")); return Task.CompletedTask; }
}
