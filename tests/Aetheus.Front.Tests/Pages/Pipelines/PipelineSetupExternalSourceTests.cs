// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-69 (OE2): the project's source is an external Git repository. When no Compose file can be read from it, the
/// wizard used to create the production and the QA pipelines anyway ("production not prepared", "QA without Compose file"): the
/// first could never deploy and the second validated nothing. It now creates neither, says why, and keeps every other pipeline.
/// The API is a double of the HTTP handler (the analysis the server would return is set by the test); the server side of the
/// same states is proved in <c>PipelineProductionSetupExternalSourceTests</c> and on PostgreSQL.</summary>
public sealed class PipelineSetupExternalSourceTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupExternalSourceTests() => _handler = BunitTestHelper.RegisterServices(this);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync(PipelineProductionAnalysisDto analysis)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, analysis);
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

    private async Task<CreatePipelineRequest[]> FinishAsync(IRenderedComponent<PipelineSetupWizard> cut, int expected)
    {
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(expected, Created().Length));
        return Created();
    }

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private bool Asked(string url) => _handler.RequestDetails.Any(request =>
        request.Method == HttpMethod.Post.Method && request.Url.EndsWith(url, StringComparison.OrdinalIgnoreCase));

    private static PipelineProductionAnalysisDto Unreadable(ProductionSourceProblem problem) => new()
    {
        Readable = false,
        ExternalSource = true,
        SourceProblem = problem
    };

    [Theory]
    [InlineData(ProductionSourceProblem.ExternalNotTheSource, "ExternalDeployCauseNotTheSource")]
    [InlineData(ProductionSourceProblem.ExternalNotSynchronized, "ExternalDeployCauseNotSynchronized")]
    public async Task AnExternalSourceThatCannotBeRead_GetsNeitherProductionNorQa_AndTheSummarySaysWhy(
        ProductionSourceProblem problem, string cause)
    {
        var cut = await OpenSummaryAsync(Unreadable(problem));

        cut.WaitForAssertion(() => Assert.Contains(cause, cut.Find("[data-testid='setup-external-deploy-blocked']").TextContent, StringComparison.Ordinal));
        // The summary lists what will be created: five pipelines, without the two that could not work.
        Assert.Equal(5, cut.FindAll("[data-testid='setup-pipelines-list'] .pipeline-setup-summary > div").Count);

        var created = await FinishAsync(cut, 5);

        Assert.DoesNotContain(created, request => request.Name is "atlas-qa" or "atlas-deploy-prod");
        // The pipelines that remain name neither of them: a candidate that triggered a QA nobody created would fail.
        Assert.All(created, request =>
        {
            Assert.DoesNotContain("APPLICATION_QA_PIPELINE", request.YamlDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("APPLICATION_DEPLOY_PIPELINE", request.YamlDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("COMPOSE_FILE", request.YamlDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("on_success", request.YamlDefinition, StringComparison.Ordinal);
        });
        Assert.Contains(Services.Toasts(), toast => toast.Summary == "ExternalDeployNotCreated");
        // Nothing of the production setup was applied (no variable, port or file written for a pipeline that does not exist).
        Assert.False(Asked(Apply));
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Summary is "ProductionNotPrepared" or "QaNoComposeFile");
    }

    [Fact]
    public async Task AnExternalSourceWhoseComposeFileIsRead_KeepsProductionAndQa()
    {
        var cut = await OpenSummaryAsync(new PipelineProductionAnalysisDto
        {
            Readable = true,
            ExternalSource = true,
            Branch = "develop",
            ComposeFiles = ["deploy/compose/remote.compose.yml"],
            ComposeFile = "deploy/compose/remote.compose.yml",
            Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
            DefaultServer = "vps"
        });

        Assert.Empty(cut.FindAll("[data-testid='setup-external-deploy-blocked']"));
        var created = await FinishAsync(cut, 7);

        Assert.Contains(created, request => request.Name == "atlas-qa");
        Assert.Contains(created, request => request.Name == "atlas-deploy-prod");
    }

    [Fact]
    public async Task AnInternalProjectWithoutCommit_KeepsTheBehaviorOfRecetteR80()
    {
        var cut = await OpenSummaryAsync(new PipelineProductionAnalysisDto { Readable = false });

        Assert.Empty(cut.FindAll("[data-testid='setup-external-deploy-blocked']"));
        var created = await FinishAsync(cut, 7);

        Assert.Contains(created, request => request.Name == "atlas-deploy-prod");
        Assert.Contains(Services.Toasts(), toast => toast.Summary == "ProductionNotPrepared");
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Summary == "ExternalDeployNotCreated");
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
