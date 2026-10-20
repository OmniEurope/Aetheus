// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Locks down the contract the installer relies on: every failure mode must
/// resolve to <c>Ok = false</c> with a short human message, well within the
/// installer's expectations. We deliberately avoid hitting the network in
/// tests - the timeout-only branch and the URL-validation branch are enough
/// to keep the protocol stable, and a real HTTP call would make CI flaky.
/// </summary>
public class BackendProbeTests
{
    [Fact]
    public async Task ProbeAsync_EmptyUrl_ReturnsClearMessage()
    {
        var result = await BackendProbe.ProbeAsync(
            string.Empty, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("ServerUrl is empty", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_MalformedUrl_ReturnsClearMessage()
    {
        var result = await BackendProbe.ProbeAsync(
            "not-a-url", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("not a valid", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // S-TECH-F2J7: drive the unreachable branch through a fake handler instead of a real call to
    // TEST-NET-1 - the old version made a live network attempt and was flaky under parallel load.
    [Fact]
    public async Task ProbeAsync_UnreachableHost_ReturnsClearMessage()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));

        var result = await BackendProbe.ProbeAsync(
            "https://192.0.2.1", TimeSpan.FromSeconds(2), handler,
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("unreachable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Deterministic timeout branch: the handler honours the client's timeout token and is cancelled,
    // so HttpClient surfaces TaskCanceledException → the "did not respond" message. No real wall-clock
    // wait beyond the tiny timeout.
    [Fact]
    public async Task ProbeAsync_Timeout_ReturnsTimedOutMessage()
    {
        var handler = new StubHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        var result = await BackendProbe.ProbeAsync(
            "https://example.com", TimeSpan.FromMilliseconds(50), handler,
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("did not respond", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_Healthy_ReturnsOk()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        var result = await BackendProbe.ProbeAsync(
            "https://example.com", TimeSpan.FromSeconds(2), handler,
            TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(200, result.HttpStatus);
    }

    [Theory]
    [InlineData(new string[0], 10)]
    [InlineData(new[] { "--timeout", "25" }, 25)]
    [InlineData(new[] { "--TIMEOUT", "25" }, 25)]
    [InlineData(new[] { "--timeout", "600" }, 60)]
    [InlineData(new[] { "--timeout", "0" }, 10)]
    [InlineData(new[] { "--timeout", "-3" }, 10)]
    [InlineData(new[] { "--timeout", "soon" }, 10)]
    [InlineData(new[] { "--timeout" }, 10)]
    [InlineData(new[] { "--probe-backend", "--timeout", "5" }, 5)]
    public void ParseTimeout_TakesAPositiveTimeoutCappedAtOneMinute(string[] args, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackendProbe.ParseTimeout(args));

    [Fact]
    public void CreateHandler_WithoutPinOrInsecureFlag_KeepsPlatformValidation()
        => Assert.Null(BackendProbe.CreateHandler("https://aetheus.example", allowInsecure: false, pinnedThumbprint: null));

    [Fact]
    public void CreateHandler_WithAPin_RejectsAMissingCertificate()
    {
        using var handler = BackendProbe.CreateHandler("https://aetheus.example", allowInsecure: true, pinnedThumbprint: "AA:BB");

        var callback = Assert.IsType<SocketsHttpHandler>(handler).SslOptions.RemoteCertificateValidationCallback!;
        // The pin wins over the insecure flag: no certificate never matches it.
        Assert.False(callback(this, null, null, System.Net.Security.SslPolicyErrors.None));
    }

    [Fact]
    public void CreateHandler_InsecureWithoutPin_AcceptsAnyCertificate()
    {
        using var handler = BackendProbe.CreateHandler("https://localhost:5001", allowInsecure: true, pinnedThumbprint: null);

        var callback = Assert.IsType<SocketsHttpHandler>(handler).SslOptions.RemoteCertificateValidationCallback!;
        Assert.True(callback(this, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "AA:BB")]
    public async Task RunWithTlsOptions_AnUnusableServerUrl_ExitsWithOne(bool allowInsecure, string? pin)
        => Assert.Equal(1, await BackendProbe.RunWithTlsOptionsAsync(
            string.Empty, ["--timeout", "2"], allowInsecure, pin, TestContext.Current.CancellationToken));

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(cancellationToken);
    }
}
