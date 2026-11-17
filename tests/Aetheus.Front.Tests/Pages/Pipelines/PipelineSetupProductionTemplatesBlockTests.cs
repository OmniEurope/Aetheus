// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Shared.Components.Git;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-7 (W8): when the environment sample or the Apache templates of the repository could not be read, the wizard says
/// so and does not create the pipelines (nor prepare production), instead of creating them without <c>PORT_FRONT</c> and
/// <c>PORT_BACK</c> and leaving the deployment to refuse them. Reading again, once it works, releases the creation.</summary>
public sealed class PipelineSetupProductionTemplatesBlockTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string ComposePath = "deploy/compose/remote.compose.yml";

    private readonly BunitTestHelper.TestHandler _handler;
    private bool _templatesUnreadable = true;

    public PipelineSetupProductionTemplatesBlockTests() => _handler = BunitTestHelper.RegisterServices(this);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "main", InternalRepositoryId = 5 }]);
        _handler.SetPaginatedJsonResponse("api/git/repos/5/branches",
            new[] { "main", "develop" }.Select(name => new GitLightBranchDto { Name = name, IsDefault = name == "main" }));
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = "main" });
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        _handler.SetAsyncJsonResponse<PipelineProductionAnalysisDto>(HttpMethod.Post, Analysis, _ => Task.FromResult(
            new PipelineProductionAnalysisDto
            {
                Readable = true,
                Branch = "develop",
                ComposeFiles = [ComposePath],
                ComposeFile = ComposePath,
                Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
                DefaultServer = "vps",
                DeclaresBackPort = true,
                TemplatesUnreadable = _templatesUnreadable
            }));
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = ComposePath,
            Server = "vps",
            Strategy = "replace",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config", Created = true }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static bool FinishDisabled(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled");

    private int Posts(string urlEnd) => _handler.RequestDetails.Count(request =>
        request.Method == HttpMethod.Post.Method && request.Url.EndsWith(urlEnd, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task UnreadableTemplates_BlockTheCreationWithAClearMessage_AndNothingIsPrepared()
    {
        var cut = await OpenSummaryAsync();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-production-templates-unreadable']")));
        var alert = cut.Find("[data-testid='setup-production-templates-unreadable']").TextContent;
        Assert.Contains("PipelineSetupProdTemplatesUnreadableTitle", alert, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupProdTemplatesUnreadable", alert, StringComparison.Ordinal);
        Assert.True(FinishDisabled(cut));
        Assert.Equal(0, Posts(Apply));
        Assert.Equal(0, Posts("api/pipelines"));
    }

    [Fact]
    public async Task ReadingAgain_ReleasesTheCreation_OnceTheTemplatesAreRead()
    {
        var cut = await OpenSummaryAsync();
        cut.WaitForAssertion(() => Assert.True(FinishDisabled(cut)));
        var analysesBefore = Posts(Analysis);

        _templatesUnreadable = false;
        await cut.Find("[data-testid='setup-production-templates-retry']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid='setup-production-templates-unreadable']"));
            Assert.False(FinishDisabled(cut));
        });
        Assert.True(Posts(Analysis) > analysesBefore);
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, Posts("api/pipelines")));
        Assert.Equal(1, Posts(Apply));
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];
}
