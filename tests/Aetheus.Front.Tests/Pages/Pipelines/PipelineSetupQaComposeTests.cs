// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-75 (W6): the QA pipeline deploys its disposable stack from the project's Compose file. Created without
/// it, <c>w6-qa</c> deployed nothing and its run was a hollow success. The wizard writes the Compose file it detects into the QA
/// pipeline whether or not the production deployment is created in the same pass, and says when it found none.</summary>
public sealed class PipelineSetupQaComposeTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string ComposeFile = "deploy/compose/remote.compose.yml";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupQaComposeTests() => _handler = BunitTestHelper.RegisterServices(this);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync(
        PipelineProductionAnalysisDto analysis, params string[] unticked)
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
        foreach (var option in unticked)
            await cut.Find($"[data-testid='setup-option-{option}']").ClickAsync(new MouseEventArgs());
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

    private static Dictionary<object, object> Variables(CreatePipelineRequest request) =>
        (Dictionary<object, object>)Yaml.Deserialize<Dictionary<object, object>>(request.YamlDefinition)["variables"];

    private bool Asked(string url) => _handler.RequestDetails.Any(request =>
        request.Method == HttpMethod.Post.Method && request.Url.EndsWith(url, StringComparison.OrdinalIgnoreCase));

    private static PipelineProductionAnalysisDto Readable(string? composeFile) => new()
    {
        Readable = true,
        Branch = "develop",
        ComposeFiles = composeFile is null ? [] : [composeFile],
        ComposeFile = composeFile,
        Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
        DefaultServer = "vps"
    };

    [Fact]
    public async Task QaTickedWithoutProduction_TheQaPipelineStillGetsTheComposeFileOfTheRepository()
    {
        var cut = await OpenSummaryAsync(Readable(ComposeFile), "production");

        var created = await FinishAsync(cut, 6);

        Assert.Equal(ComposeFile, Variables(Assert.Single(created, request => request.Name == "atlas-qa"))["COMPOSE_FILE"]);
        Assert.DoesNotContain(created, request => request.Name == "atlas-deploy-prod");
        Assert.All(created.Where(request => request.Name != "atlas-qa"),
            request => Assert.DoesNotContain("COMPOSE_FILE", request.YamlDefinition, StringComparison.Ordinal));
        // The production setup (variables, ports, files) is not applied for a pipeline that is not created.
        Assert.False(Asked(Apply));
    }

    [Fact]
    public async Task QaTickedWhenNoComposeFileIsFound_TheEndOfTheWizardSaysTheQaDeploysNothing()
    {
        var cut = await OpenSummaryAsync(Readable(null), "production");

        var created = await FinishAsync(cut, 6);

        Assert.DoesNotContain("COMPOSE_FILE", Assert.Single(created, request => request.Name == "atlas-qa").YamlDefinition, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Warning && toast.Summary == "QaNoComposeFile");
    }

    [Fact]
    public async Task WithTheQaAndTheProductionUnticked_TheRepositoryIsNotReadForAComposeFile()
    {
        var cut = await OpenSummaryAsync(Readable(ComposeFile), "production", "qa");

        var created = await FinishAsync(cut, 5);

        Assert.DoesNotContain(created, request => request.YamlDefinition.Contains("COMPOSE_FILE", StringComparison.Ordinal));
        Assert.False(Asked(Analysis));
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Summary == "QaNoComposeFile");
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];
}
