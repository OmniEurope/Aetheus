// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary>Verify live shell and Monaco responses advertise the API CSP collector through
/// report-to and Reporting-Endpoints rather than deprecated report-uri.</summary>
[Category("E2E")]
[Category("Security")]
public sealed class FrontCspReportingTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [TestCase("")]
    [TestCase("monaco-frame.html")]
    public async Task FrontDocument_NamesItsCollectorThroughReportTo(string path)
    {
        var response = await Page.APIRequest.GetAsync($"{FrontendUrl}/{path}");
        var policy = response.Headers["content-security-policy"];

        Assert.Multiple(() =>
        {
            Assert.That(policy, Does.Not.Contain("report-uri"));
            Assert.That(policy, Does.EndWith("; report-to csp-endpoint"));
            Assert.That(response.Headers["reporting-endpoints"],
                Is.EqualTo($"csp-endpoint=\"{BackendUrl.TrimEnd('/')}/api/security/csp-report\""));
        });
    }
}
