// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary>
/// Recette R2-108 (ZAP 10055-3, "CSP: Notices"): the policies the front's static server sends name the
/// API's violation collector through report-to and the Reporting-Endpoints header, never through the
/// deprecated report-uri that the scanner reports on its own. Read on the live responses of the shell
/// and of the Monaco frame.
/// </summary>
[Category("E2E")]
[Category("Security")]
public sealed class FrontCspReportingTests : E2ETestBase
{
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
