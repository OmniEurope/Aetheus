// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using Aetheus.Back.Components.Analysis;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Tests.Analysis;

public sealed class DependencyTrackClientTests
{
    [Fact]
    public async Task GetVulnerabilitiesAsync_ParsesBoundedResponse()
    {
        var client = Create("""
            [{"vulnerability":{"vulnId":"CVE-2026-1","severity":"HIGH"},
              "component":{"name":"demo","version":"1.0","purl":"pkg:nuget/demo@1.0"},
              "analysis":{"state":"NOT_SET"}}]
            """);

        var result = await client.GetVulnerabilitiesAsync("project", TestContext.Current.CancellationToken);

        var finding = Assert.Single(result);
        Assert.Equal("CVE-2026-1", finding.VulnerabilityId);
        Assert.Equal(AnalysisSeverity.High, finding.Severity);
    }

    [Fact]
    public async Task GetVulnerabilitiesAsync_RejectsExcessFindingCountInsteadOfTruncating()
    {
        var client = Create("[{},{}]", new DependencyTrackOptions { MaxFindings = 1 });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetVulnerabilitiesAsync("project", TestContext.Current.CancellationToken));

        Assert.Contains("more than", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetVulnerabilitiesAsync_RejectsDeclaredOversizedResponseBeforeReading()
    {
        var content = new StringContent("[]", Encoding.UTF8, "application/json");
        content.Headers.ContentLength = 2_000_000;
        var client = Create(content, new DependencyTrackOptions { MaxResponseBytes = 1_048_576 });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetVulnerabilitiesAsync("project", TestContext.Current.CancellationToken));

        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAndReadAsync_UploadsWaitsForProcessingAndReadsTheProjectFindings()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["PUT api/v1/bom"] = """{"token":"t-1"}""",
            ["GET api/v1/bom/token/t-1"] = """{"processing":false}""",
            ["GET api/v1/project/lookup"] = """{"uuid":"p-9"}""",
            ["GET api/v1/finding/project/p-9"] = """[{"vulnerability":{"vulnId":"CVE-2026-2","severity":"LOW"},"component":{"name":"lib"}}]""",
        });

        var submission = await Routed(handler).SubmitAndReadAsync("shop", "1.2", "{}", TestContext.Current.CancellationToken);

        Assert.Equal("p-9", submission.ProjectId);
        Assert.Equal("CVE-2026-2", Assert.Single(submission.Vulnerabilities).VulnerabilityId);
        Assert.Contains(handler.Requests, request => request.Contains("name=shop&version=1.2", StringComparison.Ordinal));
        Assert.Contains("\"autoCreate\":true", handler.PutBody, StringComparison.Ordinal);
        Assert.Contains($"\"bom\":\"{Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))}\"", handler.PutBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitAndReadAsync_WithoutAProcessingToken_Fails()
    {
        var handler = new RoutingHandler(new Dictionary<string, string> { ["PUT api/v1/bom"] = """{"token":" "}""" });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Routed(handler).SubmitAndReadAsync("shop", "1.2", "{}", TestContext.Current.CancellationToken));

        Assert.Equal("Dependency-Track returned no BOM processing token.", error.Message);
    }

    [Fact]
    public async Task SubmitAndReadAsync_WithoutAProjectUuid_Fails()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["PUT api/v1/bom"] = """{"token":"t-1"}""",
            ["GET api/v1/bom/token/t-1"] = """{"processing":false}""",
            ["GET api/v1/project/lookup"] = """{"name":"shop"}""",
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Routed(handler).SubmitAndReadAsync("shop", "1.2", "{}", TestContext.Current.CancellationToken));

        Assert.Equal("Dependency-Track project lookup returned no UUID.", error.Message);
    }

    private static DependencyTrackClient Routed(RoutingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dependency-track.test/") },
            Options.Create(new DependencyTrackOptions()), TimeProvider.System);

    /// <summary>Answers each "METHOD path" with its JSON; anything else is a 404.</summary>
    private sealed class RoutingHandler(IReadOnlyDictionary<string, string> routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string PutBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (request.Method == HttpMethod.Put) PutBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var key = $"{request.Method.Method} {request.RequestUri.AbsolutePath.TrimStart('/')}";
            return routes.TryGetValue(key, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static DependencyTrackClient Create(string content, DependencyTrackOptions? options = null) =>
        Create(new StringContent(content, Encoding.UTF8, "application/json"), options);

    private static DependencyTrackClient Create(HttpContent content, DependencyTrackOptions? options = null)
    {
        var handler = new StubHandler(content);
        return new DependencyTrackClient(new HttpClient(handler) { BaseAddress = new Uri("https://dependency-track.test/") },
            Options.Create(options ?? new DependencyTrackOptions()), TimeProvider.System);
    }

    private sealed class StubHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
