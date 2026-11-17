// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-41: the CVE tab of a server says "not reported" and "not covered" as such, lists the findings of its host
/// system, and offers the dry run of the system upgrade from a finding.</summary>
public class ServerHostCveSectionTests : BunitContext
{
    private static ServerHostFindingDto Finding(long id, string package, string? fix, DateTime? exploited = null) => new()
    {
        Id = id,
        ServerId = 1,
        ServerName = "srv-1",
        AdvisoryId = $"UBUNTU-CVE-2026-000{id}",
        CveId = $"CVE-2026-000{id}",
        PackageName = package,
        BinaryPackages = package,
        InstalledVersion = "1.0-1ubuntu1",
        FixedVersion = fix,
        Severity = AnalysisSeverity.Critical,
        SeverityPublished = true,
        ExploitedSince = exploited,
        Status = VulnerabilityMatchStatus.Affected
    };

    private static ServerHostVulnerabilitiesDto Watched(params ServerHostFindingDto[] findings) => new()
    {
        State = HostVulnerabilityWatchState.Watched,
        OsName = "Ubuntu 24.04.1 LTS",
        Ecosystem = "Ubuntu:24.04:LTS",
        ReportedAt = DateTime.UtcNow,
        MatchedAt = DateTime.UtcNow,
        PackageCount = 812,
        Findings = [.. findings]
    };

    private (IRenderedComponent<ServerHostCveSection> Cut, BunitTestHelper.TestHandler Handler) RenderWith(ServerHostVulnerabilitiesDto dto, bool isAdmin = false)
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        handler.SetJsonResponse(HttpMethod.Get, "host-vulnerabilities", dto);
        return (Render<ServerHostCveSection>(p => p.Add(c => c.ServerId, 1)), handler);
    }

    [Fact]
    public void AnAgentThatNeverReported_IsSaidSo_NeverAsNoCve()
    {
        var (cut, _) = RenderWith(new ServerHostVulnerabilitiesDto { State = HostVulnerabilityWatchState.NotReported });

        cut.WaitForAssertion(() => Assert.Contains("ServerHostCveNotReportedTitle", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("ServerHostCveNoneTitle", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=server-host-findings]"));
    }

    [Fact]
    public void ASystemNoFeedCovers_IsSaidUnsupported()
    {
        var (cut, _) = RenderWith(new ServerHostVulnerabilitiesDto { State = HostVulnerabilityWatchState.UnsupportedSystem, OsName = "Rocky Linux 9.4" });

        cut.WaitForAssertion(() => Assert.Contains("ServerHostCveUnsupportedTitle", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=server-host-findings]"));
    }

    [Fact]
    public void AWatchedServer_ListsItsFindings_WithTheirFixAndTheAttribution()
    {
        var (cut, _) = RenderWith(Watched(Finding(1, "openssl", "3.0.13-0ubuntu3.4", DateTime.UtcNow), Finding(2, "linux", null)));

        cut.WaitForAssertion(() => Assert.Contains("CVE-2026-0001", cut.Markup, StringComparison.Ordinal));
        var markup = cut.Markup;
        Assert.Contains("CVE-2026-0002", markup, StringComparison.Ordinal);
        Assert.Contains("3.0.13-0ubuntu3.4", markup, StringComparison.Ordinal);
        Assert.Contains("CveWatchNoFix", markup, StringComparison.Ordinal);
        Assert.Contains("CveWatchExploited", markup, StringComparison.Ordinal);
        Assert.Contains("CveWatchAdvisoryAttribution", markup, StringComparison.Ordinal);
        // No raw enum: the severity goes through its localized key.
        Assert.DoesNotContain(">Critical<", markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=server-host-cve-pending]"));
    }

    [Fact]
    public void AFinding_LinksItsAdvisory_InANewTab_AndOneWithoutAddressIsPlainText()
    {
        var linked = Finding(1, "openssl", "3.0.13-0ubuntu3.4") with { AdvisoryUrl = "https://osv.dev/vulnerability/UBUNTU-CVE-2026-0001" };
        var (cut, _) = RenderWith(Watched(linked, Finding(2, "linux", null)));

        cut.WaitForAssertion(() => Assert.Contains("CVE-2026-0002", cut.Markup, StringComparison.Ordinal));
        var link = Assert.Single(cut.FindAll("a.server-host-advisory-link"));
        Assert.Equal("https://osv.dev/vulnerability/UBUNTU-CVE-2026-0001", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"), StringComparison.Ordinal);
        Assert.StartsWith("CVE-2026-0001", link.TextContent.Trim(), StringComparison.Ordinal);
        // The finding the back gave no address stays readable, without a dead link.
        Assert.Contains("CVE-2026-0002", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportNotYetMatched_IsSaidPending_NotClean()
    {
        var (cut, _) = RenderWith(Watched() with { MatchedAt = null });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=server-host-cve-pending]")));
        Assert.DoesNotContain("ServerHostCveNoneTitle", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRead_OffersARetry_AndNoVerdict()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Get, "host-vulnerabilities", HttpStatusCode.InternalServerError);
        var cut = Render<ServerHostCveSection>(p => p.Add(c => c.ServerId, 1));

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("ServerHostCveNotReportedTitle", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDryRun_OfAFinding_QueuesASimulatedSystemUpgrade()
    {
        var (cut, handler) = RenderWith(Watched(Finding(1, "openssl", "3.0.13-0ubuntu3.4")), isAdmin: true);
        handler.SetJsonResponse(HttpMethod.Post, "system/upgrade", new ServiceTaskResponse { TaskId = 42 });
        cut.WaitForAssertion(() => Assert.Contains("CVE-2026-0001", cut.Markup, StringComparison.Ordinal));

        var item = cut.FindMenuItem("ServerHostCveDryRun");
        Assert.NotNull(item);
        item.Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(handler.RequestDetails, request => request.Url.Contains("system/upgrade", StringComparison.Ordinal));
            Assert.Equal("POST", post.Method);
            Assert.Contains("\"dryRun\":true", post.Body, StringComparison.OrdinalIgnoreCase);
        });
    }
}
