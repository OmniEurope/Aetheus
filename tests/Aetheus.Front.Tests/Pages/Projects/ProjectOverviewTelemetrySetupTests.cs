// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Recette R-47 (user decision 2026-11-03): while the telemetry the wizard configured waits for the application's
/// repository to hold the telemetry NuGet packages, the project's overview says to finish its configuration. The backend
/// decides (it reads the stored inventory); the overview shows what it answers and invents nothing when it cannot answer.</summary>
public sealed class ProjectOverviewTelemetrySetupTests : BunitContext
{
    private const string Warning = "[data-testid='telemetry-setup-pending']";
    private const string SetupUrl = "api/appmonitoring/projects/3/telemetry-setup";

    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectOverviewTelemetrySetupTests() => _handler = BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<ProjectOverviewSection> Open()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=3", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=3&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto { Id = 3, Name = "Web", Pipelines = [new PipelineDto { Id = 9, Name = "web-ci" }] }));
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(),
            request => request.Method == HttpMethod.Get.Method && request.Url.EndsWith(SetupUrl, StringComparison.Ordinal)));
        return cut;
    }

    [Fact]
    public void TheWarning_IsShown_WhileTheTelemetryIsRequestedAndThePackagesAreNotDetected()
    {
        _handler.SetJsonResponse(HttpMethod.Get, SetupUrl, new AppTelemetrySetupDto { Requested = true, PackagesDetected = false });

        var cut = Open();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(Warning)));
        Assert.Contains("TelemetrySetupPending", cut.Find(Warning).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarning_IsGone_OnceThePackagesAreDetected()
    {
        _handler.SetJsonResponse(HttpMethod.Get, SetupUrl, new AppTelemetrySetupDto { Requested = true, PackagesDetected = true });

        var cut = Open();

        Assert.Empty(cut.FindAll(Warning));
    }

    [Fact]
    public void TheWarning_IsNeverShown_ForAProjectThatDidNotChooseTheTelemetry()
    {
        _handler.SetJsonResponse(HttpMethod.Get, SetupUrl, new AppTelemetrySetupDto { Requested = false, PackagesDetected = false });

        var cut = Open();

        Assert.Empty(cut.FindAll(Warning));
    }

    [Fact]
    public void WhenTheServerCannotAnswer_NoWarningIsInvented()
    {
        _handler.SetResponse(HttpMethod.Get, SetupUrl, System.Net.HttpStatusCode.InternalServerError);

        var cut = Open();

        Assert.Empty(cut.FindAll(Warning));
    }
}
