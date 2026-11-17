// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-38 (OE1): the project already holds <c>atlas-security</c> and the wizard is run again as "Application". It
/// used to try to create it, the server's unique index refused with a generic write conflict and the user only read
/// "Operation failed". The wizard now reads the project's pipeline names, says which ones it keeps, creates the others, and
/// never writes a variable naming a pipeline it did not create. A refusal that remains (a name taken since, a refused
/// definition) is told by pipeline name and in the server's own words.</summary>
public sealed class PipelineSetupExistingPipelinesTests : BunitContext
{
    private const string ProjectPipelines = "api/pipelines?page=1&pageSize=200&projectId=7";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupExistingPipelinesTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Arrange(params string[] existing)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, ProjectPipelines,
            existing.Select((name, index) => new PipelineDto { Id = 300 + index, Name = name, ProjectId = 7 }));
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync()
    {
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

    private static Task FinishAsync(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    [Fact]
    public async Task APipelineTheProjectAlreadyHolds_IsKept_NamedOnTheSummary_AndTheOthersAreCreated()
    {
        Arrange("atlas-security");
        var cut = await OpenSummaryAsync();

        // The test localizer returns the key: the sentence names the pipelines, checked by what the wizard creates below.
        cut.WaitForAssertion(() => Assert.Contains("PipelineSetupExistingSome", cut.Find("[data-testid='setup-existing-pipelines']").TextContent, StringComparison.Ordinal));
        await FinishAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal(6, Created().Length));

        var created = Created();
        Assert.DoesNotContain(created, request => request.Name == "atlas-security");
        // R-70 (user decision 2026-11-03, replacing the R-38 rule that left the kept pipeline unnamed): a pipeline the project
        // holds is named in the candidate with its real name, like one created in the same pass.
        var candidate = Assert.Single(created, request => request.Name == "atlas-candidate").YamlDefinition;
        Assert.Contains("APPLICATION_SECURITY_PIPELINE: \"atlas-security\"", candidate, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_QUALITY_PIPELINE: \"atlas-quality\"", candidate, StringComparison.Ordinal);
    }

    /// <summary>Recette R-70 (OE2): the wizard is run a second time on a project that already holds its security and quality
    /// pipelines (created by a first pass as an external application). The candidate used to be written without
    /// <c>APPLICATION_QUALITY_PIPELINE</c> and <c>APPLICATION_SECURITY_PIPELINE</c> and fell back on the template's names
    /// <c>application-quality</c> and <c>application-security</c>, which the project does not have.</summary>
    [Fact]
    public async Task ASecondPass_WritesTheCandidateWithTheRealNamesOfTheSecurityAndQualityPipelinesTheProjectHolds()
    {
        Arrange("atlas-quality", "atlas-security");
        var cut = await OpenSummaryAsync();

        await FinishAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal(5, Created().Length));

        var created = Created();
        Assert.DoesNotContain(created, request => request.Name is "atlas-quality" or "atlas-security");
        var candidate = Assert.Single(created, request => request.Name == "atlas-candidate").YamlDefinition;
        Assert.Contains("APPLICATION_QUALITY_PIPELINE: \"atlas-quality\"", candidate, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_SECURITY_PIPELINE: \"atlas-security\"", candidate, StringComparison.Ordinal);
        // A pipeline the project lacks is still never named: the nightly run was not asked for a history pipeline.
        Assert.DoesNotContain("APPLICATION_SECURITY_HISTORY_PIPELINE", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APipelineTheProjectLacks_IsStillNeverNamedInTheCandidate()
    {
        Arrange("atlas-quality");
        var cut = await OpenSummaryAsync();

        await FinishAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal(6, Created().Length));

        var candidate = Assert.Single(Created(), request => request.Name == "atlas-candidate").YamlDefinition;
        Assert.Contains("APPLICATION_QUALITY_PIPELINE: \"atlas-quality\"", candidate, StringComparison.Ordinal);
        // atlas-security is created in this pass, so it is named too; nothing is named that exists nowhere.
        Assert.Contains("APPLICATION_SECURITY_PIPELINE: \"atlas-security\"", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("APPLICATION_SECURITY_HISTORY_PIPELINE", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNothingExists_NoWarningIsShown_AndEverythingIsCreated()
    {
        Arrange();
        var cut = await OpenSummaryAsync();

        Assert.Empty(cut.FindAll("[data-testid='setup-existing-pipelines']"));
        await FinishAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
    }

    [Fact]
    public async Task WhenEveryPipelineAlreadyExists_NothingCanBeCreated_AndTheSummarySaysWhy()
    {
        Arrange("atlas-ci", "atlas-quality", "atlas-security", "atlas-qa", "atlas-candidate", "atlas-nightly", "atlas-deploy-prod");
        var cut = await OpenSummaryAsync();

        cut.WaitForAssertion(() => Assert.Contains("PipelineSetupExistingAll",
            cut.Find("[data-testid='setup-existing-pipelines']").TextContent, StringComparison.Ordinal));
        Assert.True(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
    }

    private const string TakenSentence = "A pipeline named 'atlas-ci' already exists in this project.";

    /// <summary>The conflict of a name taken since the names were read: the API client carries the server's sentence (it used to
    /// return nothing for a 409, and the wizard fell back on the generic "Operation failed").</summary>
    [Fact]
    public async Task AConflictOnCreation_CarriesTheServersOwnSentence()
    {
        Arrange();
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new { message = TakenSentence }, HttpStatusCode.Conflict);

        var outcome = await Services.GetRequiredService<ApiClient>().Pipelines.CreatePipelineAsync(
            new CreatePipelineRequest { Name = "atlas-ci", ProjectId = 7, YamlDefinition = "name: atlas-ci" },
            Xunit.TestContext.Current.CancellationToken);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, outcome.StatusCode);
        Assert.Equal(TakenSentence, outcome.ErrorMessage);
    }

    [Fact]
    public async Task ANameTakenSinceTheSummary_IsRefused_AndTheWizardNamesThePipeline_NotTheGenericFailure()
    {
        Arrange();
        var cut = await OpenSummaryAsync();
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new { message = TakenSentence }, HttpStatusCode.Conflict);

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger));
        var summaries = Services.Toasts().Select(toast => toast.Summary).ToList();
        Assert.Contains("PipelineCreateFailed", summaries);
        Assert.DoesNotContain("OperationFailed", summaries);
        // Nothing was created: the first pipeline was the one refused.
        Assert.All(Created(), request => Assert.Equal("atlas-ci", request.Name));
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
