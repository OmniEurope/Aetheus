// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

public class PipelineEditTests : BunitContext
{
    public PipelineEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    private static string ModelYaml(IRenderedComponent<PipelineEdit> component)
    {
        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(component.Instance)!;
        return (string)model.GetType().GetProperty("YamlDefinition")!.GetValue(model)!;
    }

    private void SetupNewPipelineMocks()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string>());
        _handler.SetJsonResponse("api/vaults/names", new List<string>());
        _handler.SetJsonResponse("api/servers/names", new List<string>());
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 });
    }

    private void SetupEditPipelineMocks(int id = 5)
    {
        _handler.SetJsonResponse($"api/pipelines/{id}", new PipelineDto
        {
            Id = id,
            Name = "Deploy Prod",
            Description = "Production deploy",
            YamlDefinition = "name: deploy\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual
        });
        _handler.SetJsonResponse($"api/pipelines/{id}/runs", new PaginatedResult<PipelineRunDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        _handler.SetJsonResponse($"api/pipelines/{id}/validate", new PipelineYamlDefinition());
        _handler.SetJsonResponse($"api/pipelines/{id}/run", new PipelineRunDto { Id = 1, PipelineId = id });
        _handler.SetJsonResponse($"api/pipelines/{id}/dry-run", new DryRunResultDto());
    }

    [Fact]
    public void NewPipeline_ShowsEmptyForm()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        // New-pipeline mode renders the create title and never fetches an existing pipeline detail.
        Assert.Contains("NewPipeline", cut.Markup);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/pipelines/") && !r.Url.Contains("templates"));
    }

    [Fact]
    public void EditPipeline_LoadsExistingData()
    {
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        Assert.Contains("Deploy Prod", cut.Markup);
    }

    [Fact]
    public void Header_ShowsRunAlone_AndEditIsTheFirstEntryOfTheMoreActionsMenu()
    {
        // Recette R-421 (standard): Run is the page's one visible action; Edit moved into the menu.
        SetupNewPipelineMocks(); // the edit tab the menu entry opens loads the editor's name lists
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        var trigger = cut.WaitForElement(".omni-page-header__menu .omni-overflow-menu__trigger", TimeSpan.FromSeconds(2));

        var headerButtons = cut.FindAll(".omni-page-header__actions > .omni-button");
        Assert.DoesNotContain(headerButtons, button => button.TextContent.Contains("Edit", StringComparison.Ordinal));
        Assert.Single(cut.FindAll(".omni-page-header__actions .omni-split-button"));

        trigger.Click();
        var items = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item");
        Assert.Contains("Edit", items[0].TextContent, StringComparison.Ordinal);

        items[0].Click();
        Assert.Equal(1, (int)typeof(PipelineEdit).GetField("_selectedTab", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!);
        Assert.Empty(cut.FindAll(".omni-overflow-menu__popup"));
    }

    [Fact]
    public void RunsTab_EachSummaryTileHasItsLabelLineThenItsValueLine()
    {
        // Recette R-417: the grade badge sat glued to its label; every tile now has a label line and a
        // value line of its own.
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.FindAll(".pipeline-runs-summary-card").Count == 4, TimeSpan.FromSeconds(2));

        Assert.All(cut.FindAll(".pipeline-runs-summary-card .omni-card__body"), body =>
        {
            var children = body.Children;
            Assert.Equal(2, children.Length);
            Assert.Contains("pipeline-runs-summary-label", children[0].ClassName, StringComparison.Ordinal);
            Assert.Contains("pipeline-runs-summary-value", children[1].ClassName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RunsTab_LatestGradeAndLastSuccessTiles_OpenTheirRun()
    {
        // Recette R2-128: "latest grade" opens the graded run on its Gate tab (its findings), "last
        // success" opens the successful run; the two other tiles stay plain.
        SetupEditPipelineMocks();
        var start = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
        _handler.SetJsonResponse("api/pipelines/5/runs", new PaginatedResult<PipelineRunDto>
        {
            Items =
            [
                new PipelineRunDto { Id = 31, PipelineId = 5, Status = PipelineStatus.Failed, StartedAt = start.AddMinutes(30), CompletedAt = start.AddMinutes(32), GateGrade = AnalysisGrade.F },
                new PipelineRunDto { Id = 21, PipelineId = 5, Status = PipelineStatus.Success, StartedAt = start.AddMinutes(20), CompletedAt = start.AddMinutes(22), GateGrade = AnalysisGrade.A }
            ],
            TotalCount = 2
        });
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.FindAll("a.tile-link").Count == 2, TimeSpan.FromSeconds(2));

        var links = cut.FindAll("a.tile-link").Select(link => link.GetAttribute("href")).ToList();
        Assert.Equal(["/pipelines/runs/31?tab=gate", "/pipelines/runs/21"], links);
        Assert.Equal(2, cut.FindAll(".pipeline-runs-summary-card.tile-clickable").Count);
        Assert.Equal(4, cut.FindAll(".pipeline-runs-summary-card").Count);
    }

    [Fact]
    public void ProjectPipeline_Breadcrumb_Includes_Project_And_ProjectSection()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "Deploy Prod",
            Description = "Production deploy",
            YamlDefinition = "name: deploy\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual,
            ProjectId = 2,
            ProjectName = "Toto"
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 2, Name = "Toto" }],
            TotalCount = 1
        });

        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();

        cut.WaitForAssertion(() => Assert.Equal(
            ["Projects", "Toto", "Pipelines", "Deploy Prod"],
            breadcrumb.Items.Select(item => item.Text)));
        Assert.Equal("/projects/2/pipelines", breadcrumb.Items[2].Href);
    }

    [Fact]
    public void EditPipeline_WhenTemplateIsOutdated_ShowsFleetUpdateAction()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/fleet-item", new PipelineFleetItemDto
        {
            PipelineId = 5,
            PipelineName = "Deploy Prod",
            TemplateName = "ci",
            PinnedVersion = 1,
            LatestVersion = 2,
            Freshness = PipelineFleetFreshness.Outdated
        });

        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        var overflowTrigger = cut.WaitForElement(".omni-overflow-menu__trigger", TimeSpan.FromSeconds(2));

        // The secondary header actions now live in the overflow menu, which renders its items only
        // while it is open - so the action must be absent before the trigger is clicked.
        Assert.DoesNotContain("UpdatePipelineTemplate", cut.Markup, StringComparison.Ordinal);
        overflowTrigger.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(
                "UpdatePipelineTemplate",
                cut.Find(".omni-overflow-menu__popup").InnerHtml,
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TemplateSelection_AppliesTypedParameterValuesAsPinnedDefaults()
    {
        _handler.SetJsonResponse("api/pipelines/templates/7", new PipelineTemplateDto
        {
            Id = 7,
            Name = "dotnet-ci",
            Version = 3,
            YamlContent = """
                name: dotnet-ci
                parameters:
                  - name: environment
                    type: choice
                    required: true
                    allowed_values: [qa, production]
                stages: []
                """
        });
        var coordinator = new PipelineTemplateEditorCoordinator(
            Services.GetRequiredService<ApiClient>());

        var selection = await coordinator.SelectAsync(7, "toto-ci");
        Assert.NotNull(selection);
        Assert.Single(selection.Parameters);
        // R-490: "environment" is required and has no value yet, so nothing is sent to the resolver (it
        // answered 400 and the template could not be selected); the base is the template as written.
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("/resolve", StringComparison.Ordinal));

        var yaml = PipelineTemplateEditorCoordinator.ApplyParameters(
            selection, new Dictionary<string, string> { ["environment"] = "production" });
        var definition = new YamlSerializationService().Parse(yaml);

        Assert.NotNull(definition);
        Assert.Equal("dotnet-ci@3", definition.Extends);
        var parameter = Assert.Single(definition.Parameters);
        Assert.Equal("production", parameter.Default);
        Assert.True(parameter.Required);
        Assert.Equal(["qa", "production"], parameter.AllowedValues);
    }

    [Fact]
    public void R490_TheValuesATemplateIsResolvedWith_AreItsDefaultsOverriddenByThePipeline()
    {
        const string template = """
            name: host-bluegreen-deploy
            parameters:
              - name: revision
                required: true
              - name: colour
                default: blue
            stages: []
            """;
        const string pipeline = """
            name: aetheus-deploy-prod
            extends: host-bluegreen-deploy@1
            parameters:
              - name: revision
                default: candidate
            stages: []
            """;

        var values = PipelineTemplateEditorCoordinator.ParameterValues(template, pipeline);

        Assert.NotNull(values);
        Assert.Equal("candidate", values["revision"]);
        Assert.Equal("blue", values["colour"]);
        // Without the pipeline's default the required parameter has no value: nothing to resolve.
        Assert.Null(PipelineTemplateEditorCoordinator.ParameterValues(template, null));
    }

    [Fact]
    public async Task R490_OpeningAPipelineThatDefaultsARequiredTemplateParameter_ResolvesItsBase()
    {
        _handler.SetJsonResponse("api/pipelines/templates/7", new PipelineTemplateDto
        {
            Id = 7,
            Name = "host-bluegreen-deploy",
            Version = 1,
            YamlContent = "name: host-bluegreen-deploy\nparameters:\n  - name: revision\n    required: true\nstages: []"
        });
        _handler.SetJsonResponse("api/pipelines/templates/7/resolve?version=1", "name: host-bluegreen-deploy\nstages: []");
        var coordinator = new PipelineTemplateEditorCoordinator(Services.GetRequiredService<ApiClient>());

        var baseYaml = await coordinator.LoadBaseYamlAsync(
            "name: aetheus-deploy-prod\nextends: host-bluegreen-deploy@1\nparameters:\n  - name: revision\n    default: candidate\nstages: []",
            [new PipelineTemplateSummaryDto { Id = 7, Name = "host-bluegreen-deploy", Version = 1 }]);

        Assert.NotNull(baseYaml);
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/templates/7/resolve?version=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TemplateSelection_ApiFailureIsNotMaskedAsNoSelection()
    {
        _handler.SetResponse("api/pipelines/templates/7", System.Net.HttpStatusCode.ServiceUnavailable);
        var coordinator = new PipelineTemplateEditorCoordinator(
            Services.GetRequiredService<ApiClient>());

        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.SelectAsync(7, "toto-ci"));
    }

    [Fact]
    public void RunParametersDialog_AllowsTemplateSpecificSubmitLabel()
    {
        var cut = Render<RunParametersDialog>(parameters => parameters
            .Add(component => component.Parameters,
            [
                new PipelineRunParameterDto
                {
                    Name = "environment",
                    DisplayName = "Environment",
                    Required = true
                }
            ])
            .Add(component => component.SubmitText, "ApplyTemplateParameters")
            .Add(component => component.SubmitIcon, "check"));

        Assert.Contains("ApplyTemplateParameters", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Check);
    }

    [Fact]
    public void EditPipeline_LegacyTemplateReference_WarnsAndPinsCurrentVersion()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "Deploy Prod",
            YamlDefinition = "name: deploy\nextends: 'Basic CI'\nstages: []",
            TriggerType = PipelineTriggerType.Manual
        });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 1, Name = "Basic CI", Version = 3 }
        });
        _handler.SetJsonResponse("api/pipelines/templates/1", new PipelineTemplateDto
        {
            Id = 1,
            Name = "Basic CI",
            Version = 3,
            Versions = [new PipelineTemplateVersionDto { Version = 3, YamlContent = "name: basic\nstages: []" }]
        });
        _handler.SetJsonResponse(
            "api/pipelines/templates/1/resolve?version=3",
            "name: basic\nstages: []");

        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("LegacyTemplateReferenceWarning"), TimeSpan.FromSeconds(2));
        var button = cut.FindAll("button").Single(element => element.TextContent.Contains("PinTemplateVersion"));
        button.Click();

        cut.WaitForAssertion(() => Assert.Contains("Basic CI@3", ModelYaml(cut)), TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("LegacyTemplateReferenceWarning", cut.Markup);
    }

    [Fact]
    public void EditPipeline_GitManagedSource_OffersAndKeepsTheSelectedBranch()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto
        {
            RepositoryId = 9,
            Path = ".pipeline/deploy-prod.yaml",
            Branch = "release/2026.07",
            CommitHash = "0123456789abcdef0123456789abcdef01234567"
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/9/branches", new List<GitLightBranchDto>
        {
            new() { Name = "main", IsDefault = true },
            new() { Name = "release/2026.07" }
        });

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));

        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("release/2026.07", model.GetType().GetProperty("SourceBranch")!.GetValue(model));
        // The header badge that used to echo the branch was removed (PLAN-003 lot 16); the model
        // assertion above is what proves the selected branch is kept.
    }

    [Fact]
    public void Submit_NewPipeline_NavigatesToCreated()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/pipelines", new PipelineDto { Id = 10, Name = "new" });
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        // Name is [Required]; populate it so the template form validates and OnSubmit actually runs.
        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "new");

        var form = cut.Find("form");
        form.Submit();

        // A successful create POSTs the new pipeline and routes to its detail page.
        cut.WaitForState(() => nav.Uri.Contains("/pipelines/10"), TimeSpan.FromSeconds(2));
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/pipelines"));
        Assert.Contains("/pipelines/10", nav.Uri);
    }

    [Fact]
    public void EditPipeline_WithRuns_ShowsRunHistory()
    {
        _handler.SetJsonResponse("api/pipelines/6", new PipelineDto
        {
            Id = 6,
            Name = "CI Pipeline",
            Description = "Continuous integration",
            YamlDefinition = "name: ci\ntrigger: webhook\nstages: []",
            TriggerType = PipelineTriggerType.Webhook
        });
        _handler.SetJsonResponse("api/pipelines/6/runs", new PaginatedResult<PipelineRunDto>
        {
            Items =
            [
                new() { Id = 1, PipelineId = 6, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddHours(-1), CompletedAt = DateTime.UtcNow },
                new() { Id = 2, PipelineId = 6, Status = PipelineStatus.Failed, StartedAt = DateTime.UtcNow.AddMinutes(-30), CompletedAt = DateTime.UtcNow }
            ],
            TotalCount = 2
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 6));
        cut.WaitForState(() => cut.Markup.Contains("CI Pipeline"), TimeSpan.FromSeconds(2));
        Assert.Contains("CI Pipeline", cut.Markup);
    }

    [Fact]
    public async Task RunsGrid_LoadsRequestedServerPage()
    {
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        _handler.SetJsonResponse("api/pipelines/5/runs?page=2&pageSize=25",
            new PaginatedResult<PipelineRunDto>
            {
                Items = [new PipelineRunDto { Id = 26, PipelineId = 5, Status = PipelineStatus.Success }],
                TotalCount = 26,
                Page = 2,
                PageSize = 25
            });
        var load = typeof(PipelineEdit).GetMethod("OnRunsLoadDataAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)load.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 25, Top = 25 }])!);

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/5/runs?page=2&pageSize=25", StringComparison.Ordinal));
        Assert.Equal(26, (int)typeof(PipelineEdit).GetField("_runsTotalCount",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!);
        Assert.Equal(26, Assert.Single((List<PipelineRunDto>)typeof(PipelineEdit).GetField("_runs",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!).Id);
    }

    [Fact]
    public void PipelineIdChange_ClearsPriorSourceAndTemplateState()
    {
        SetupEditPipelineMocks(5);
        SetupEditPipelineMocks(6);
        _handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto
        {
            RepositoryId = 9,
            Branch = "release",
            Path = ".pipeline/ci.yaml"
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/9/branches",
            new List<GitLightBranchDto> { new() { Name = "release" } });
        _handler.SetResponse("api/pipelines/6/source", System.Net.HttpStatusCode.NoContent);
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => ((List<string>)typeof(PipelineEdit).GetField("_sourceBranches",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!).Contains("release"));
        typeof(PipelineEdit).GetField("_selectedTemplateId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, 42);
        typeof(PipelineEdit).GetField("_baseTemplateYaml", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "old-template");

        cut.Render(p => p.Add(x => x.Id, 6));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("api/pipelines/6/source", StringComparison.Ordinal)));
        Assert.Null(typeof(PipelineEdit).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance));
        Assert.Empty((List<string>)typeof(PipelineEdit).GetField("_sourceBranches",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!);
        Assert.Null(typeof(PipelineEdit).GetField("_selectedTemplateId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance));
        Assert.Null(typeof(PipelineEdit).GetField("_baseTemplateYaml", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance));
    }

    [Fact]
    public async Task PipelineRunStarted_UsesSecondHubArgumentAsPipelineId()
    {
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        var before = _handler.Requests.Count(request => request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal));

        var method = typeof(PipelineEdit).GetMethod("OnPipelineRunStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [999, 5])!);

        cut.WaitForAssertion(() =>
            Assert.True(_handler.Requests.Count(request => request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal)) > before),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void EditPipeline_WithProject_LoadsProjectList()
    {
        _handler.SetJsonResponse("api/pipelines/7", new PipelineDto
        {
            Id = 7,
            Name = "Project Pipeline",
            Description = "Pipeline with project",
            YamlDefinition = "name: project-pipe",
            TriggerType = PipelineTriggerType.Manual,
            ProjectId = 1
        });
        _handler.SetJsonResponse("api/pipelines/7/runs", new PaginatedResult<PipelineRunDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "My Project" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 7));
        cut.WaitForState(() => cut.Markup.Contains("Project Pipeline"), TimeSpan.FromSeconds(2));
        Assert.Contains("Project Pipeline", cut.Markup);
    }

    // --- Method-level tests ---

    [Fact]
    public void OnYamlValueChanged_SetsYamlAndResetsValidation()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        var method = typeof(PipelineEdit).GetMethod("OnYamlValueChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, ["new yaml content"]);

        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var yamlDef = model.GetType().GetProperty("YamlDefinition")!.GetValue(model) as string;
        Assert.Equal("new yaml content", yamlDef);

        var yamlValid = typeof(PipelineEdit).GetField("_yamlValid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(yamlValid);
    }

    [Fact]
    public void OnVisualYamlChanged_UpdatesModel()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        var method = typeof(PipelineEdit).GetMethod("OnVisualYamlChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, ["trigger: webhook\nstages: []"]);

        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var yamlDef = model.GetType().GetProperty("YamlDefinition")!.GetValue(model) as string;
        Assert.Equal("trigger: webhook\nstages: []", yamlDef);
    }


    [Theory]
    [InlineData(0, 0, "0s")]
    [InlineData(0, 45, "45s")]
    public void FormatDuration_FormatsCorrectly(int minutes, int seconds, string expected)
    {
        var started = DateTime.UtcNow;
        var completed = started.AddMinutes(minutes).AddSeconds(seconds);
        var result = PipelineRunPresentation.FormatDuration(started, completed);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatDuration_NullCompleted_ReturnsDash()
    {
        var result = PipelineRunPresentation.FormatDuration(DateTime.UtcNow, null);
        Assert.Equal("\u2014", result);
    }

    [Fact]
    public void FormatDuration_CompletionBeforeStartNeverShowsNegativeTime()
    {
        var started = DateTime.UtcNow;

        var result = PipelineRunPresentation.FormatDuration(started, started.AddSeconds(-5));

        Assert.Equal("0s", result);
    }

    [Fact]
    public void TemplateReference_ParseLegacyQuotedName_ReturnsUnpinnedReference()
    {
        var reference = PipelineTemplateReferenceHelper.Parse("extends: 'Shared CI template' # legacy");

        Assert.NotNull(reference);
        Assert.Equal("Shared CI template", reference.Name);
        Assert.Null(reference.Version);
    }

    [Fact]
    public void TemplateReference_Pin_PreservesCommentAndPinsExactVersion()
    {
        var yaml = "name: demo\nextends: Shared CI template # keep\nstages: []";

        var pinned = PipelineTemplateReferenceHelper.Pin(yaml, "Shared CI template", 7);

        Assert.Contains("extends: 'Shared CI template@7' # keep", pinned, StringComparison.Ordinal);
        Assert.Equal(7, PipelineTemplateReferenceHelper.Parse(pinned)!.Version);
    }

    [Fact]
    public void FormatDuration_MinutesAndSeconds()
    {
        var started = DateTime.UtcNow;
        var completed = started.AddMinutes(3).AddSeconds(15);
        var result = PipelineRunPresentation.FormatDuration(started, completed);
        Assert.Equal("3m 15s", result);
    }

    [Theory]
    [InlineData(PipelineStatus.Success, OmniTone.Success)]
    [InlineData(PipelineStatus.Failed, OmniTone.Danger)]
    [InlineData(PipelineStatus.Running, OmniTone.Accent)]
    // PLAN-003 D13: amber is now Partial (finished with a swallowed failure); a cancelled run is a
    // non-event, so it takes the neutral grey.
    [InlineData(PipelineStatus.Cancelled, OmniTone.Info)]
    [InlineData(PipelineStatus.Partial, OmniTone.Warning)]
    public void GetRunBadge_ReturnsValue(PipelineStatus status, OmniTone expected)
    {
        var result = PipelineRunPresentation.Badge(status);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task OnSubmit_ExistingPipeline_UpdatesAndSaves()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/update", new PipelineDto { Id = 5, Name = "Updated" });

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));

        var method = typeof(PipelineEdit).GetMethod("OnSubmit", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var submit = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(new Aetheus.Front.Components.Pipelines.PipelineSaveDecision("main")));
        await submit;

        var saving = (bool)typeof(PipelineEdit).GetField("_saving", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(saving);
        Assert.Contains(_handler.Requests, request => request.Method == "PUT" && request.Url.Contains("api/pipelines/5"));
    }

    [Fact]
    public void Front009_ALoadedPipeline_IsCleanUntilOnlyItsSourceBranchIsEdited()
    {
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;
        var model = (PipelineModel)typeof(PipelineEdit).GetField("_model", priv)!.GetValue(cut.Instance)!;
        bool Dirty() => PipelineDefinitionLiveSync.HasUnsavedChanges(
            model,
            (PipelineDto?)typeof(PipelineEdit).GetField("_pipeline", priv)!.GetValue(cut.Instance),
            (string?)typeof(PipelineEdit).GetField("_savedSourceBranch", priv)!.GetValue(cut.Instance));

        Assert.False(Dirty());
        model.SourceBranch = "release/2026.10";
        Assert.True(Dirty());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a-name-longer-than-one-hundred-characters-a-name-longer-than-one-hundred-characters-a-name-longer-than")]
    public async Task Front008_MonacoCtrlS_WithAnInvalidName_SendsNoMutation(string invalidName)
    {
        // FRONT-008: Monaco's OnSave called OnSubmit outside the form's validity-gated submit.
        SetupEditPipelineMocks();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/pipelines/5?tab=edit");
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string>());
        _handler.SetJsonResponse("api/vaults/names", new List<string>());
        _handler.SetJsonResponse("api/servers/names", new List<string>());
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 });
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, invalidName);
        var requestsBefore = _handler.Requests.Count;

        var save = cut.InvokeAsync(() => cut.FindComponent<MonacoEditor>().Instance.OnSave.InvokeAsync());
        var stoppedBeforeTheReviewDialog = save.IsCompleted;
        if (!stoppedBeforeTheReviewDialog)
        {
            // Without the guard the save review dialog opens; confirm it so the test ends on the PUT it sends.
            var dialog = Services.GetRequiredService<OmniDialogService>();
            await cut.InvokeAsync(() => dialog.Close(new PipelineSaveDecision("main")));
        }
        await save;

        Assert.True(stoppedBeforeTheReviewDialog);
        Assert.DoesNotContain(_handler.Requests.Skip(requestsBefore), request => request.Method is "PUT" or "POST" && request.Url.Contains("api/pipelines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnRun_TriggersPipelineRun()
    {
        SetupEditPipelineMocks();
        // Preflight resolves cleanly (empty stage list) and the pipeline declares no queue-time
        // parameters, so OnRun proceeds all the way to actually triggering the run.
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));

        var method = typeof(PipelineEdit).GetMethod("OnRun", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // OnRun runs the preflight gate, then POSTs the run trigger, then clears the _running guard.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/pipelines/5/preflight"));
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/pipelines/5/run"));
        Assert.False((bool)typeof(PipelineEdit).GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task R2_038_OnRun_StaysOnThePipeline_AndReloadsTheRunsGrid()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var uriBefore = navigation.Uri;
        var runLoadsBefore = _handler.Requests.Count(request => request.Method == "GET"
            && request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal));

        var method = typeof(PipelineEdit).GetMethod(
            "OnRun", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        // The launched run is read back into this page's grid; the page is not left for the run's.
        Assert.Equal(uriBefore, navigation.Uri);
        Assert.True(_handler.Requests.Count(request => request.Method == "GET"
            && request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal)) > runLoadsBefore);
        Assert.Contains(Services.Toasts(),
            notification => notification.Severity == OmniSeverity.Info);
    }

    [Fact]
    public void R2_038_WhileLaunching_TheRunButtonIsBusy_AndNoPageLoaderShows()
    {
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));
        var loadersBefore = cut.FindAll("[role=status]").Count;

        typeof(PipelineEdit).GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, true);
        cut.Render();

        Assert.NotEmpty(cut.FindAll("[aria-busy=true]"));
        Assert.Equal(loadersBefore, cut.FindAll("[role=status]").Count);
    }

    [Fact]
    public async Task OnValidateYaml_EmptyYaml_LeavesTheFlagOff()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        // Set empty yaml
        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("YamlDefinition")!.SetValue(model, "");

        var method = typeof(PipelineEdit).GetMethod("OnValidateYaml", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var validating = (bool)typeof(PipelineEdit).GetField("_validating", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(validating);
    }

    [Fact]
    public async Task OnDelete_NavigatesToPipelines()
    {
        SetupEditPipelineMocks();
        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/5", System.Net.HttpStatusCode.OK);
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"), TimeSpan.FromSeconds(2));

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var dialog = Services.GetRequiredService<OmniDialogService>();

        // OnDelete awaits a confirmation dialog that never resolves on its own: start it un-awaited,
        // confirm it (Close(true)), then await. The confirmed path deletes the pipeline and routes back.
        var method = typeof(PipelineEdit).GetMethod("OnDelete", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(true));
        await task;

        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.EndsWith("api/pipelines/5"));
        Assert.EndsWith("/pipelines", nav.Uri);
    }

    [Fact]
    public void NewPipeline_WithTemplates_ShowsTemplateSelector()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 1, Name = "Basic CI" },
            new() { Id = 2, Name = "Docker Deploy" }
        });
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string>());
        _handler.SetJsonResponse("api/vaults/names", new List<string>());
        _handler.SetJsonResponse("api/servers/names", new List<string>());
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 });

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        var templatesField = typeof(PipelineEdit).GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        cut.WaitForState(() => ((List<PipelineTemplateSummaryDto>)templatesField.GetValue(cut.Instance)!).Count == 2, TimeSpan.FromSeconds(2));

        // The new-pipeline form loads the available templates for its template-picker dropdown.
        var templates = (List<PipelineTemplateSummaryDto>)templatesField.GetValue(cut.Instance)!;
        Assert.Equal(2, templates.Count);
        Assert.Contains(templates, t => t.Name == "Basic CI");
    }

    [Fact]
    public void OnYamlValueChanged_ClearsValidation()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        typeof(PipelineEdit).GetField("_yamlValid", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, true);

        var method = typeof(PipelineEdit).GetMethod("OnYamlValueChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, ["new yaml content"]);

        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var yamlDef = (string)model.GetType().GetProperty("YamlDefinition")!.GetValue(model)!;
        Assert.Equal("new yaml content", yamlDef);

        var yamlValid = typeof(PipelineEdit).GetField("_yamlValid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(yamlValid);
    }

    [Fact]
    public async Task OnDebouncedYamlChanged_CallsValidation()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("YamlDefinition")!.SetValue(model, "name: test\ntrigger: manual");

        var method = typeof(PipelineEdit).GetMethod("OnDebouncedYamlChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["name: test"])!);

        var validating = (bool)typeof(PipelineEdit).GetField("_validating", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(validating); // Should be false after completion
    }

    [Fact]
    public async Task OnFormatYaml_WithNoEditor_LeavesTheStateUnchanged()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        var modelField = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var model = modelField.GetValue(cut.Instance)!;
        var yamlProp = model.GetType().GetProperty("YamlDefinition")!;
        var before = (string)yamlProp.GetValue(model)!;

        var method = typeof(PipelineEdit).GetMethod("OnFormatYaml", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // OnFormatYaml only delegates formatting to the Monaco editor; it must not mutate the YAML model.
        Assert.Equal(before, (string)yamlProp.GetValue(model)!);
    }



    [Fact]
    public async Task OnValidateYaml_WithValidYaml_SetsYamlValidTrue()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        var model = typeof(PipelineEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("YamlDefinition")!.SetValue(model, "name: test\ntrigger: manual\nstages: []");

        var method = typeof(PipelineEdit).GetMethod("OnValidateYaml", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var yamlValid = (bool?)typeof(PipelineEdit).GetField("_yamlValid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.True(yamlValid);
    }

    [Fact]
    public async Task LoadAutocompleteSuggestionsAsync_PopulatesNames()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string> { "lib1", "lib2" });
        _handler.SetJsonResponse("api/vaults/names", new List<string> { "vault1" });
        _handler.SetJsonResponse("api/servers/names", new List<string> { "server1", "server2" });

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        var method = typeof(PipelineEdit).GetMethod("LoadAutocompleteSuggestionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [null])!);

        var libraryNames = (List<string>)typeof(PipelineEdit).GetField("_libraryNames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var vaultNames = (List<string>)typeof(PipelineEdit).GetField("_vaultNames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var serverNames = (List<string>)typeof(PipelineEdit).GetField("_serverNames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(2, libraryNames.Count);
        Assert.Single(vaultNames);
        Assert.Equal(2, serverNames.Count);
    }
}
