// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-5, the branch the planner reads a pipeline's schedule on, through the real page, card, calendar and
/// block info with the real English resources. It proves what the page says (which branch a definition was read on), that the
/// selector offers main and develop, and that a choice asks for a confirmation naming the old and the new branch, then stores the
/// pipeline's real source branch and reads the plan again (confirmed), or writes nothing (declined, failed save, with an error
/// toast) and puts the selector back. It does not prove the backend's storage or read of the repository (the writer tests and the
/// HTTP integration tests prove those).
/// </summary>
public sealed class PipelineScheduleSourceBranchTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Night = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly BunitTestHelper.TestHandler _handler;
    private Func<string?, ScheduledTaskDto> _task = _ => Own();

    public PipelineScheduleSourceBranchTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(Now)));
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(4, "Aetheus", "aetheus", OrganizationRole.Owner) });
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/schedule-plan?organizationId=4", _ =>
        {
            var url = _handler.RequestsSnapshot().Last(request => request.Method == "GET" && request.Url.Contains("schedule-plan", StringComparison.Ordinal)).Url;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            static DateTime Read(string? value) =>
                DateTime.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            return Task.FromResult(new SchedulePlanDto
            {
                OrganizationId = 4,
                FromUtc = Read(query["from"]),
                ToUtc = Read(query["to"]),
                Tasks = [_task(query["sourceBranch"])]
            });
        });
    }

    /// <summary>A pipeline whose definition is read on main, as its own source branch says.</summary>
    private static ScheduledTaskDto Own() => new()
    {
        PipelineId = 11,
        PipelineName = "aetheus-demo",
        Enabled = true,
        CronExpression = "0 0 0 * * *",
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = 0,
        Minute = 0,
        DurationMinutes = 30,
        NextRunUtc = Night,
        Occurrences = [new() { StartUtc = Night, EndUtc = Night.AddMinutes(30) }],
        SourceBranch = "main",
        OwnSourceBranch = "main",
        CanChooseSourceBranch = true
    };

    private async Task<IRenderedComponent<PipelineSchedule>> RenderPageAsync(string query = "")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/schedule" + query);
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync();
        var cut = Render<PipelineSchedule>();
        cut.WaitForState(() => cut.FindAll("[data-testid^='schedule-card-']").Count > 0, BunitTestHelper.AsyncRenderWait);
        return cut;
    }

    private static IReadOnlyList<string> Choices(IRenderedComponent<PipelineSchedule> cut) =>
        [.. cut.FindAll("#schedule-source-11 [role='radio']").Select(radio => radio.TextContent.Trim())];

    private IEnumerable<string> PlanUrls() =>
        _handler.RequestsSnapshot().Where(request => request.Method == "GET" && request.Url.Contains("schedule-plan", StringComparison.Ordinal)).Select(request => request.Url);

    [Fact]
    public async Task TheCard_SaysWhichBranchTheDefinitionWasReadOn_AndOffersMainAndDevelop()
    {
        var cut = await RenderPageAsync();

        Assert.Equal("Definition read on branch main", cut.Find("[data-testid='schedule-card-11'] [data-testid='schedule-source-label']").TextContent.Trim());
        Assert.Equal(new[] { "main", "develop" }, Choices(cut));
        Assert.Empty(cut.FindAll("[data-testid='schedule-source-missing']"));
        var selected = cut.FindAll("#schedule-source-11 [role='radio']").Single(radio => radio.GetAttribute("aria-checked") == "true");
        Assert.Equal("main", selected.TextContent.Trim());
    }

    [Fact]
    public async Task APipelineWithNoRepositoryToRead_HasNoSelector_AndNoBranchLine()
    {
        _task = _ => Own() with { CanChooseSourceBranch = false, SourceBranch = null, OwnSourceBranch = null };

        var cut = await RenderPageAsync();

        Assert.Empty(cut.FindAll("[data-testid='schedule-source']"));
        Assert.Empty(cut.FindAll("#schedule-source-11"));
    }

    [Fact]
    public async Task APipelineOnItsRepositorysDefaultBranch_SaysSo()
    {
        _task = _ => Own() with { SourceBranch = null, OwnSourceBranch = null };

        var cut = await RenderPageAsync();

        Assert.Equal("Definition read on the default branch of the repository", cut.Find("[data-testid='schedule-source-label']").TextContent.Trim());
    }

    [Fact]
    public async Task ABranchOutsideMainAndDevelop_IsOfferedToo_WhenItIsThePipelinesOwn()
    {
        _task = _ => Own() with { SourceBranch = "release", OwnSourceBranch = "release" };

        var cut = await RenderPageAsync();

        Assert.Equal(new[] { "main", "develop", "release" }, Choices(cut));
    }

    private ImmediateDialogService Dialogs => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private IEnumerable<(string Method, string Url, string? Body)> BranchWrites() =>
        _handler.RequestDetails.Where(request => request.Method == "PUT" && request.Url.Contains("api/pipelines/11/source-branch", StringComparison.Ordinal));

    private static string Selected(IRenderedComponent<PipelineSchedule> cut) =>
        cut.FindAll("#schedule-source-11 [role='radio']").Single(radio => radio.GetAttribute("aria-checked") == "true").TextContent.Trim();

    private static void Choose(IRenderedComponent<PipelineSchedule> cut, string branch) =>
        cut.FindAll("#schedule-source-11 [role='radio']").Single(radio => radio.TextContent.Trim() == branch).Click();

    [Fact]
    public async Task ChoosingDevelop_AsksToConfirmNamingTheOldAndTheNewBranch_StoresIt_AndReadsThePlanAgain()
    {
        var stored = "main";
        _task = _ => Own() with { SourceBranch = stored, OwnSourceBranch = stored };
        _handler.SetAsyncJsonResponse(HttpMethod.Put, "api/pipelines/11/source-branch", _ =>
        {
            stored = "develop";
            return Task.FromResult(new PipelineDto { Id = 11, Name = "aetheus-demo", SourceBranch = "develop" });
        });
        Dialogs.ConfirmResult = true;
        var cut = await RenderPageAsync();
        var plansBefore = PlanUrls().Count();

        Choose(cut, "develop");

        cut.WaitForAssertion(() => Assert.Equal(
            "Definition read on branch develop", cut.Find("[data-testid='schedule-source-label']").TextContent.Trim()), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("aetheus-demo", Dialogs.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Contains("develop instead of main", Dialogs.LastConfirmMessage, StringComparison.Ordinal);
        var write = Assert.Single(BranchWrites());
        Assert.Contains("\"branch\":\"develop\"", write.Body, StringComparison.Ordinal);
        Assert.Contains("\"expectedBranch\":\"main\"", write.Body, StringComparison.Ordinal);
        Assert.True(PlanUrls().Count() > plansBefore, "The plan is read again after the change.");
        Assert.DoesNotContain(PlanUrls(), url => url.Contains("sourceBranch", StringComparison.Ordinal));
        Assert.Equal("develop", Selected(cut));
    }

    [Fact]
    public async Task DecliningTheConfirmation_WritesNothing_AndTheSelectorGoesBackToTheCurrentBranch()
    {
        Dialogs.ConfirmResult = false;
        var cut = await RenderPageAsync();

        Choose(cut, "develop");

        cut.WaitForAssertion(() => Assert.Equal("main", Selected(cut)), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("develop instead of main", Dialogs.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Empty(BranchWrites());
        Assert.Equal("Definition read on branch main", cut.Find("[data-testid='schedule-source-label']").TextContent.Trim());
    }

    [Fact]
    public async Task AFailedSave_SaysSoInAnErrorToast_AndTheSelectorGoesBackToTheCurrentBranch()
    {
        _handler.SetResponse(HttpMethod.Put, "api/pipelines/11/source-branch", HttpStatusCode.InternalServerError);
        Dialogs.ConfirmResult = true;
        var cut = await RenderPageAsync();

        Choose(cut, "develop");

        var overlay = Services.GetRequiredService<OmniOverlayService>();
        cut.WaitForAssertion(() => Assert.Contains(overlay.Notifications, toast => toast.Severity == OmniSeverity.Danger), BunitTestHelper.AsyncRenderWait);
        var toast = overlay.Notifications.Single(notification => notification.Severity == OmniSeverity.Danger);
        Assert.Contains("aetheus-demo", toast.Message, StringComparison.Ordinal);
        Assert.Single(BranchWrites());
        cut.WaitForAssertion(() => Assert.Equal("main", Selected(cut)), BunitTestHelper.AsyncRenderWait);
        Assert.Equal("Definition read on branch main", cut.Find("[data-testid='schedule-source-label']").TextContent.Trim());
    }

    [Fact]
    public async Task AChosenBranchWithNoReadableDefinition_StillSaysSoAndWhichScheduleIsShown_WhenThePlanReadsOneThatWay()
    {
        _task = _ => Own() with { SourceBranch = "develop", ChosenBranchMissing = true };

        var cut = await RenderPageAsync();

        Assert.Equal(
            "No readable definition on develop: the schedule of main is shown.",
            cut.Find("[data-testid='schedule-source-missing']").TextContent.Trim());
    }

    [Fact]
    public async Task TheBlockOfTheCalendar_NamesTheBranch_AndItsInfoSaysWhereTheDefinitionWasRead()
    {
        var cut = await RenderPageAsync();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-omni-scheduler-appointment]")), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("branch main", cut.Find("[data-omni-scheduler-appointment^='11|']").OuterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBlockInfo_SaysTheBranchTheDefinitionWasReadOn()
    {
        var info = Render<PipelineScheduleBlockInfo>(parameters => parameters.Add(component => component.Task, Own() with { SourceBranch = "develop" }));

        Assert.Equal(
            "Viewing the definition on develop (display only): the pipeline itself reads main",
            info.Find("[data-testid='schedule-info-source']").TextContent.Trim());
    }

    [Fact]
    public void TheBlockInfo_HasNoBranchLine_ForAPipelineWithNoRepository()
    {
        var info = Render<PipelineScheduleBlockInfo>(parameters => parameters.Add(
            component => component.Task, Own() with { CanChooseSourceBranch = false, SourceBranch = null, OwnSourceBranch = null }));

        Assert.Empty(info.FindAll("[data-testid='schedule-info-source']"));
    }
}
