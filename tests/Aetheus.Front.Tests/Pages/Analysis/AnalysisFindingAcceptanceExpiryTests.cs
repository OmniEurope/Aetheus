// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>
/// Recette R-24 in the finding page: accepting the risk of a CVE-watch finding offers 30, 90 or 180 days (90
/// preselected) instead of a free date, the length is what is sent, and an accepted finding shows how long its
/// acceptance lasts. Findings of other scanners keep the free date; an expired acceptance is an open finding again.
/// </summary>
public sealed class AnalysisFindingAcceptanceExpiryTests : BunitContext
{
    private static readonly DateTime Expiry = new(2027, 2, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly BunitTestHelper.TestHandler _handler;

    public AnalysisFindingAcceptanceExpiryTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // R-21: the page reads its finding's project name for the trail.
        _handler.SetJsonResponse("api/projects/1", new ProjectDetailDto { Id = 1, Name = "Aetheus" });
    }

    private IRenderedComponent<AnalysisFindingDetail> RenderFinding(
        string scannerKey,
        AnalysisFindingStatus status = AnalysisFindingStatus.Open,
        params AnalysisFindingDecisionDto[] decisions)
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
        var occurrence = new AnalysisFindingOccurrenceDto { Id = 1, ScannerKey = scannerKey, ToolName = scannerKey };
        _handler.SetJsonResponse("api/analysis/findings/21", new AnalysisFindingDto
        {
            Id = 21,
            ProjectId = 1,
            RuleId = "CVE-2026-0001",
            Title = "Vulnerable package",
            Message = "A package is affected.",
            Category = AnalysisCategory.Dependencies,
            Severity = AnalysisSeverity.High,
            Status = status,
            LatestOccurrence = occurrence
        });
        _handler.SetJsonResponse("api/analysis/findings/21/occurrences?take=100", new List<AnalysisFindingOccurrenceDto> { occurrence });
        _handler.SetJsonResponse("api/analysis/findings/21/decisions", decisions.ToList());
        _handler.SetJsonResponse(HttpMethod.Post, "api/analysis/findings/21/decisions", new AnalysisFindingDecisionDto { Id = 5, AnalysisFindingId = 21 });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/analysis/findings/21?tab=decisions");
        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 21));
        cut.WaitForState(() => cut.Markup.Contains("analysis-decision-card", StringComparison.Ordinal));
        return cut;
    }

    private static AnalysisFindingDecisionDto Decision(DateTime? expiresAt, DateTime? revokedAt = null) => new()
    {
        Id = 3,
        AnalysisFindingId = 21,
        Status = AnalysisFindingStatus.Accepted,
        Reason = "accepted until the vendor ships a fix",
        CreatedByUsername = "admin",
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt,
        CreatedAt = Expiry.AddDays(-30)
    };

    [Fact]
    public void AcceptingACveWatchFinding_OffersThe30_90_And180DayLengths_With90Preselected_AndNoFreeDate()
    {
        var cut = RenderFinding("cve-watch");
        Assert.NotEmpty(cut.FindAll(".analysis-decision-details .omni-date-time-picker, .analysis-decision-details [type='datetime-local']"));

        cut.Find("[data-testid='analysis-decision-Accepted']").Click();

        // The server derives the date: the bar only offers the three lengths, the middle one (90 days) is selected.
        var choices = cut.FindAll(".analysis-decision-details .omni-select-bar [role='radio']");
        Assert.Equal(3, choices.Count);
        Assert.Equal(["false", "true", "false"], choices.Select(choice => choice.GetAttribute("aria-checked")));
        Assert.Empty(cut.FindAll(".analysis-decision-details .omni-date-time-picker, .analysis-decision-details [type='datetime-local']"));
        Assert.Equal(2, cut.FindAll(".analysis-decision-details .omni-form-field").Count);
    }

    [Fact]
    public void TheChosenLength_IsWhatIsSent_AndNoFreeDateWithIt()
    {
        var cut = RenderFinding("cve-watch");
        BunitTestHelper.Interactive(this);
        cut.Find("textarea").Input("Risk accepted until the vendor ships a fix.");
        cut.Find("[data-testid='analysis-decision-Accepted']").Click();

        cut.FindAll(".analysis-decision-details .omni-select-bar [role='radio']")[2].Click();
        cut.Find(".analysis-decision-form form").Submit();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST" && request.Url.EndsWith("api/analysis/findings/21/decisions", StringComparison.Ordinal));
            Assert.Contains("\"acceptanceDays\":180", post.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"expiresAt\":\"", post.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void WithoutAChange_The90DayLengthIsSent()
    {
        var cut = RenderFinding("cve-watch");
        BunitTestHelper.Interactive(this);
        cut.Find("textarea").Input("Risk accepted until the vendor ships a fix.");
        cut.Find("[data-testid='analysis-decision-Accepted']").Click();

        cut.Find(".analysis-decision-form form").Submit();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST" && request.Url.EndsWith("api/analysis/findings/21/decisions", StringComparison.Ordinal));
            Assert.Contains("\"acceptanceDays\":90", post.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void AFindingOfAnotherScanner_KeepsTheFreeDate()
    {
        var cut = RenderFinding("opengrep");

        cut.Find("[data-testid='analysis-decision-Accepted']").Click();

        Assert.Empty(cut.FindAll(".analysis-decision-details .omni-select-bar"));
        Assert.NotEmpty(cut.FindAll(".analysis-decision-details .omni-date-time-picker, .analysis-decision-details [type='datetime-local']"));
    }

    [Fact]
    public void ADecisionThatIsNotAnAcceptance_OnACveWatchFinding_KeepsTheFreeDate()
    {
        var cut = RenderFinding("cve-watch");

        cut.Find("[data-testid='analysis-decision-FalsePositive']").Click();

        Assert.Empty(cut.FindAll(".analysis-decision-details .omni-select-bar"));
        Assert.NotEmpty(cut.FindAll(".analysis-decision-details .omni-date-time-picker, .analysis-decision-details [type='datetime-local']"));
    }

    [Fact]
    public void AnAcceptedFinding_ShowsUntilWhenItsAcceptanceEnds()
    {
        var cut = RenderFinding("cve-watch", AnalysisFindingStatus.Accepted, Decision(Expiry));

        var badge = cut.Find("[data-testid='analysis-accepted-until']");

        Assert.Contains("AnalysisAcceptedUntil", badge.TextContent, StringComparison.Ordinal);
        Assert.Equal(Expiry.ToString("O"), badge.QuerySelector("time")!.GetAttribute("datetime"));
    }

    [Fact]
    public void AnAcceptanceFromBeforeTheExpiry_IsShownWithoutOne()
    {
        var cut = RenderFinding("cve-watch", AnalysisFindingStatus.Accepted, Decision(null));

        var badge = cut.Find("[data-testid='analysis-accepted-until']");

        Assert.Contains("AnalysisAcceptedNoExpiry", badge.TextContent, StringComparison.Ordinal);
        Assert.Null(badge.QuerySelector("time"));
    }

    [Fact]
    public void AnExpiredAcceptance_IsAnOpenFindingAgain_WithNoAcceptedBadge()
    {
        var cut = RenderFinding("cve-watch", AnalysisFindingStatus.Open, Decision(Expiry, revokedAt: Expiry));

        Assert.Empty(cut.FindAll("[data-testid='analysis-accepted-until']"));
        Assert.Contains("AnalysisFindingStatusOpen", cut.Find(".analysis-decision-selected").TextContent, StringComparison.Ordinal);
    }
}
