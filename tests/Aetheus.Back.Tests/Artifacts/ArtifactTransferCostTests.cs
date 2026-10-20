// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Aetheus.Back.Components.Artifacts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Artifacts;

/// <summary>Recette R1-90 to R1-92: the server-side cost of an artifact transfer, without the network.
/// Parts are received from memory, assembled, published into the content-addressed store and read
/// back, on the real file system, each phase timed. The default size keeps the suite fast; set
/// <c>AETHEUS_ARTIFACT_BENCH_MIB</c> (and <c>AETHEUS_ARTIFACT_BENCH_OUT</c> for a result file) to
/// measure a production-sized payload.</summary>
public sealed class ArtifactTransferCostTests : IDisposable
{
    private const int RunId = 7;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-transfer-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReceiveCompleteAndDownload_KeepTheBytesAndReportTheirCost()
    {
        var ct = TestContext.Current.CancellationToken;
        var totalMib = int.TryParse(Environment.GetEnvironmentVariable("AETHEUS_ARTIFACT_BENCH_MIB"), out var mib) ? mib : 8;
        const int parts = 4;
        var partBytes = totalMib * 1024L * 1024 / parts;
        var payload = new byte[partBytes];
        RandomNumberGenerator.Fill(payload);
        var partDigest = Convert.ToHexStringLower(SHA256.HashData(payload));
        string wholeDigest;
        using (var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (var i = 0; i < parts; i++) whole.AppendData(payload);
            wholeDigest = Convert.ToHexStringLower(whole.GetHashAndReset());
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = _root })
            .Build();
        var uploads = new ChunkedArtifactUploadService(configuration, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var storage = new ArtifactStorageService(configuration);
        var session = await uploads.BeginAsync(RunId, "payload", "Build", parts, wholeDigest, ct);

        var receive = Stopwatch.StartNew();
        for (var index = 0; index < parts; index++)
            Assert.True((await uploads.AcceptPartAsync(RunId, session.UploadId, index, partDigest, new MemoryStream(payload, writable: false), ct)).Accepted);
        receive.Stop();

        var complete = Stopwatch.StartNew();
        // The controller path: assemble and verify, then the store adopts the verified file.
        var (relativePath, storedDigest) = await uploads.CompleteAsync(RunId, session.UploadId, async verified =>
            (await storage.AdoptVerifiedArtifactAsync(1, 2, RunId, $"payload-{RunId}.zip", verified, ct), verified.Sha256), ct);
        complete.Stop();

        var download = Stopwatch.StartNew();
        string downloadedDigest;
        await using (var stored = storage.OpenArtifact(relativePath)!)
            downloadedDigest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stored, ct));
        download.Stop();

        Assert.Equal(wholeDigest, storedDigest);
        Assert.Equal(wholeDigest, downloadedDigest);
        Report(totalMib, parts, receive.Elapsed, complete.Elapsed, download.Elapsed);
    }

    private static void Report(int totalMib, int parts, TimeSpan receive, TimeSpan complete, TimeSpan download)
    {
        string Rate(TimeSpan elapsed) => (totalMib / elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"artifact-transfer size={totalMib}MiB parts={parts} receive={receive.TotalMilliseconds:F0}ms ({Rate(receive)} MiB/s) complete={complete.TotalMilliseconds:F0}ms ({Rate(complete)} MiB/s) download={download.TotalMilliseconds:F0}ms ({Rate(download)} MiB/s)");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable("AETHEUS_ARTIFACT_BENCH_OUT") is { Length: > 0 } output)
            File.AppendAllText(output, line + Environment.NewLine);
    }
}
