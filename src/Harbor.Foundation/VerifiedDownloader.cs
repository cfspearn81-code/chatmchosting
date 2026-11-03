using System.Security.Cryptography;
namespace Harbor.Foundation;

/// <summary>Single staged downloader. TLS validation remains enabled; published hashes are mandatory when supplied.</summary>
public sealed class VerifiedDownloader : Harbor.Core.IVerifiedDownloader
{
    public Task DownloadAsync(Harbor.Core.DownloadArtifact artifact, IProgress<Harbor.Core.DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        => DownloadCoreAsync(artifact.Url, artifact.DestinationPath, artifact.Checksum, artifact.ChecksumAlgorithm, null, progress, cancellationToken);
    private readonly HttpClient client;
    public VerifiedDownloader(HttpClient? client = null) => this.client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    public Task DownloadAsync(Uri uri, string destination, string? checksum = null, string? algorithm = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => DownloadCoreAsync(uri,destination,checksum,algorithm,progress,null,cancellationToken);
    private async Task DownloadCoreAsync(Uri uri, string destination, string? checksum, string? algorithm, IProgress<double>? progress, IProgress<Harbor.Core.DownloadProgress>? byteProgress, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Downloads require HTTPS.", nameof(uri));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var stage = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; long total = 0;
                while (true)
                {
                    var n = await source.ReadAsync(buffer, cancellationToken); if (n == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken); total += n;
                    byteProgress?.Report(new Harbor.Core.DownloadProgress(total,response.Content.Headers.ContentLength));
                    if (response.Content.Headers.ContentLength is > 0) progress?.Report((double)total / response.Content.Headers.ContentLength.Value);
                }
                await output.FlushAsync(cancellationToken);
            }
            if (checksum is not null)
            {
                using var hasher = (algorithm ?? "SHA256").Replace("-", "").ToUpperInvariant() switch
                { "SHA1" => (HashAlgorithm)SHA1.Create(), "SHA256" => SHA256.Create(), "SHA512" => SHA512.Create(), _ => throw new ArgumentException("Unsupported checksum algorithm.") };
                await using var data = File.OpenRead(stage);
                var actual = Convert.ToHexString(await hasher.ComputeHashAsync(data, cancellationToken));
                if (!actual.Equals(checksum.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded artifact checksum did not match the published checksum.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stage, destination, true); progress?.Report(1);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
