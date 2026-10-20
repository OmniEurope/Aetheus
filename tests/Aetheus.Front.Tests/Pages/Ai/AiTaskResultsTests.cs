// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.AiTasks;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>
/// Recette R3-17: an AI task's results page. Readable verdicts (never the raw "None"), the run named by
/// ?result= selected, and the Diff tab only for a run that produced a patch.
/// </summary>
public sealed class AiTaskResultsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AiTaskResultsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/ai/tasks/7", new AiTaskDefinitionDto { Id = 7, Name = "nightly review", ServerId = 20, ProfileName = "claude" });
        _handler.SetJsonResponse("api/ai/results", new PaginatedResult<AiRunResultDto>
        {
            Items =
            [
                new AiRunResultDto { Id = 101, ServerTaskId = 2, Verdict = AiVerdict.None, Succeeded = true, DurationMs = 23_000, ReportMarkdown = "second report", ProfileName = "claude", CreatedAt = DateTime.UtcNow },
                new AiRunResultDto { Id = 100, ServerTaskId = 1, Verdict = AiVerdict.Pass, Succeeded = true, DurationMs = 61_000, ReportMarkdown = "first report", ProfileName = "claude", DiffPatch = "diff --git a b", CreatedAt = DateTime.UtcNow.AddHours(-1) }
            ],
            TotalCount = 2
        });
    }

    [Fact]
    public void ShowsReadableVerdicts_AndTheNewestRunWithoutADiffTab()
    {
        var cut = Render<AiTaskResults>(parameters => parameters.Add(page => page.TaskId, 7));

        cut.WaitForAssertion(() => Assert.Contains("second report", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("AiVerdictNone", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("AiVerdictPass", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">None<", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll(".ai-results-run").Count);
        Assert.Empty(cut.FindAll("[role=tab]"));
        Assert.Equal(4, cut.FindAll(".ai-results-tiles > *").Count);
    }

    [Fact]
    public void SelectsTheRunNamedInTheAddress_AndOffersItsDiff()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/ai-tasks/7?result=100");

        var cut = Render<AiTaskResults>(parameters => parameters.Add(page => page.TaskId, 7));

        cut.WaitForAssertion(() => Assert.Contains("first report", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("true", cut.Find(".ai-results-run--selected").GetAttribute("aria-pressed"));
        Assert.Contains(cut.FindAll("[role=tab]"), tab => tab.TextContent.Contains("Diff", StringComparison.Ordinal));
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private int DefinitionReads => _handler.Requests.Count(request => request.Url.EndsWith("api/ai/tasks/7", StringComparison.Ordinal));

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(null, 1)]
    public void Edit_OpensTheTaskDialogForThisTask_AndReloadsOnlyWhenSaved(bool? saved, int expectedReads)
    {
        Dialog.OpenResult = saved;
        var cut = Render<AiTaskResults>(parameters => parameters.Add(page => page.TaskId, 7));
        cut.WaitForAssertion(() => Assert.Contains("second report", cut.Markup, StringComparison.Ordinal));

        cut.FindMenuItem("Edit")!.Click();

        Assert.Equal(typeof(AiTaskDialog), Dialog.LastComponent);
        Assert.Equal(7, Dialog.LastParameters!["DefinitionId"]);
        Assert.Null(Dialog.LastParameters!["FixedProjectId"]);
        cut.WaitForAssertion(() => Assert.Equal(expectedReads, DefinitionReads));
    }

    [Fact]
    public void DownloadPatch_SavesTheSelectedRunsDiffAsAPatchFile()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/ai-tasks/7?result=100&tab=diff");
        var cut = Render<AiTaskResults>(parameters => parameters.Add(page => page.TaskId, 7));
        cut.WaitForAssertion(() => Assert.Contains("DownloadPatch", cut.Markup, StringComparison.Ordinal));

        cut.FindAll("button").Single(button => button.TextContent.Contains("DownloadPatch", StringComparison.Ordinal)).Click();

        var invocation = JSInterop.VerifyInvoke("downloadFile");
        Assert.Equal(["ai-proposed-100.patch", "diff --git a b", "text/x-diff"], invocation.Arguments);
    }
}
