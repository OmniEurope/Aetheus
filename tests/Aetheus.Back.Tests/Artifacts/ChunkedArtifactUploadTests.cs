// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Artifacts;

/// <summary>
/// The upload path for an artifact a single request cannot carry. What matters here is not that the
/// happy path works but that the refusals do: the reason this exists at all is that the previous
/// answer (four separate artifacts reassembled by `cat` in three YAML files) verified nothing.
/// </summary>
public sealed class ChunkedArtifactUploadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"aetheus-chunked-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly ChunkedArtifactUploadService _sut;

    public ChunkedArtifactUploadTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = _root })
            .Build();
        _sut = new ChunkedArtifactUploadService(configuration, _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static string Sha(params byte[][] parts)
    {
        using var sha = SHA256.Create();
        var all = parts.SelectMany(part => part).ToArray();
        return Convert.ToHexStringLower(sha.ComputeHash(all));
    }

    private Task<ChunkedUploadSession> BeginAsync(int totalParts, string digest) =>
        _sut.BeginAsync(7, "payload.tar.gz", "Build", totalParts, digest, TestContext.Current.CancellationToken);

    private Task<ChunkedPartResult> SendAsync(string uploadId, int index, byte[] content, string? digest = null) =>
        _sut.AcceptPartAsync(
            7, uploadId, index, digest ?? Sha(content), new MemoryStream(content),
            TestContext.Current.CancellationToken);

    /// <summary>Completes the upload and returns the bytes of the verified file handed to the publisher.</summary>
    private Task<byte[]> AssembleAsync(string uploadId) =>
        _sut.CompleteAsync(7, uploadId,
            file => File.ReadAllBytesAsync(file.Path, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task ThePublisherReceivesTheVerifiedFile_AndTheSessionIsGoneAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Bytes("verified payload");
        var session = await BeginAsync(1, Sha(content));
        await SendAsync(session.UploadId, 0, content);

        var handed = await _sut.CompleteAsync(7, session.UploadId, file => Task.FromResult(file), ct);

        Assert.Equal(Sha(content), handed.Sha256);
        Assert.Equal(content.Length, handed.Length);
        Assert.False(File.Exists(handed.Path));
        await Assert.ThrowsAsync<NotFoundException>(() => AssembleAsync(session.UploadId));
    }

    [Fact]
    public async Task ARefusedPublication_DeletesTheAssembledCopyAndKeepsThePartsForARetry()
    {
        // Recette R1-91: the store now takes the assembled file itself; a quota refusal must neither
        // leave a gigabyte copy behind nor cost the agent the parts it already sent.
        var ct = TestContext.Current.CancellationToken;
        var content = Bytes("retry me");
        var session = await BeginAsync(1, Sha(content));
        await SendAsync(session.UploadId, 0, content);
        string? assembledPath = null;

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CompleteAsync<int>(7, session.UploadId, file =>
        {
            assembledPath = file.Path;
            throw new ConflictException("quota");
        }, ct));

        Assert.False(File.Exists(assembledPath));
        Assert.Equal("retry me", System.Text.Encoding.UTF8.GetString(await AssembleAsync(session.UploadId)));
    }

    [Fact]
    public async Task PartsAreAssembledInOrderAndTheWholeDigestIsVerified()
    {
        var first = Bytes("hello ");
        var second = Bytes("world");
        var session = await BeginAsync(2, Sha(first, second));

        Assert.True((await SendAsync(session.UploadId, 0, first)).Accepted);
        Assert.True((await SendAsync(session.UploadId, 1, second)).Accepted);

        Assert.Equal("hello world", System.Text.Encoding.UTF8.GetString(await AssembleAsync(session.UploadId)));
    }

    [Fact]
    public async Task APartWhoseBytesDoNotMatchItsAnnouncedDigestIsRefused()
    {
        // The whole point: a part corrupted in flight is rejected on arrival rather than assembled
        // into the artifact that gets deployed.
        var session = await BeginAsync(1, Sha(Bytes("good")));

        var result = await SendAsync(session.UploadId, 0, Bytes("corrupted"), digest: Sha(Bytes("good")));

        Assert.False(result.Accepted);
        Assert.Contains("does not match", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResendingAPartAfterAFailureCompletesTheUpload()
    {
        // Resumption: a network cut costs the part, not the gigabyte. The first attempt is refused,
        // the second carries the right bytes, and the session keeps everything it already had.
        var first = Bytes("aaaa");
        var second = Bytes("bbbb");
        var session = await BeginAsync(2, Sha(first, second));

        Assert.True((await SendAsync(session.UploadId, 0, first)).Accepted);
        Assert.False((await SendAsync(session.UploadId, 1, Bytes("truncated"), digest: Sha(second))).Accepted);
        var retry = await SendAsync(session.UploadId, 1, second);

        Assert.True(retry.Accepted);
        Assert.Equal(2, retry.ReceivedParts);
        Assert.Equal(8, (await AssembleAsync(session.UploadId)).Length);
    }

    [Fact]
    public async Task CompletingWithAMissingPartIsRefusedAndNamesTheCount()
    {
        var session = await BeginAsync(3, Sha(Bytes("x")));
        await SendAsync(session.UploadId, 0, Bytes("x"));

        var error = await Assert.ThrowsAsync<BadRequestException>(
            () => AssembleAsync(session.UploadId));

        Assert.Contains("2 of 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartsThatAreEachValidButDoNotFormTheAnnouncedArtifactAreRefused()
    {
        // Every part matched its own digest and the whole still does not: a wrong count, a wrong
        // order, or a session resumed against a different build.
        var session = await BeginAsync(1, Sha(Bytes("expected")));
        await SendAsync(session.UploadId, 0, Bytes("valid but other"));

        await Assert.ThrowsAsync<BadRequestException>(
            () => AssembleAsync(session.UploadId));
    }

    [Fact]
    public async Task AnUploadBelongingToAnotherRunIsNotVisible()
    {
        // The controller proves the agent runs the run it names; this closes the rest, an agent
        // using another run's upload id to publish into that run.
        var session = await BeginAsync(1, Sha(Bytes("x")));

        await Assert.ThrowsAsync<NotFoundException>(
            () => _sut.AcceptPartAsync(
                8, session.UploadId, 0, Sha(Bytes("x")), new MemoryStream(Bytes("x")),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("not-hex-at-all-not-hex-at-all-nn")]
    [InlineData("short")]
    public async Task AnUploadIdThatIsNotTheIssuedTokenIsRefused(string uploadId)
    {
        await Assert.ThrowsAsync<BadRequestException>(
            () => _sut.AcceptPartAsync(
                7, uploadId, 0, Sha(Bytes("x")), new MemoryStream(Bytes("x")),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnfinishedUploadIsPurgedOnceItIsOldEnough()
    {
        var session = await BeginAsync(2, Sha(Bytes("x")));
        await SendAsync(session.UploadId, 0, Bytes("x"));

        _time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, _sut.PurgeExpired(TimeSpan.FromHours(24)));

        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, _sut.PurgeExpired(TimeSpan.FromHours(24)));
        await Assert.ThrowsAsync<NotFoundException>(
            () => AssembleAsync(session.UploadId));
    }

    [Fact]
    public async Task ADeclaredPartCountOutsideTheAllowedRangeIsRefusedAtBegin()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => BeginAsync(0, Sha(Bytes("x"))));
        await Assert.ThrowsAsync<BadRequestException>(() => BeginAsync(10_001, Sha(Bytes("x"))));
    }

    [Fact]
    public async Task ADigestThatIsNotASha256IsRefusedAtBegin()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => BeginAsync(1, "not-a-digest"));
    }

    // --- Audit AUD-04 and AUD-06 (recette R1-50) ---

    [Fact]
    public async Task APartThatWouldExceedTheProjectQuota_IsRefusedAndNotStored()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ArtifactStorage:BasePath"] = _root,
                ["ArtifactStorage:ProjectQuotaBytes"] = "8"
            })
            .Build();
        var sut = new ChunkedArtifactUploadService(configuration, _time);
        var ct = TestContext.Current.CancellationToken;
        var first = Bytes("12345");
        var second = Bytes("67890");
        var session = await sut.BeginAsync(7, "payload.tar.gz", "Build", 2, Sha(first, second), ct);

        Assert.True((await sut.AcceptPartAsync(7, session.UploadId, 0, Sha(first), new MemoryStream(first), ct)).Accepted);
        var refused = await sut.AcceptPartAsync(7, session.UploadId, 1, Sha(second), new MemoryStream(second), ct);

        Assert.False(refused.Accepted);
        Assert.Contains("quota", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refused.ReceivedParts);
    }

    [Fact]
    public async Task AnotherRunCannotDiscardTheUpload_ItsOwnRunCan()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Bytes("payload");
        var session = await BeginAsync(1, Sha(content));

        Assert.False(await _sut.DiscardForRunAsync(8, session.UploadId, ct));
        Assert.True((await SendAsync(session.UploadId, 0, content)).Accepted);

        Assert.True(await _sut.DiscardForRunAsync(7, session.UploadId, ct));
        await Assert.ThrowsAsync<NotFoundException>(() => AssembleAsync(session.UploadId));
    }
}
