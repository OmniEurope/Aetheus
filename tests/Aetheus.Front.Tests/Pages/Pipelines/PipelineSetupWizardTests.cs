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
            // The summary line carries the count (the localizer stub returns the key, not its format).
            Assert.Contains("PipelineSetupRecapPipelineCount", cut.Find("[data-testid='setup-summary']").TextContent, StringComparison.Ordinal);
            Assert.Equal(7, cut.FindAll("[data-testid='setup-pipelines-list'] .pipeline-setup-summary > div").Count);
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

        cut.WaitForAssertion(() => Assert.Equal(7, _handler.RequestDetails.Count(request =>
            request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))));
        var requests = _handler.RequestDetails
            .Where(request => request.Method == HttpMethod.Post.Method
                && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Equal(
            ["atlas-ci", "atlas-quality", "atlas-security", "atlas-qa", "atlas-candidate", "atlas-nightly", "atlas-deploy-prod"],
            requests.Select(request => request.Name));
        Assert.All(requests, request =>
        {
            Assert.Equal(7, request.ProjectId);
            Assert.Equal("develop", request.SourceBranch);
            Assert.Contains("APPLICATION_HAS_FRONTEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_BACKEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            // R2-2: the database is ticked by default.
            Assert.Contains("APPLICATION_HAS_DATABASE: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
        });
        Assert.Contains("extends: application-candidate@1", requests[4].YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_SECURITY_ENABLED: \"true\"", requests[4].YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("extends: application-deploy-prod@1", requests[6].YamlDefinition, StringComparison.Ordinal);
    }

    /// <summary>R-60: the end of the wizard lands on the project's overview, not on its pipelines page.</summary>
    [Fact]
    public async Task Wizard_FinishNavigatesToTheProjectOverview()
    {
        await FinishWizardAsync(new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }, TemplateCatalog());

        var nav = Services.GetRequiredService<NavigationManager>();
        await Task.Run(() => SpinWait.SpinUntil(() => nav.Uri.EndsWith("/projects/7", StringComparison.Ordinal), TimeSpan.FromSeconds(10)));
        Assert.Equal("http://localhost/projects/7", nav.Uri);
    }

    /// <summary>User decision 2026-10-24: every pipeline the wizard creates is pinned to the number its template has in
    /// the catalog at that moment (its latest published version), never to a floating latest.</summary>
    [Fact]
    public async Task Wizard_PinsEachPipelineToTheLatestPublishedNumberOfItsTemplate()
    {
        var catalog = TemplateCatalog().Select((template, index) => template with { Version = index + 2 }).ToList();
        var requests = await FinishWizardAsync(new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }, catalog);

        Assert.NotEmpty(requests);
        Assert.All(requests, request =>
        {
            var extends = request.YamlDefinition.Split('\n').Single(line => line.StartsWith("extends: ", StringComparison.Ordinal));
            var reference = extends["extends: ".Length..];
            var at = reference.LastIndexOf('@');
            Assert.True(at > 0, $"'{reference}' carries no version number.");
            var template = catalog.Single(item => item.Name == reference[..at]);
            Assert.Equal(template.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), reference[(at + 1)..]);
        });
        // "latest" never names a template version. The only line allowed to carry the word is the release selector of the
        // candidate's chain to the deployment (R-72: the release the candidate just published), which is not a template.
        Assert.All(requests, request => Assert.DoesNotContain(
            request.YamlDefinition.Split('\n').Where(line => line != "    release: latest"),
            line => line.Contains("latest", StringComparison.OrdinalIgnoreCase)));
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
        // The readiness verdict of the selection runs only on reaching the summary: the selection it depends on
        // is settled there. The Capabilities step only reads what the branch holds, for the four templates whose
        // default commands need it (R-10 to R-13), never for the selection.
        var branchReads = _handler.RequestDetails.Where(request =>
            request.Url.EndsWith("api/pipelines/setup/readiness", StringComparison.OrdinalIgnoreCase)).ToList();
        var branchRead = Assert.Single(branchReads);
        Assert.Contains("application-qa", branchRead.Body, StringComparison.Ordinal);
        Assert.Contains("application-deploy-prod", branchRead.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("application-security", branchRead.Body, StringComparison.Ordinal);

        await cut.InvokeAsync(wizard.Instance.NextStep);

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid='setup-readiness-check']"));
            Assert.Contains("ReadinessMissingAdapterScripts", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(".pipeline/scripts/ci.sh", cut.Markup, StringComparison.Ordinal);
            // Remediation points at the repository the author has to commit into.
            Assert.Contains("/git-repositories?projectId=7", cut.Markup, StringComparison.Ordinal);
        });

        // A blocking finding informs, it does not lock the wizard: writing the adapter after wiring
        // the pipelines is a legitimate order of work.
        Assert.False(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));

        // Recette R2-24: the summary and what is left to do are told apart by a rule (OmniDivider), and so
        // is each card of the to-do block from the one before it.
        var recap = cut.Find("[data-testid='setup-recap']");
        var order = recap.Children.Select(child => child.ClassList.Contains("omni-divider") ? "divider" : child.GetAttribute("data-testid")).ToList();
        // ADR-057: the production pipeline is selected, and the repository of this project could not be read.
        Assert.Equal(["setup-summary", "divider", "setup-readiness", "setup-production-unreadable"], order);
        var readiness = cut.Find("[data-testid='setup-readiness']");
        var cards = readiness.Children.Where(child => child.ClassList.Contains("omni-card")).ToList();
        Assert.True(cards.Count >= 2, "the blocking finding is a second card after the to-do card");
        Assert.All(cards.Skip(1), card => Assert.Contains("omni-divider", card.PreviousElementSibling!.ClassName, StringComparison.Ordinal));
        Assert.All(cut.FindAll("[data-testid='setup-recap'] .omni-divider"), divider => Assert.Equal("separator", divider.GetAttribute("role")));
    }

    [Fact]
    public async Task Wizard_SummaryStep_WithoutARepository_OffersItsCreationAndWaitsForIt()
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
            Assert.Single(cut.FindAll("[data-testid='setup-readiness-create-repository']")));
        // R2-19: no sentence about files that were not checked, and the repository is created, not described.
        Assert.DoesNotContain("PipelineSetupReadinessFilesNotChecked", cut.Markup, StringComparison.Ordinal);
        // R2-17: the creation waits for the repository, and the one alert says so.
        Assert.True(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
        Assert.Single(cut.FindAll("[data-testid='setup-blocked']"));
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
            // R2-11: only the pipelines really created are named.
            var variable = toggle.Replace("_ENABLED", "_PIPELINE", StringComparison.Ordinal);
            Assert.Contains($"{variable}: \"{pipeline}\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_CI_PIPELINE: \"atlas-ci\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("APPLICATION_DEPLOY_PIPELINE", request.YamlDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("APPLICATION_CANDIDATE_PIPELINE", request.YamlDefinition, StringComparison.Ordinal);
            Assert.DoesNotContain("APPLICATION_QUALITY_PIPELINE", request.YamlDefinition, StringComparison.Ordinal);
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

    /// <summary>R2-14: the "start from" choice is the first thing of the Project step, inside the one
    /// wizard (never above it); a click swaps the steps behind it, it is gone once Next went past that
    /// step and it can be changed again by going back to it.</summary>
    [Fact]
    public async Task StartFromChoice_IsInsideTheProjectStep_AndSwapsTheStepsOfTheSameWizard()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 1, Name = "Aetheus", DefaultBranch = "main" },
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }
        ]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/copy-plan", new PipelineProjectCopyPlanDto());
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();

        // Inside the Project step's body, in the wizard.
        Assert.Single(cut.FindAll(".omni-wizard [data-testid='setup-start-from']"));
        Assert.Equal(4, wizard.Instance.Steps.Count);
        Assert.NotEmpty(cut.FindAll("#oe-formfield-pages-pipelines-pipelinesetupwizardstep-2"));

        // One click on the other mode updates everything that depends on it: the steps and the fields.
        await SelectStartFromAsync(cut, copy: true);
        Assert.Same(wizard.Instance, cut.FindComponent<WizardDialog>().Instance);
        Assert.Equal(["Project", "PipelineCopyStepPipelines", "WizardStepSummary"], wizard.Instance.Steps.Select(step => step.Title));
        Assert.Single(cut.FindAll(".omni-wizard [data-testid='setup-start-from']"));
        Assert.NotEmpty(cut.FindAll("#pipeline-copy-source"));
        Assert.Empty(cut.FindAll("#oe-formfield-pages-pipelines-pipelinesetupwizardstep-2"));
        Assert.Equal(7, cut.FindComponent<PipelineProjectCopyStep>().Instance.Flow.TargetProjectId);
        await SelectStartFromAsync(cut, copy: false);
        Assert.Equal(4, wizard.Instance.Steps.Count);
        Assert.Empty(cut.FindAll("#pipeline-copy-source"));
        Assert.NotEmpty(cut.FindAll("#oe-formfield-pages-pipelines-pipelinesetupwizardstep-2"));

        // Past the Project step, the choice is gone; going back brings it back, still changeable.
        await cut.InvokeAsync(wizard.Instance.NextStep);
        Assert.Empty(cut.FindAll("[data-testid='setup-start-from']"));
        await cut.Find("button.omni-wizard__previous").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Single(cut.FindAll("[data-testid='setup-start-from']"));
        await SelectStartFromAsync(cut, copy: true);
        Assert.Equal(3, wizard.Instance.Steps.Count);
        Assert.NotEmpty(cut.FindAll("#pipeline-copy-source"));
    }

    /// <summary>The project chosen in the copy mode is the one the template mode starts with after the
    /// switch back, so the switch never loses it.</summary>
    [Fact]
    public async Task StartFromChoice_KeepsTheProjectChosenInTheCopyMode()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 1, Name = "Aetheus", DefaultBranch = "main" },
            new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }
        ]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));

        await SelectStartFromAsync(cut, copy: true);
        await cut.InvokeAsync(() => cut.FindComponent<PipelineProjectCopyStep>().Instance.Flow.SetTargetProject(1));
        await SelectStartFromAsync(cut, copy: false);

        cut.WaitForAssertion(() => Assert.Equal("aetheus",
            cut.Find("#oe-formfield-pages-pipelines-pipelinesetupwizardstep-2").GetAttribute("value")));
    }

    /// <summary>The two "start from" radios are named by their visible labels, never by the raw
    /// "True" / "False" of a boolean value: the label is the radio's value and its name.</summary>
    [Fact]
    public void StartFromRadios_AreNamedByTheirVisibleLabels()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));

        var radios = cut.FindAll("[data-testid='setup-start-from'] input[type='radio']");

        Assert.Equal(2, radios.Count);
        Assert.Equal(["PipelineSetupStartTemplates", "PipelineSetupStartExistingProject"],
            radios.Select(radio => radio.ParentElement!.TextContent.Trim()));
        Assert.All(radios, radio =>
        {
            Assert.Equal("LABEL", radio.ParentElement!.TagName);
            Assert.Equal(radio.ParentElement.TextContent.Trim(), radio.GetAttribute("value"));
            Assert.DoesNotContain(radio.GetAttribute("value"), new[] { "True", "False" });
        });
        Assert.Equal("PipelineSetupStartFrom", cut.Find("[data-testid='setup-start-from'] legend").TextContent);
    }

    /// <summary>A real change event on a radio of the choice switches the mode.</summary>
    internal static async Task SelectStartFromAsync(IRenderedComponent<PipelineSetupWizard> cut, bool copy)
    {
        var radios = cut.FindAll("[data-testid='setup-start-from'] input[type='radio']");
        await radios[copy ? 1 : 0].ChangeAsync(new ChangeEventArgs());
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
                + Uri.EscapeDataString("frontend,backend,database,ci,quality,security,qa,candidate,nightly,production,rollback,lang-dotnet")
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

    /// <summary>R2-19: libraries and vaults the wizard created are listed as created; the pipelines can be
    /// created without opening each one of them (opening one left the wizard).</summary>
    [Fact]
    public void Wizard_FinishDoesNotWaitForTheCreatedElementsToBeOpened()
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

        Assert.False(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid='setup-provisioned-open']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-blocked']"));
    }

    /// <summary>R2-17: while a required vault is missing the creation is impossible: the final action is
    /// disabled and a single alert says why; the summary line counts the element to create.</summary>
    [Fact]
    public void Wizard_FinishWaitsForTheMissingElements_WithASingleAlert()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = true,
            Checks =
            [
                new PipelineSetupReadinessCheckDto
                {
                    Kind = PipelineSetupReadinessKind.MissingRequiredVaults,
                    Severity = PipelineSetupReadinessSeverity.Blocking,
                    Items = ["atlas.qa"]
                }
            ]
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7&prefix=atlas&options=backend%2Cci&step=summary");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-blocked']")));
        Assert.True(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
        Assert.Contains("PipelineSetupRecapElementsToCreate", cut.Find("[data-testid='setup-todo-elements-state']").TextContent, StringComparison.Ordinal);
    }

    /// <summary>R2-17: the list of the pipelines to create is folded away until the reader opens it.</summary>
    [Fact]
    public void Wizard_SummaryStep_FoldsThePipelinesListByDefault()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        NothingMissing();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7&prefix=atlas&options=backend%2Cci&step=summary");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-pipelines-list']")));
        var list = cut.Find("[data-testid='setup-pipelines-list']");
        // The OE collapsible group is a native disclosure; it must be one, and it must start closed.
        var disclosure = list.TagName == "DETAILS" ? list : list.QuerySelector("details");
        Assert.NotNull(disclosure);
        Assert.False(disclosure.HasAttribute("open"), "the pipelines list must start folded");
        Assert.Empty(cut.FindAll("[data-testid='setup-blocked']"));
    }

    /// <summary>R2-24: the summary is one block per subject, each a card of its own, the lines of the first one
    /// told apart by OE's description list; no alert stacks on them when nothing blocks.</summary>
    [Fact]
    public void Wizard_SummaryStep_IsOneBlockPerSubject_WithoutStackedAlerts()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = true,
            Checks =
            [
                new PipelineSetupReadinessCheckDto
                {
                    Kind = PipelineSetupReadinessKind.NoRunnerConfigured,
                    Severity = PipelineSetupReadinessSeverity.Blocking
                }
            ]
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7&prefix=atlas&options=backend%2Cci&step=summary");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-checks']")));
        var blocks = cut.FindAll("[data-testid='setup-recap'] > .omni-card, [data-testid='setup-recap'] > .pipeline-setup-readiness > .omni-card");
        Assert.Equal(["setup-summary", "setup-todo", "setup-checks"], blocks.Select(block => block.GetAttribute("data-testid")));
        // The first block: project, type and pipelines, one line each, in OE's description list.
        var summary = cut.Find("[data-testid='setup-summary']");
        Assert.Equal(3, summary.QuerySelectorAll("dl > .omni-description-list__item").Length);
        Assert.Contains("Atlas", summary.TextContent, StringComparison.Ordinal);
        // A finding that does not stop the creation is a row, not an alert, and the creation can go on.
        Assert.Empty(cut.FindAll(".omni-alert"));
        Assert.False(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
    }

    private async Task<CreatePipelineRequest[]> FinishWizardAsync(
        ProjectDto project, List<PipelineTemplateSummaryDto> catalog, int? templateId = null)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [project]);
        _handler.SetJsonResponse("api/pipelines/templates", catalog);
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        NothingMissing();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/pipelines/setup?projectId={project.Id}"
                + (templateId is { } id ? $"&templateId={id}" : string.Empty));
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains(project.Name, cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        // Without a template chosen the wizard creates the complete default set of seven pipelines.
        var expected = templateId is null ? 7 : 1;
        cut.WaitForAssertion(() => Assert.True(_handler.RequestDetails.Count(request =>
            request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase)) >= expected));
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
        if (sameProject is { } fullStack)
            await cut.Find($"[data-testid='setup-shape-{(fullStack ? "fullstack" : "separate")}']")
                .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
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
