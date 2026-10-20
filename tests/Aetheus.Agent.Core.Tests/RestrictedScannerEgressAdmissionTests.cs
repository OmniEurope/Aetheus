// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// What the scanner egress proxy lets through, read from an in-memory connection (no socket): the request
/// must carry the proxy credentials (otherwise a 407 is written back) and name an audited destination;
/// anything else closes the connection without a byte relayed.
/// </summary>
public sealed class RestrictedScannerEgressAdmissionTests
{
    private const string Authorization = "Basic YWV0aGV1czpzZWNyZXQ=";
    private static readonly IPAddress Audited = IPAddress.Parse("203.0.113.7");

    private static IPAddress? AuditedOnly(string host, int port) =>
        host == "registry.example.com" && port == 443 ? Audited : null;

    private static async Task<(RestrictedScannerEgress.AdmittedRequest? Admitted, string Written)> AdmitAsync(
        string request, Func<string, int, IPAddress?>? allowed = null)
    {
        await using var connection = new ClientConnection(request);
        var admitted = await RestrictedScannerEgress.AdmitAsync(
            connection, Authorization, allowed ?? AuditedOnly, TestContext.Current.CancellationToken);
        return (admitted, connection.Written);
    }

    [Fact]
    public async Task AnAuthorizedConnectToAnAuditedDestination_IsAdmitted_WithItsAddress()
    {
        const string header = "CONNECT registry.example.com:443 HTTP/1.1\r\nProxy-Authorization: " + Authorization + "\r\n\r\n";

        var (admitted, written) = await AdmitAsync(header + "TLS");

        Assert.NotNull(admitted);
        Assert.Equal(new RestrictedScannerEgress.ProxyRequest(true, "registry.example.com", 443, header), admitted.Request);
        Assert.Equal(Audited, admitted.Address);
        Assert.Empty(written);
    }

    [Fact]
    public async Task ThePlainHttpRequestAdmitted_IsTheOneRewrittenForTheUpstream()
    {
        var seen = new List<(string Host, int Port)>();

        var (admitted, _) = await AdmitAsync(
            "GET http://mirror.example.com:8080/feed?x=1 HTTP/1.1\r\nproxy-authorization:  " + Authorization + "  \r\n\r\n",
            (host, port) => { seen.Add((host, port)); return Audited; });

        Assert.Equal([("mirror.example.com", 8080)], seen);
        Assert.NotNull(admitted);
        Assert.False(admitted.Request.Connect);
        Assert.StartsWith("GET /feed?x=1 HTTP/1.1\r\n", admitted.Request.Header, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONNECT registry.example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT registry.example.com:443 HTTP/1.1\r\nProxy-Authorization: Basic d3Jvbmc6d3Jvbmc=\r\n\r\n")]
    [InlineData("CONNECT registry.example.com:443 HTTP/1.1\r\nAuthorization: " + Authorization + "\r\n\r\n")]
    public async Task ARequestWithoutTheProxyCredentials_IsAnswered407_AndRefused(string request)
    {
        var (admitted, written) = await AdmitAsync(request);

        Assert.Null(admitted);
        Assert.StartsWith("HTTP/1.1 407 Proxy Authentication Required\r\n", written, StringComparison.Ordinal);
        Assert.Contains("Proxy-Authenticate: Basic realm=\"Aetheus\"\r\n", written, StringComparison.Ordinal);
        Assert.EndsWith("Connection: close\r\n\r\n", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONNECT other.example.com:443 HTTP/1.1\r\nProxy-Authorization: " + Authorization + "\r\n\r\n")]
    [InlineData("CONNECT registry.example.com:8443 HTTP/1.1\r\nProxy-Authorization: " + Authorization + "\r\n\r\n")]
    [InlineData("GET https://registry.example.com/ HTTP/1.1\r\nProxy-Authorization: " + Authorization + "\r\n\r\n")]
    [InlineData("GET /relative HTTP/1.1\r\nProxy-Authorization: " + Authorization + "\r\n\r\n")]
    public async Task AnAuthorizedRequestToAnythingNotAudited_IsRefusedSilently(string request)
    {
        var (admitted, written) = await AdmitAsync(request);

        Assert.Null(admitted);
        Assert.Empty(written);
    }

    [Fact]
    public async Task AConnectionClosedBeforeTheHeaderEnds_IsRefusedSilently()
    {
        var (admitted, written) = await AdmitAsync("CONNECT registry.example.com:443 HTTP/1.1\r\nProxy-Author");

        Assert.Null(admitted);
        Assert.Empty(written);
    }

    [Fact]
    public async Task AnAdmittedRequest_IsRelayedOnce_ToItsAuditedAddress()
    {
        await using var connection = new ClientConnection(
            "CONNECT registry.example.com:443 HTTP/1.1\r\nProxy-Authorization: " + Authorization + "\r\n\r\n");
        var relayed = new List<RestrictedScannerEgress.AdmittedRequest>();

        await RestrictedScannerEgress.ServeAsync(connection, Authorization, AuditedOnly,
            (admitted, _) => { relayed.Add(admitted); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var request = Assert.Single(relayed);
        Assert.Equal(("registry.example.com", 443, Audited), (request.Request.Host, request.Request.Port, request.Address));
    }

    [Theory]
    [InlineData("CONNECT registry.example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT other.example.com:443 HTTP/1.1\r\nProxy-Authorization: " + Authorization + "\r\n\r\n")]
    public async Task ARefusedRequest_IsNeverRelayed(string request)
    {
        await using var connection = new ClientConnection(request);
        var relays = 0;

        await RestrictedScannerEgress.ServeAsync(connection, Authorization, AuditedOnly,
            (_, _) => { relays++; return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, relays);
    }

    /// <summary>A client connection in memory: reads the bytes the client sent, records what the proxy writes back.</summary>
    private sealed class ClientConnection(string sent) : Stream
    {
        private readonly MemoryStream _input = new(Encoding.ASCII.GetBytes(sent));
        private readonly MemoryStream _output = new();

        public string Written => Encoding.ASCII.GetString(_output.ToArray());

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                _output.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
