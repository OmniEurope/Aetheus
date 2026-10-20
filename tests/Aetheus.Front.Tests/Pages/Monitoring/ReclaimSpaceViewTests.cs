// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Monitoring;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Monitoring;

/// <summary> « Récupérer la place » lists the purged tables, asks before rewriting one, refuses to
/// start while a pipeline runs, shows the sizes before and after, and only administrators see it.
/// </summary>
public sealed class ReclaimSpaceViewTests : BunitContext
{
    private const long Megabyte = 1024 * 1024;
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly AnsweringDialogService _dialog = new();

    public ReclaimSpaceViewTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        Services.AddScoped<OmniDialogService>(_ => _dialog);
    }

    private static ReclaimSpaceStatusDto Status(int activeRuns = 0, params ReclaimSpaceResultDto[] history) => new()
    {
        ActivePipelineRuns = activeRuns,
        Tables = [new ReclaimSpaceTableDto("AnalysisMetrics", 2_425 * Megabyte), new ReclaimSpaceTableDto("AppLogEntries", 219 * Megabyte)],
        History = [.. history]
    };

    [Fact]
    public void AConfirmedReclaim_StartsTheTablesRewrite()
    {
        _handler.SetJsonResponse("api/data-storage/reclaim", Status());
        _handler.SetResponse(HttpMethod.Post, "api/data-storage/reclaim", System.Net.HttpStatusCode.Accepted);
        _dialog.Answer = true;
        var cut = Render<ReclaimSpaceView>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=reclaim-space-start]").Count));

        cut.FindAll("[data-testid=reclaim-space-start]")[0].Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == "POST"));
        Assert.Contains("AnalysisMetrics", _handler.RequestDetails.Single(request => request.Method == "POST").Body);
        // The confirmation was asked first (the test localizer returns the key).
        Assert.Equal("ReclaimSpaceConfirm", _dialog.LastMessage);
    }

    [Fact]
    public void ADeclinedConfirmation_SendsNothing()
    {
        _handler.SetJsonResponse("api/data-storage/reclaim", Status());
        _dialog.Answer = false;
        var cut = Render<ReclaimSpaceView>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=reclaim-space-start]")));

        cut.FindAll("[data-testid=reclaim-space-start]")[0].Click();

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "POST");
    }

    [Fact]
    public void WhileAPipelineRuns_TheButtonsWaitAndThePageSaysWhy()
    {
        _handler.SetJsonResponse("api/data-storage/reclaim", Status(activeRuns: 3));

        var cut = Render<ReclaimSpaceView>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=reclaim-space-busy]")));
        Assert.All(cut.FindAll("[data-testid=reclaim-space-start]"), button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public void AFinishedReclaim_ShowsTheSizeAfter()
    {
        _handler.SetJsonResponse("api/data-storage/reclaim", Status(0, new ReclaimSpaceResultDto
        {
            Table = "AnalysisMetrics",
            RequestedBy = "admin",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow,
            BytesBefore = 2_425 * Megabyte,
            BytesAfter = 300 * Megabyte,
            Succeeded = true
        }));

        var cut = Render<ReclaimSpaceView>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=reclaim-space-after]")));
        Assert.Contains("ReclaimSpaceDone", cut.Markup);
    }

    [Fact]
    public void TheSupervision_ShowsTheActionToAdministrators()
    {
        _handler.SetJsonResponse("api/appmonitoring/summary", new AppMonitoringSummaryDto());
        _handler.SetJsonResponse("api/data-storage/projects", new ProjectStorageReportDto());
        _handler.SetJsonResponse("api/data-storage/reclaim", Status());

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=reclaim-space-start]")));
    }

    private sealed class AnsweringDialogService()
        : OmniDialogService(new AppDialogs(new OmniOverlayService(), new BunitTestHelper.StubLocalizer()), new OmniOverlayService())
    {
        public bool Answer { get; set; }
        public string LastMessage { get; private set; } = string.Empty;

        public override Task<bool?> Confirm(string? message, string? title, OmniConfirmOptions? options = null)
        {
            LastMessage = message ?? string.Empty;
            return Task.FromResult<bool?>(Answer);
        }
    }

    [Fact]
    public void R171_TheViewHasNoExplanatoryParagraph_ButKeepsItsConfirmation()
    {
        _handler.SetJsonResponse("api/data-storage/reclaim", Status());
        var cut = Render<ReclaimSpaceView>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=reclaim-space-start]").Count));

        // D1 (PLAN-002 lot 4) adds a "?" help mark in the heading; still no paragraph under the grid.
        Assert.Empty(cut.FindAll("[data-testid=reclaim-space-note]"));
    }

    [Fact]
    public void TheSection_HasTheAnchorTheLinkTargetsAndAHelpMarkSayingWhenToRunIt()
    {
        _handler.SetJsonResponse("api/data-storage/reclaim", Status());

        var cut = Render<ReclaimSpaceView>();

        Assert.NotNull(cut.Find("#reclaim-space"));
        Assert.Equal("ReclaimSpaceHelp", cut.Find("[data-testid=reclaim-space-help]").GetAttribute("aria-label"));
    }
}
