// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Core.Tests.Services;

/// <summary>
/// The agent half of the chunked upload: the digests it announces must be the digests of the bytes
/// it will send, because the server refuses a part that does not match and a whole that does not
/// match. A plan that is right about one and wrong about the other would refuse every large upload.
/// </summary>
public sealed class ArtifactChunkPlanTests(ArtifactChunkPlanTests.MultiChunkPlan multiChunk)
    : IClassFixture<ArtifactChunkPlanTests.MultiChunkPlan>
{
    private static string Sha(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++) bytes[index] = (byte)(index % 251);
        return bytes;
    }

    [Fact]
    public async Task ASmallArtifactIsOnePartWhoseDigestIsTheWholeDigest()
    {
        var content = Pattern(1024);

        var plan = await ArtifactChunkPlan.BuildAsync(
            new MemoryStream(content), TestContext.Current.CancellationToken);

        var chunk = Assert.Single(plan.Chunks);
        Assert.Equal(Sha(content), plan.Sha256);
        Assert.Equal(plan.Sha256, chunk.Sha256);
        Assert.Equal(0, chunk.Offset);
        Assert.Equal(content.Length, chunk.Length);
    }

    [Fact]
    public void EachPartDigestIsTheDigestOfExactlyItsOwnBytes()
    {
        // The property the server checks part by part, on the real production loop over an artifact
        // one tail past the 256 MiB part size. Verified against hashes taken independently of the
        // plan, so a plan that hashed the wrong window would be caught.
        var plan = multiChunk.Plan;

        Assert.Equal(2, plan.Chunks.Count);
        Assert.Equal(multiChunk.IndependentPartDigests, plan.Chunks.Select(chunk => chunk.Sha256));
        Assert.Equal(multiChunk.IndependentWholeDigest, plan.Sha256);
    }

    [Fact]
    public void ThePartsCoverTheArtifactExactlyOnce()
    {
        var plan = multiChunk.Plan;

        Assert.Equal([0, 1], plan.Chunks.Select(chunk => chunk.Index));
        Assert.Equal(0, plan.Chunks[0].Offset);
        Assert.Equal(ArtifactChunkPlan.ChunkSizeBytes, plan.Chunks[0].Length);
        Assert.Equal(MultiChunkPlan.Tail, plan.Chunks[1].Length);
        Assert.Equal(MultiChunkPlan.Length, plan.Chunks.Sum(chunk => chunk.Length));
        for (var index = 1; index < plan.Chunks.Count; index++)
            Assert.Equal(
                plan.Chunks[index - 1].Offset + plan.Chunks[index - 1].Length,
                plan.Chunks[index].Offset);
    }

    [Fact]
    public async Task TheStreamIsLeftWhereItWasFound()
    {
        // The caller sends the parts from the same stream, so a plan that consumed it would send
        // nothing at all.
        var stream = new MemoryStream(Pattern(1024));

        await ArtifactChunkPlan.BuildAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void AWindowReadsOnlyItsOwnPartAndCanBeReadAgainAfterAFailedAttempt()
    {
        // What makes a retry safe: a fresh window over the same file re-reads the same bytes, unlike
        // the single-request upload whose stream was already partially consumed.
        var stream = new PatternStream(MultiChunkPlan.Length);
        var second = multiChunk.Plan.Chunks[1];

        static byte[] Drain(Stream window)
        {
            using var buffer = new MemoryStream();
            window.CopyTo(buffer);
            return buffer.ToArray();
        }

        var first = Drain(new ArtifactChunkStream(stream, second));
        var again = Drain(new ArtifactChunkStream(stream, second));

        Assert.Equal(PatternStream.ExpectedBytes(ArtifactChunkPlan.ChunkSizeBytes, MultiChunkPlan.Tail), first);
        Assert.Equal(first, again);
    }

    [Fact]
    public async Task ANonSeekableStreamIsRefusedRatherThanSilentlySentWhole()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => ArtifactChunkPlan.BuildAsync(new NonSeekableStream(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The real plan over an artifact of one 256 MiB part plus a tail, built once for the class. The
    /// bytes are generated from their position rather than allocated, and the expected digests are
    /// computed by a separate pass with a different read size, independent of the production loop.
    /// </summary>
    public sealed class MultiChunkPlan : IAsyncLifetime
    {
        internal const long Tail = 700;
        internal const long Length = ArtifactChunkPlan.ChunkSizeBytes + Tail;

        internal ArtifactChunkPlan.Plan Plan { get; private set; } = null!;
        internal List<string> IndependentPartDigests { get; } = [];
        internal string IndependentWholeDigest { get; private set; } = string.Empty;

        public async ValueTask InitializeAsync()
        {
            Plan = await ArtifactChunkPlan.BuildAsync(new PatternStream(Length));

            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var stream = new PatternStream(Length);
            var buffer = new byte[1 << 20];
            foreach (var partLength in new[] { ArtifactChunkPlan.ChunkSizeBytes, Tail })
            {
                using var part = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var remaining = partLength; remaining > 0;)
                {
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    part.AppendData(buffer, 0, read);
                    whole.AppendData(buffer, 0, read);
                    remaining -= read;
                }
                IndependentPartDigests.Add(Convert.ToHexStringLower(part.GetHashAndReset()));
            }
            IndependentWholeDigest = Convert.ToHexStringLower(whole.GetHashAndReset());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A seekable, read-only stream whose byte at position p is p % 251: any window read at
    /// the wrong offset hashes differently, and nothing is allocated for its length.</summary>
    private sealed class PatternStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        internal static byte[] ExpectedBytes(long offset, long count)
        {
            var bytes = new byte[count];
            for (var index = 0L; index < count; index++) bytes[index] = (byte)((offset + index) % 251);
            return bytes;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Math.Max(0, length - Position));
            var value = (int)(Position % 251);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)value;
                value = value == 250 ? 0 : value + 1;
            }
            Position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
