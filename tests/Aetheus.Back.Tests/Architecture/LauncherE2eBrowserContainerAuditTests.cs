// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Candidate 2956 failed its QA on a layout the developer's <c>ylaunch -te</c> had passed: the local suite drove a
/// Chromium of the Windows host, whose system-ui font is Segoe UI, while QA drives the Chromium of the browser image
/// built from <c>deploy/docker/Dockerfile.e2e</c>, with its own Linux fonts. The launcher now runs the suite in that
/// same image (owner decision of 2026-10-27), with no silent fallback to a host browser.
///
/// Like the other launcher guards, this reads the script rather than running it.
/// </summary>
public sealed class LauncherE2eBrowserContainerAuditTests
{
    private static string LauncherSource =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "scripts", "launch-core.ps1"));

    [Fact]
    public void E2eSuite_RunsInTheQaBrowserImage()
    {
        var source = LauncherSource;

        Assert.Contains("\"deploy\\docker\\Dockerfile.e2e\"", source, StringComparison.Ordinal);
        Assert.Contains("Start-E2eContainerRun -Stack $shard.Stack", source, StringComparison.Ordinal);
        Assert.Contains("if (-not (Build-E2eRunnerImage))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void E2eSuite_NeverFallsBackToAHostBrowser()
    {
        var source = LauncherSource;

        Assert.DoesNotContain("playwright.ps1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-TestRun -Target (Join-Path $root \"tests\\Aetheus.E2E\")", source, StringComparison.Ordinal);
    }
}
