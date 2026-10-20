// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Aetheus.Back.Tests.Security;

/// <summary>Exercises the HTTP pipeline with browser CSP report content types and Reporting API
/// arrays. The controller parses these bodies directly because the default JSON formatter does not
/// accept their formats.</summary>
public class CspReportEndpointIntegrationTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private const string LegacyBody = """
        {"csp-report":{"document-uri":"https://app.example/pipelines/runs/2281","blocked-uri":"inline","violated-directive":"script-src","source-file":"https://app.example/x.js","line-number":12,"script-sample":"alert(1)"}}
        """;

    private const string ReportingApiBody = """
        [{"type":"csp-violation","age":0,"url":"https://app.example/pipelines/runs/2281","body":{"documentURL":"https://app.example/pipelines/runs/2281","blockedURL":"inline","effectiveDirective":"script-src-elem","sourceFile":"https://app.example/x.js","lineNumber":12,"sample":"alert(1)"}}]
        """;

    [Theory]
    [InlineData("application/csp-report", LegacyBody)]      // report-uri, every browser
    [InlineData("application/reports+json", ReportingApiBody)] // report-to, Reporting API
    [InlineData("application/json", LegacyBody)]
    public async Task A_Report_Posted_The_Way_Browsers_Post_It_Is_Accepted(string contentType, string body)
    {
        using var response = await Post(contentType, body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_Malformed_Report_Is_Still_Accepted_Silently()
    {
        using var response = await Post("application/csp-report", "{ not json");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_Oversized_Report_Is_Dropped_Unparsed()
    {
        // Only the first 8 KiB is ever read, and a read that fills the cap is dropped without being
        // parsed: an oversized body can neither be interpreted nor crash the endpoint.
        using var response = await Post("application/csp-report", new string('x', 64 * 1024));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task The_Response_Carries_The_Csp_Reporting_Directives()
    {
        using var response = await factory.CreateClient()
            .GetAsync("/health/live", TestContext.Current.CancellationToken);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("report-uri /api/security/csp-report", csp, StringComparison.Ordinal);
        Assert.Contains("report-to csp-endpoint", csp, StringComparison.Ordinal);

        var endpoints = Assert.Single(response.Headers.GetValues("Reporting-Endpoints"));
        Assert.Equal("csp-endpoint=\"/api/security/csp-report\"", endpoints);
    }

    [Fact]
    public async Task The_Script_Policy_Asks_For_A_Sample_Without_Allowing_Inline_Or_Eval()
    {
        // Recette R1-83: four blocked inline scripts arrived with no sample, so their origin could not be
        // read. 'report-sample' only fills the report; the policy itself must stay as strict as before.
        using var response = await factory.CreateClient()
            .GetAsync("/health/live", TestContext.Current.CancellationToken);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        var scriptSrc = csp.Split(';', StringSplitOptions.TrimEntries)
            .Single(directive => directive.StartsWith("script-src ", StringComparison.Ordinal));
        Assert.Equal("script-src 'self' 'wasm-unsafe-eval' blob: 'report-sample'", scriptSrc);
        Assert.DoesNotContain("'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", csp, StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> Post(string contentType, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await factory.CreateClient().PostAsync(
            "/api/security/csp-report", content, TestContext.Current.CancellationToken);
    }
}
