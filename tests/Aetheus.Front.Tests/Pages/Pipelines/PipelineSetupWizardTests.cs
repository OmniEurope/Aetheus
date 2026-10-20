// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineSetupWizardTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupWizardTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public async Task Wizard_PreselectsProjectAndSummarizesTheGeneratedPipelineSet()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }
        ]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        NothingMissing();

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();

        await cut.InvokeAsync(wizard.Instance.NextStep);
        await ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("atlas-ci", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("application-ci", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("atlas-deploy-prod", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("6 Pipelines", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Wizard_FinishCreatesTheCompleteCoherentDefaultPipelineSet()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }
        ]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        NothingMissing();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();

        await cut.InvokeAsync(wizard.Instance.NextStep);
        await ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(6, _handler.RequestDetails.Count(request =>
            request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))));
        var requests = _handler.RequestDetails
            .Where(request => request.Method == HttpMethod.Post.Method
                && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Equal(
            ["atlas-ci", "atlas-quality", "atlas-security", "atlas-qa", "atlas-candidate", "atlas-deploy-prod"],
            requests.Select(request => request.Name));
        Assert.All(requests, request =>
        {
            Assert.Equal(7, request.ProjectId);
            Assert.Equal("develop", request.SourceBranch);
            Assert.Contains("APPLICATION_HAS_FRONTEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_BACKEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_DATABASE: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
        });
        Assert.Contains("extends: application-candidate@1", requests[4].YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_SECURITY_ENABLED: \"true\"", requests[4].YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("extends: application-deploy-prod@1", requests[5].YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wizard_SummaryStep_ShowsWhatWouldStopThesePipelinesFromRunning()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }
        ]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = true,
            InspectedBranch = "develop",
            Checks =
            [
                new PipelineSetupReadinessCheckDto
                {
                    Kind = PipelineSetupReadinessKind.MissingAdapterScripts,
                    Severity = PipelineSetupReadinessSeverity.Blocking,
                    Items = [".pipeline/scripts/ci.sh"]
                }
            ]
        });

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();

        await cut.InvokeAsync(wizard.Instance.NextStep);
        await ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        // The check runs only on reaching the summary: the selection it depends on is settled there.
        Assert.DoesNotContain(_handler.RequestDetails, request =>
            request.Url.EndsWith("api/pipelines/setup/readiness", StringComparison.OrdinalIgnoreCase));

        await cut.InvokeAsync(wizard.Instance.NextStep);

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid='setup-readiness-check']"));
            Assert.Contains("ReadinessMissingAdapterScripts", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(".pipeline/scripts/ci.sh", cut.Markup, StringComparison.Ordinal);
            // Remediation points at the repository the author has to commit into.
            Assert.Contains("/git-repositories?projectId=7", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("PipelineSetupReadinessFilesNotChecked", cut.Markup, StringComparison.Ordinal);
        });

        // A blocking finding informs, it does not lock the wizard: writing the adapter after wiring
        // the pipelines is a legitimate order of work.
        Assert.False(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Wizard_SummaryStep_SaysTheFilesWereNotCheckedRatherThanImplyingTheyAreThere()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }
        ]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = false,
            Checks =
            [
                new PipelineSetupReadinessCheckDto
                {
                    Kind = PipelineSetupReadinessKind.NoRepository,
                    Severity = PipelineSetupReadinessSeverity.Blocking
                }
            ]
        });

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();

        await cut.InvokeAsync(wizard.Instance.NextStep);
        await ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("ReadinessNoRepository", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("PipelineSetupReadinessFilesNotChecked", cut.Markup, StringComparison.Ordinal);
        });
    }

    /// <summary>The extended QA and the Git-history secret scan the nightly runs are wizard
    /// options, named after the project and enabled for its nightly and candidate.</summary>
    [Theory]
    [InlineData("application-qa-extended", "atlas-qa-extended", "APPLICATION_QA_EXTENDED_ENABLED")]
    [InlineData("application-security-history", "atlas-security-history", "APPLICATION_SECURITY_HISTORY_ENABLED")]
    public async Task Wizard_CreatesTheNightlyExtensionsWithTheirToggleWritten(
        string template, string pipeline, string toggle)
    {
        var requests = await FinishWizardAsync(
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" },
            TemplateCatalog(),
            templateId: Template(template).Id);

        Assert.Equal(["atlas-ci", pipeline], requests.Select(request => request.Name));
        Assert.Contains($"extends: {template}@1", requests[1].YamlDefinition, StringComparison.Ordinal);
        Assert.All(requests, request =>
        {
            Assert.Contains($"{toggle}: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_QA_EXTENDED_PIPELINE: \"atlas-qa-extended\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_SECURITY_HISTORY_PIPELINE: \"atlas-security-history\"", request.YamlDefinition, StringComparison.Ordinal);
        });
    }

    /// <summary>The version pinned is that of the template the backend resolves for the project,
    /// its organization's own before the one the platform shares.</summary>
    [Fact]
    public async Task Wizard_PinsTheVersionOfTheProjectsOrganizationTemplate()
    {
        List<PipelineTemplateSummaryDto> catalog =
        [
            new() { Id = 1, Name = "application-ci", Version = 2, OrganizationId = 1 },
            new() { Id = 2, Name = "application-ci", Version = 7, OrganizationId = 5 }
        ];

        var requests = await FinishWizardAsync(
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop", OrganizationId = 5 },
            catalog,
            templateId: 1);

        Assert.Contains("extends: application-ci@7", Assert.Single(requests).YamlDefinition, StringComparison.Ordinal);
    }

    /// <summary>Reaching the summary writes the choices into the wizard's own address, the return
    /// address of the pages the summary opens.</summary>
    [Fact]
    public async Task Wizard_ReachingTheSummary_PutsTheChoicesInItsAddress()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        NothingMissing();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();

        await cut.InvokeAsync(wizard.Instance.NextStep);
        await ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);

        Assert.Equal(
            "http://localhost/pipelines/setup?projectId=7&prefix=atlas&options="
                + Uri.EscapeDataString("frontend,backend,ci,quality,security,qa,candidate,production,rollback")
                + "&architecture=application&sameProject=no&step=summary",
            nav.Uri);
    }

    /// <summary>Coming back to that address reopens the summary with the same choices, checks
    /// readiness again, and creates exactly what was chosen.</summary>
    [Fact]
    public async Task Wizard_ComingBackToTheSummaryAddress_RestoresTheSummaryWithTheSameChoices()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        NothingMissing();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7&prefix=perf&options=backend%2Cci%2Cquality&step=summary");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Contains("perf-quality", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("perf-ci", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("perf-security", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(3, cut.FindComponent<WizardDialog>().Instance.CurrentStep);
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Url.EndsWith("api/pipelines/setup/readiness", StringComparison.OrdinalIgnoreCase)));

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(2, _handler.RequestDetails.Count(request =>
            request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))));
        var created = _handler.RequestDetails
            .Where(request => request.Method == HttpMethod.Post.Method
                && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Equal(["perf-ci", "perf-quality"], created.Select(request => request.Name));
        Assert.All(created, request =>
        {
            Assert.Contains("APPLICATION_HAS_FRONTEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_BACKEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_SECURITY_ENABLED: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
        });
    }

    /// <summary>Libraries and vaults the wizard created must each be opened before the pipelines
    /// can be created.</summary>
    [Fact]
    public void Wizard_FinishWaitsUntilEveryCreatedElementWasOpened()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        NothingMissing();
        var state = Services.GetRequiredService<PipelineSetupProvisionState>();
        state.Record(7, new PipelineRequirementsProvisionResultDto
        {
            Vaults = [new ProvisionedRequirementDto { Id = 13, Name = "atlas-secrets", Keys = [] }]
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7&prefix=atlas&options=backend%2Cci&step=summary");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-provisioned-created']")));
        Assert.True(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));

        cut.Find("[data-testid='setup-provisioned-open']").Click();
        cut.Render();

        Assert.False(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
    }

    private async Task<CreatePipelineRequest[]> FinishWizardAsync(
        ProjectDto project, List<PipelineTemplateSummaryDto> catalog, int templateId)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [project]);
        _handler.SetJsonResponse("api/pipelines/templates", catalog);
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        NothingMissing();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/pipelines/setup?projectId={project.Id}&templateId={templateId}");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains(project.Name, cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase)));
        return _handler.RequestDetails
            .Where(request => request.Method == HttpMethod.Post.Method
                && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
    }

    /// <summary>The type is asked on the architecture step before anything else.</summary>
    internal static async Task ChooseArchitectureAsync(
        IRenderedComponent<PipelineSetupWizard> cut, string slug, bool? sameProject = null)
    {
        await cut.Find($"[data-testid='setup-architecture-{slug}']")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        if (sameProject is { } answer)
            await cut.Find($"[data-testid='setup-same-project'] input[value='{answer}']")
                .ChangeAsync(new ChangeEventArgs { Value = answer.ToString() });
    }

    /// <summary>Reaching the summary now asks the backend for readiness; a clean verdict keeps the
    /// cases that are about the summary itself about the summary.</summary>
    private void NothingMissing() => _handler.SetJsonResponse(
        HttpMethod.Post,
        "api/pipelines/setup/readiness",
        new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        Template("application-ci"),
        Template("application-quality"),
        Template("application-security"),
        Template("application-qa"),
        Template("application-qa-extended"),
        Template("application-security-history"),
        Template("application-candidate"),
        Template("application-nightly"),
        Template("application-deploy-prod"),
        Template("application-release-fast"),
        Template("package-telemetry"),
        Template("package-web-analytics-dotnet"),
        Template("package-web-analytics-browser"),
        Template("publish-observability-packages")
    ];

    private static PipelineTemplateSummaryDto Template(string name) => new()
    {
        Id = name.GetHashCode(StringComparison.Ordinal),
        Name = name,
        Version = 1
    };
}
