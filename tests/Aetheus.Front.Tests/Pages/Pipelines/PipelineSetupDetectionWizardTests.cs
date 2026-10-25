// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// R-10 to R-13: when the Capabilities step opens, the wizard reads what the branch holds. A branch with no
/// .NET test project starts with the QA unticked, and says why on the option; a QA ticked anyway, a CI or
/// quality without a solution, a deployment without a Compose file or Docker are warned about before the
/// pipelines are created; the candidate's <c>APPLICATION_QA_ENABLED</c> follows the final choice.
/// </summary>
public sealed class PipelineSetupDetectionWizardTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupDetectionWizardTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static readonly string[] Templates =
    [
        "application-ci", "application-quality", "application-security", "application-qa",
        "application-candidate", "application-nightly", "application-deploy-prod"
    ];

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(PipelineSetupDetectionDto detection)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", Templates.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        }).ToList());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = true,
            InspectedBranch = "develop",
            Detection = detection
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });

        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='setup-option-qa']")));
        return cut;
    }

    private static bool IsTicked(IRenderedComponent<PipelineSetupWizard> cut, string option) =>
        cut.Find($"[data-testid='setup-option-{option}']").ClassList.Contains("omni-selectable-card--selected");

    private CreatePipelineRequest[] Created() => [.. _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)];

    private async Task<CreatePipelineRequest[]> FinishAsync(IRenderedComponent<PipelineSetupWizard> cut, int expected)
    {
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(expected, Created().Length));
        return Created();
    }

    [Fact]
    public async Task BranchWithoutATestProject_StartsWithQaUnticked_SaysWhyOnTheOption_AndCreatesNoQa()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto
        {
            HasDotNetSolution = true,
            HasDotNetTestProject = false,
            HasComposeFile = true,
            HasDockerRunner = true
        });

        Assert.False(IsTicked(cut, "qa"));
        // The reason sits on the QA option itself, as a badge with its OE tooltip.
        var badge = cut.Find("[data-testid='setup-option-qa'] [data-testid='setup-qa-no-tests']");
        Assert.Contains("PipelineSetupQaNoTestsBadge", badge.TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupQaNoTestsTip", cut.Find("[data-testid='setup-option-qa']").InnerHtml, StringComparison.Ordinal);
        // Unticked: nothing to warn about yet.
        Assert.Empty(cut.FindAll("[data-testid='setup-detection-warnings']"));

        var requests = await FinishAsync(cut, 6);

        Assert.DoesNotContain(requests, request => request.Name == "atlas-qa");
        // The candidate follows the final choice.
        var candidate = Assert.Single(requests, request => request.Name == "atlas-candidate");
        Assert.Contains("APPLICATION_QA_ENABLED: \"false\"", candidate.YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QaTickedAgainOnABranchWithoutTests_WarnsOnTheCapabilitiesStep_AndTheCandidateFollows()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto
        {
            HasDotNetSolution = true,
            HasDotNetTestProject = false,
            HasComposeFile = true,
            HasDockerRunner = true
        });

        await cut.Find("[data-testid='setup-option-qa']").ClickAsync(new MouseEventArgs());

        Assert.True(IsTicked(cut, "qa"));
        var alert = cut.Find("[data-testid='setup-detection-warnings']");
        Assert.Contains("PipelineSetupWarnNoTests", alert.TextContent, StringComparison.Ordinal);

        var requests = await FinishAsync(cut, 7);

        Assert.Contains(requests, request => request.Name == "atlas-qa");
        var candidate = Assert.Single(requests, request => request.Name == "atlas-candidate");
        Assert.Contains("APPLICATION_QA_ENABLED: \"true\"", candidate.YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BranchWithTests_KeepsQaTicked_WithoutBadgeNorWarning()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto
        {
            HasDotNetSolution = true,
            HasDotNetTestProject = true,
            HasComposeFile = true,
            HasDockerRunner = true
        });

        Assert.True(IsTicked(cut, "qa"));
        Assert.Empty(cut.FindAll("[data-testid='setup-qa-no-tests']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-detection-warnings']"));
    }

    [Fact]
    public async Task DetectionThatCouldNotBeRead_ClaimsNothing_AndKeepsTheStartingOptions()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto());

        Assert.True(IsTicked(cut, "qa"));
        Assert.Empty(cut.FindAll("[data-testid='setup-qa-no-tests']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-detection-warnings']"));
    }

    [Fact]
    public async Task MissingSolutionComposeFileAndDocker_AreWarnedBeforeCreation_NamingWhatIsMissing()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto
        {
            HasDotNetSolution = false,
            HasDotNetTestProject = true,
            HasComposeFile = false,
            HasDockerRunner = false
        });

        var warnings = cut.Find("[data-testid='setup-detection-warnings']").TextContent;
        Assert.Contains("PipelineSetupWarnNoSolution", warnings, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupWarnNoCompose", warnings, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupWarnNoDocker", warnings, StringComparison.Ordinal);
        Assert.DoesNotContain("PipelineSetupWarnNoTests", warnings, StringComparison.Ordinal);

        // A warning informs, it does not lock the wizard.
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        Assert.False(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
    }

    [Fact]
    public async Task OptionsComingBackFromTheSummaryAddress_AreNotChangedByTheDetection()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", Templates.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        }).ToList());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = true,
            InspectedBranch = "develop",
            Detection = new PipelineSetupDetectionDto { HasDotNetTestProject = false }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "http://localhost/pipelines/setup?projectId=7&prefix=atlas&options=ci,quality,security,qa,candidate,production,rollback&architecture=application&sameProject=yes&step=summary");

        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find("button.omni-wizard__previous").ClickAsync(new MouseEventArgs());

        Assert.True(IsTicked(cut, "qa"));
    }
}
