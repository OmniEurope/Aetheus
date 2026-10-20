// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public sealed class SelfMonitoringDeploymentAuditTests
{
    [Fact]
    public void BrowserBootstrap_ReplacesUnknownRouteValuesBeforeExport()
    {
        var bootstrap = Read(
            RepoRoot(), "src", "Aetheus.Front", "wwwroot", "js", "aetheus-analytics-bootstrap.js");

        Assert.Contains("return \"{value}\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("routeResolver: resolveAetheusAnalyticsRoute", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", bootstrap, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine([root, .. segments]));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
