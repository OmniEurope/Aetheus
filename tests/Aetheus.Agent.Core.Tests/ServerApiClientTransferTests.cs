// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The transfer paths of <see cref="ServerApiClient"/>: the chunked upload of an artifact over
/// the single-request threshold, and the download buffered to a self-deleting file.</summary>
public class ServerApiClientTransferTests
{
    private const string UploadId = "0123456789abcdef0123456789abcdef";

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, long BodyBytes)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long bytes = 0;
            if (request.Content is not null)
            {
                await using var body = await request.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0) bytes += read;
            }
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.PathAndQuery, bytes));
            return route(request);
        }
    }

    /// <summary>A seekable stream of zeros of any length, so a part-sized artifact costs no memory.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - Position);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            Position += n;
            return n;
        }

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

    private static (ServerApiClient Sut, RoutingHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var handler = new RoutingHandler(route);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5301/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        return (new ServerApiClient(factory, new AgentState(TimeProvider.System)), handler);
    }

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage UploadRoute(HttpRequestMessage request, HttpStatusCode completeStatus)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/begin", StringComparison.Ordinal)) return Json(new { uploadId = UploadId });
        if (path.Contains("/part/", StringComparison.Ordinal)) return Json(new { accepted = true, receivedParts = 1 });
        if (path.Contains("/complete/", StringComparison.Ordinal))
            return completeStatus == HttpStatusCode.OK
                ? Json(new PipelineArtifactDto { Id = 31, Name = "web" })
                : new HttpResponseMessage(completeStatus) { Content = new StringContent("digest mismatch") };
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AnArtifactOverTheThreshold_IsSentInDigestedPartsThenCompleted()
    {
        var (sut, handler) = Build(request => UploadRoute(request, HttpStatusCode.OK));
        var total = ArtifactChunkPlan.ChunkThresholdBytes + 1;

        var artifact = await sut.UploadArtifactAsync(5, "web", "Build", new ZeroStream(total), TestContext.Current.CancellationToken);

        Assert.Equal(31, artifact!.Id);
        var paths = handler.Requests.Select(r => r.Path).ToList();
        Assert.StartsWith("/api/artifacts/upload/5/begin?name=web&stageName=Build&totalParts=2&sha256=", paths[0], StringComparison.Ordinal);
        Assert.StartsWith($"/api/artifacts/upload/5/part/{UploadId}/0?sha256=", paths[1], StringComparison.Ordinal);
        Assert.StartsWith($"/api/artifacts/upload/5/part/{UploadId}/1?sha256=", paths[2], StringComparison.Ordinal);
        Assert.Equal($"/api/artifacts/upload/5/complete/{UploadId}?name=web&stageName=Build", paths[3]);
        Assert.Equal(total, handler.Requests[1].BodyBytes + handler.Requests[2].BodyBytes);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ARefusedCompletion_AbandonsTheUploadAndReportsTheRefusal()
    {
        var (sut, handler) = Build(request => UploadRoute(request, HttpStatusCode.BadRequest));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sut.UploadArtifactAsync(
            5, "web", null, new ZeroStream(ArtifactChunkPlan.ChunkThresholdBytes + 1), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("digest mismatch", error.Message, StringComparison.Ordinal);
        var last = handler.Requests[^1];
        Assert.Equal((HttpMethod.Delete, $"/api/artifacts/upload/5/{UploadId}"), (last.Method, last.Path));
    }

    [Fact]
    public async Task ADownloadTheServerRefuses_ReturnsNull()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Assert.Null(await sut.DownloadArtifactAsync(9, 4, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADownload_IsBufferedFromTheStartWithItsDigestAndSizeMeasured()
    {
        var payload = "artifact-bytes"u8.ToArray();
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

        await using var stream = await sut.DownloadArtifactAsync(9, 4, TestContext.Current.CancellationToken);

        Assert.Equal("/api/artifacts/agent-download/9?runId=4", handler.Requests.Single().Path);
        Assert.Equal(0, stream!.Position);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(payload, copy.ToArray());
        Assert.True(ArtifactDownloadTelemetry.TryGet(stream, out var measurement));
        Assert.Equal((Convert.ToHexStringLower(SHA256.HashData(payload)), (long)payload.Length), (measurement.Sha256, measurement.Bytes));
    }
}
