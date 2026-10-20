// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// The static server serves the analytics script under the WebAnalytics package version
/// (<c>StaticAssetVersion</c> defaults to <c>$(Version)</c>, PLAN-002 C8), so the bootstrap must import
/// that same path: a package version bump that leaves the bootstrap behind turns the import into a 404
/// and silently stops the measurement of Aetheus itself.
/// </summary>
public sealed class AnalyticsBootstrapAssetVersionTests
{
    [Fact]
    public void TheBootstrap_ImportsTheScriptUnderTheWebAnalyticsPackageVersion()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.WebAnalytics", "Aetheus.WebAnalytics.csproj"));
        var version = project.Descendants("Version").Single().Value.Trim();
        var staticAssetVersion = project.Descendants("StaticAssetVersion").Single().Value.Trim();
        var bootstrap = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "js", "aetheus-analytics-bootstrap.js"));
        var imported = Regex.Match(
            bootstrap, @"""/_content/Aetheus\.WebAnalytics/(?<version>[^/""]+)/aetheus-web-analytics\.js""");

        Assert.Equal("$(Version)", staticAssetVersion);
        Assert.True(imported.Success, "The bootstrap no longer imports the packaged analytics script.");
        Assert.Equal(version, imported.Groups["version"].Value);
    }
}
