// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Dashboards;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>A page is drawn from its HTTP reads: the realtime hub is a refresh channel and never stands between a
/// loaded list and the screen. ComponentBase draws again only when OnInitializedAsync ends, so state set after the
/// reads and before a hub start that never answers stays undrawn (the loader of E2E 2026-11-02 on the pipeline page,
/// the same shape as these pages). Every read here answers after the page has drawn its first state, as in the
/// browser, and the hub never answers.</summary>
public sealed class LiveStartDoesNotHoldTheReadBackTests : BunitContext
{
    private const string ActiveLoadingBar = "thead > tr.omni-data-grid__progress .omni-loading-bar--active";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private readonly BunitTestHelper.TestHandler _handler;

    public LiveStartDoesNotHoldTheReadBackTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true);
        Services.AddSingleton<HubConnectionFactory>(sp => new BunitTestHelper.BlockingHubConnectionFactory(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetRequiredService<AuthStateProvider>()));
    }

    [Fact]
    public void MonitoredApps_AreDrawnWhileTheLiveConnectionNeverAnswers()
    {
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps",
            new List<MonitoredAppDto> { new() { Id = 1, ProjectId = 1, Name = "toto-qa", CurrentStatus = AppHealthStatus.Up } });
        var samples = new TaskCompletionSource<List<AppHealthSampleDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/appmonitoring/apps/1/samples", _ => samples.Task);

        var cut = Render<MonitoredAppsList>(parameters => parameters.Add(list => list.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("apps/1/samples", StringComparison.Ordinal)));
        samples.SetResult([]);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("toto-qa", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll(ActiveLoadingBar));
        }, Patience);
    }

    [Fact]
    public void ProjectQuality_LeavesItsLoader_WhileTheLiveConnectionNeverAnswers()
    {
        _handler.SetJsonResponse("api/pipelines/projects/1/quality-trend", new ProjectQualityTrendDto());
        _handler.SetJsonResponse("api/analysis/projects/1/findings?page=1&pageSize=25&status=Open", new PaginatedResult<AnalysisFindingDto>());
        var summary = new TaskCompletionSource<AnalysisProjectSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/analysis/projects/1/summary", _ => summary.Task);

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(section => section.ProjectId, 1));
        Assert.Contains("aetheus-loader", cut.Markup, StringComparison.Ordinal);
        summary.SetResult(new AnalysisProjectSummaryDto());

        cut.WaitForAssertion(() => Assert.DoesNotContain("aetheus-loader", cut.Markup, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void TheHomeApplications_AreDrawnWhileTheLiveConnectionNeverAnswers()
    {
        BunitTestHelper.SetOnboardingWizardResponses(_handler);
        _handler.SetJsonResponse("api/monitoring/dashboard", new DashboardOverviewDto());
        var summary = new TaskCompletionSource<AppMonitoringSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/appmonitoring/summary", _ => summary.Task);

        var cut = Render<Home>();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("appmonitoring/summary", StringComparison.Ordinal)), Patience);
        summary.SetResult(new AppMonitoringSummaryDto
        {
            TotalCount = 1,
            UpCount = 1,
            Applications = [new MonitoredAppStatusDto { Id = 1, ProjectId = 1, Name = "toto-qa", CurrentStatus = AppHealthStatus.Up }]
        });

        cut.WaitForAssertion(() => Assert.Contains("toto-qa", cut.Markup, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void TheVariableLibrariesOfAServer_AreDrawnWhenTheirUsageCannotBeReadAndTheLiveConnectionNeverAnswers()
    {
        var libraries = new TaskCompletionSource<List<VariableLibraryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/servers/1/variable-libraries", _ => libraries.Task);
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/resource-usage", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<VariableLibrariesList>(parameters => parameters.Add(list => list.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("variable-libraries", StringComparison.Ordinal)), Patience);
        libraries.SetResult([new VariableLibraryDto { Id = 1, Name = "toto-vars" }]);

        cut.WaitForAssertion(() => Assert.Contains("toto-vars", cut.Markup, StringComparison.Ordinal), Patience);
    }
}
