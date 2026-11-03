using System.Net;
using System.Security.Cryptography;
using System.Text;
using Harbor.Core;
using Harbor.Foundation;
using Harbor.Management;
using Xunit;

namespace Harbor.Tests;

public sealed class BackendTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "Harbor tests ü " + Guid.NewGuid().ToString("N"));
    public BackendTests() => Directory.CreateDirectory(temp);
    public void Dispose() => Directory.Delete(temp, true);

    [Fact]
    public async Task RepositoryPersistsStableIdentityAndRejectsUnknownSchema()
    {
        var repository = new ServerRepository(temp);
        var original = new ServerDefinition { Name = "World café", MinecraftVersion = "1.21.1", Directory = Path.Combine(temp, "世界"), Port = 25577 };
        await repository.SaveAsync(original);
        var restored = Assert.Single(await new ServerRepository(temp).ListAsync());
        Assert.Equal(original, restored);
        await repository.SaveAsync(original with { Name = "Changed" });
        Assert.Equal("Changed", Assert.Single(await repository.ListAsync()).Name);
        await File.WriteAllTextAsync(Path.Combine(temp, original.Id + ".json"), "{\"SchemaVersion\":999,\"Server\":{}}");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ListAsync());
    }

    [Fact]
    public async Task CoordinatorSerializesAndCancellationDoesNotLeakGate()
    {
        var coordinator = new OperationCoordinator(); var id = Guid.NewGuid();
        var first = await coordinator.AcquireAsync(id);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AcquireAsync(id, cancellation.Token));
        await first.DisposeAsync();
        await using var next = await coordinator.AcquireAsync(id);
        await using var other = await coordinator.AcquireAsync(Guid.NewGuid());
    }

    [Fact]
    public void PropertiesRoundTripEscapesCommentsUnknownKeysAndContinuation()
    {
        const string input = "# retain me\nunknown\\ key = hello\\\n  world\nmotd=\\u00e9\nserver-port=25565\n";
        var properties = JavaProperties.Parse(input);
        Assert.Equal("helloworld", properties.Values["unknown key"]);
        Assert.Equal("é", properties.Values["motd"]);
        properties.Set("motd", "café\\server\nline");
        var output = properties.ToString();
        Assert.Contains("# retain me", output);
        Assert.Equal("café\\server\nline", JavaProperties.Parse(output).Values["motd"]);
        Assert.Equal("helloworld", JavaProperties.Parse(output).Values["unknown key"]);
    }

    [Theory]
    [InlineData("server-port", "65536")]
    [InlineData("level-name", "../world")]
    [InlineData("view-distance", "1")]
    [InlineData("online-mode", "yes")]
    public void PropertiesRejectDangerousOrInvalidValues(string key, string value)
        => Assert.Throws<ArgumentException>(() => JavaProperties.Validate(key, value));

    [Fact]
    public async Task DownloadChecksumFailurePreservesOriginalAndRemovesStage()
    {
        var destination = Path.Combine(temp, "artifact.jar"); await File.WriteAllTextAsync(destination, "original");
        using var client = new HttpClient(new FixedHandler("corrupted"));
        var downloader = new VerifiedDownloader(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(new DownloadArtifact(new Uri("https://example.test/file"), destination, new string('0',64))));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Single(Directory.GetFiles(temp));
    }

    [Fact]
    public async Task DownloadCommitsOnlyVerifiedPayload()
    {
        const string content = "verified server bytes";
        using var client = new HttpClient(new FixedHandler(content));
        var destination = Path.Combine(temp, "世界 artifact.jar");
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        await new VerifiedDownloader(client).DownloadAsync(new DownloadArtifact(new Uri("https://example.test/file"), destination, checksum));
        Assert.Equal(content, await File.ReadAllTextAsync(destination));
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
