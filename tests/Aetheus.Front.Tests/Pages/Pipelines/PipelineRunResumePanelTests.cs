// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-39: a run that rolled back after a missed confirmation shows why it waits, until
/// when, and offers Resume as its main action with Cancel in its menu.</summary>
public sealed class PipelineRunResumePanelTests : BunitContext
{
    private const string ResumeUrl = "api/pipelines/runs/42/resume";
    private const string CancelUrl = "api/pipelines/runs/42/cancel";
    private static readonly DateTime Deadline = new(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunResumePanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        // The detail names the deadline: a template that shows where the formatted date lands.
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer[Arg.Any<string>()].Returns(call => (string)call[0] == "RunAwaitingResumeDetail"
            ? new LocalizedString("RunAwaitingResumeDetail", "until {0}")
            : new LocalizedString((string)call[0], (string)call[0]));
        Services.AddSingleton(localizer);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", HttpStatusCode.NoContent);
        _handler.SetResponse(HttpMethod.Post, ResumeUrl, HttpStatusCode.NoContent);
        _handler.SetResponse(HttpMethod.Post, CancelUrl, HttpStatusCode.NoContent);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private bool Posted(string url) =>
        _handler.Requests.Any(request => request.Method == "POST" && request.Url.Contains(url, StringComparison.Ordinal));

    private int RunReads() =>
        _handler.Requests.Count(request => request.Method == "GET" && request.Url.EndsWith("api/pipelines/runs/42", StringComparison.Ordinal));

    private IRenderedComponent<PipelineRun> RenderRun(PipelineStatus status, DateTime? deadline)
    {
        _handler.SetJsonResponse("api/pipelines/runs/42", new PipelineRunDto
        {
            Id = 42,
            PipelineId = 5,
            PipelineName = "aetheus-release-fast",
            Status = status,
            StartedAt = new DateTime(2026, 10, 24, 8, 30, 0, DateTimeKind.Utc),
            ResumeDeadline = deadline
        });
        var cut = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 42));
        cut.WaitForState(() => cut.Markup.Contains("aetheus-release-fast", StringComparison.Ordinal));
        return cut;
    }

    /// <summary>The Cancel entry of the resume button's menu, the menu opened as the chevron does.</summary>
    private static IRenderedComponent<OmniMenuItem> ResumeMenuCancel(IRenderedComponent<PipelineRun> cut)
    {
        var panel = cut.FindComponent<PipelineRunResumePanel>();
        var split = panel.FindComponent<OmniSplitButton>();
        if (split.FindComponents<OmniMenuItem>().Count == 0)
            split.Find("button[aria-haspopup]").Click();
        return Assert.Single(split.FindComponents<OmniMenuItem>(), item => item.Instance.Class == "run-resume-cancel");
    }

    [Fact]
    public void AWaitingRun_ExplainsTheWait_WithTheLocalDeadline_AndOffersResumeAsItsMainAction()
    {
        var cut = RenderRun(PipelineStatus.AwaitingResume, Deadline);

        var alert = cut.Find("[data-testid='run-awaiting-resume']");
        Assert.Contains("RunAwaitingResumeTitle", alert.TextContent, StringComparison.Ordinal);
        Assert.Contains($"until {Deadline.ToLocalTime():g}", alert.TextContent, StringComparison.Ordinal);
        var split = cut.FindComponent<OmniSplitButton>();
        Assert.Equal(OmniButtonVariant.Primary, split.Instance.Variant);
        Assert.Equal("ResumeRun", split.Instance.Text);
    }

    [Theory]
    [InlineData(PipelineStatus.Running)]
    [InlineData(PipelineStatus.RolledBack)]
    [InlineData(PipelineStatus.Failed)]
    public void ARunThatDoesNotWait_ShowsNoResume(PipelineStatus status)
    {
        var cut = RenderRun(status, deadline: null);

        Assert.Empty(cut.FindAll("[data-testid='run-awaiting-resume']"));
    }

    [Fact]
    public async Task Resume_AsksTheServer_ThenReloadsTheRun()
    {
        var cut = RenderRun(PipelineStatus.AwaitingResume, Deadline);
        var readsBefore = RunReads();

        await cut.InvokeAsync(() => cut.FindComponent<OmniSplitButton>().Instance.OnClick.InvokeAsync());

        Assert.True(Posted(ResumeUrl));
        Assert.True(RunReads() > readsBefore, "A resume reloads the run.");
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Info);
    }

    [Fact]
    public async Task AResumeTheServerRefuses_SaysSo_AndStillReloads()
    {
        _handler.SetResponse(HttpMethod.Post, ResumeUrl, HttpStatusCode.Conflict);
        var cut = RenderRun(PipelineStatus.AwaitingResume, Deadline);
        var readsBefore = RunReads();

        await cut.InvokeAsync(() => cut.FindComponent<OmniSplitButton>().Instance.OnClick.InvokeAsync());

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger);
        Assert.True(RunReads() > readsBefore, "The page catches up with the state the server kept.");
    }

    [Fact]
    public async Task Cancel_IsConfirmedInRed_WithItsVerb_AndRevenirToDismiss_ThenStopsTheRun()
    {
        Dialog.ConfirmResult = true;
        var cut = RenderRun(PipelineStatus.AwaitingResume, Deadline);

        await cut.InvokeAsync(() => ResumeMenuCancel(cut).Instance.OnClick.InvokeAsync());

        Assert.Equal("CancelAwaitingRunConfirm", Dialog.LastConfirmMessage);
        Assert.NotNull(Dialog.LastConfirmOptions);
        Assert.True(Dialog.LastConfirmOptions.Destructive);
        Assert.Equal("Stop", Dialog.LastConfirmOptions.OkButtonText);
        Assert.Equal("GoBack", Dialog.LastConfirmOptions.CancelButtonText);
        Assert.Equal(OmniTone.Danger, ResumeMenuCancel(cut).Instance.Tone);
        Assert.True(Posted(CancelUrl));
        Assert.False(Posted(ResumeUrl));
    }

    [Fact]
    public async Task ADeclinedCancel_StopsNothing()
    {
        Dialog.ConfirmResult = false;
        var cut = RenderRun(PipelineStatus.AwaitingResume, Deadline);

        await cut.InvokeAsync(() => ResumeMenuCancel(cut).Instance.OnClick.InvokeAsync());

        Assert.False(Posted(CancelUrl));
    }

    [Fact]
    public async Task ACancelTheServerRefuses_SaysSo()
    {
        _handler.SetResponse(HttpMethod.Post, CancelUrl, HttpStatusCode.Conflict);
        Dialog.ConfirmResult = true;
        var cut = RenderRun(PipelineStatus.AwaitingResume, Deadline);

        await cut.InvokeAsync(() => ResumeMenuCancel(cut).Instance.OnClick.InvokeAsync());

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger);
    }

    [Fact]
    public void TheWaitingStatus_ReadsAsAWarning()
    {
        Assert.Equal(OmniTone.Warning, PipelineHelper.GetRunBadge(PipelineStatus.AwaitingResume));
    }
}
