// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.Services;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// The pipeline page driven through its markup, the public parameters and callbacks of its child
/// components (the Monaco editor, the runs grid, the visual editor, the dropdowns), the navigation
/// manager and the requests the faked API receives. The hub events the page listens to are delivered
/// through a captured connection. No private member of the page is read or written (RFL-1), except for
/// <see cref="PipelineRunStarted_UsesSecondHubArgumentAsPipelineId"/> (an unused handler, see there).
/// </summary>
public class PipelineEditTests : BunitContext
{
    public PipelineEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<YamlSerializationService>();
        // The settings tab's AI profile selector reads the profiles the user can see.
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>());
    }

    private readonly BunitTestHelper.TestHandler _handler;

    private const string RunButton = ".omni-page-header__pinned button.omni-button";

    private void OpenTab(string tab, int id = 5) =>
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/pipelines/{id}?tab={tab}");

    private IRenderedComponent<PipelineEdit> RenderPipeline(int? id)
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, id));
        return cut;
    }

    private static MonacoEditor Editor(IRenderedComponent<PipelineEdit> cut) => cut.FindComponent<MonacoEditor>().Instance;

    private static Task ClickAsync(AngleSharp.Dom.IElement element) => element.ClickAsync(new MouseEventArgs());

    private static AngleSharp.Dom.IElement ButtonLabelled(IRenderedComponent<PipelineEdit> cut, string label) =>
        cut.FindAll("button").First(button => button.TextContent.Trim() == label);

    // STD-BTN detection 11: Validate and Format sit in the editor toolbar's "More actions" menu.
    private static AngleSharp.Dom.IElement MenuItemLabelled(IRenderedComponent<PipelineEdit> cut, string label) =>
        cut.FindMenuItem(label) ?? throw new InvalidOperationException($"No menu entry {label}.");

    private static bool ShowsBadge(IRenderedComponent<PipelineEdit> cut, string text) =>
        cut.FindAll(".omni-badge").Any(badge => badge.TextContent.Trim() == text);

    private static bool IsBusy(AngleSharp.Dom.IElement button) =>
        button.ClassList.Contains("btn-busy") || button.GetAttribute("aria-busy") == "true";

    /// <summary>The connections the page opened, by hub path, none of which ever delivers an event on
    /// its own: a test delivers one with <see cref="HubHandlerInvoker"/>.</summary>
    private Dictionary<string, SilentHubConnection> CaptureHubs()
    {
        var hubs = new Dictionary<string, SilentHubConnection>(StringComparer.Ordinal);
        Services.AddSingleton<HubConnectionFactory>(services => new CapturingHubs(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<AuthStateProvider>(),
            hubs));
        return hubs;
    }

    private sealed class CapturingHubs(IConfiguration config, AuthStateProvider auth, Dictionary<string, SilentHubConnection> hubs)
        : HubConnectionFactory(config, auth, NullLogger<AuthDelegatingHandler>.Instance)
    {
        public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null) =>
            hubs[hubPath] = new SilentHubConnection(refuseJoin: false);
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
        cut.WaitForState(() => cut.Markup.Length > 100);

        // New-pipeline mode renders the create title and never fetches an existing pipeline detail.
        Assert.Contains("NewPipeline", cut.Markup);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/pipelines/") && !r.Url.Contains("templates"));
    }

    [Fact]
    public void EditPipeline_LoadsExistingData()
    {
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        Assert.Contains("Deploy Prod", cut.Markup);
    }

    [Fact]
    public void Header_ShowsRunAlone_AndEditIsTheFirstEntryOfTheMoreActionsMenu()
    {
        // Run is directly visible (pinned, no chevron: R-40) and Edit belongs to the header menu.
        SetupNewPipelineMocks(); // the edit tab the menu entry opens loads the editor's name lists
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        var trigger = cut.WaitForElement(".omni-page-header__menu .omni-overflow-menu__trigger");

        var headerButtons = cut.FindAll(".omni-page-header__pinned > .omni-button");
        Assert.DoesNotContain(headerButtons, button => button.TextContent.Contains("Edit", StringComparison.Ordinal));
        Assert.Single(headerButtons);
        Assert.Empty(cut.FindAll(".omni-page-header .omni-split-button"));
        Assert.Empty(cut.FindComponents<MonacoEditor>());

        trigger.Click();
        var items = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item");
        Assert.Contains("Edit", items[0].TextContent, StringComparison.Ordinal);
        // The variant of Run that the split button carried is an entry of the menu.
        Assert.Contains(items, item => item.TextContent.Contains("RunWithOptions", StringComparison.Ordinal));

        items[0].Click();
        // The Edit tab is now the one shown: its editor is on the page.
        Assert.NotEmpty(cut.FindComponents<MonacoEditor>());
        Assert.Empty(cut.FindAll(".omni-overflow-menu__popup"));
    }

    [Fact]
    public void RunsTab_EachSummaryTileHasItsLabelLineThenItsValueLine()
    {
        // Each summary tile separates its label and value.
        SetupEditPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.FindAll(".pipeline-runs-summary-card").Count == 4);

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
        // "latest grade" opens the graded run on its Gate tab (its findings), "last success" opens
        // the successful run; the two other tiles stay plain.
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
        // The tile is itself the link (OmniCard.Href), a direct child of the OE tile grid: no wrapper.
        cut.WaitForState(() => cut.FindAll("a.pipeline-runs-summary-card").Count == 2);

        var links = cut.FindAll("a.pipeline-runs-summary-card").Select(link => link.GetAttribute("href")).ToList();
        Assert.Equal(["/pipelines/runs/31?tab=gate", "/pipelines/runs/21"], links);
        Assert.All(cut.FindAll("a.pipeline-runs-summary-card"), link => Assert.Contains("omni-card--action", link.ClassName, StringComparison.Ordinal));
        Assert.Equal(4, cut.FindAll(".omni-tile-grid.pipeline-runs-summary > .pipeline-runs-summary-card").Count);
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
        var overflowTrigger = cut.WaitForElement(".omni-page-header__menu .omni-overflow-menu__trigger");

        // The secondary header actions now live in the overflow menu, which renders its items only
        // while it is open - so the action must be absent before the trigger is clicked.
        Assert.DoesNotContain("UpdatePipelineTemplate", cut.Markup, StringComparison.Ordinal);
        overflowTrigger.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(
                "UpdatePipelineTemplate",
                cut.Find(".omni-overflow-menu__popup").InnerHtml,
                StringComparison.Ordinal));
    }

    [Fact]
    public void EditPipeline_FleetUpdateAction_OpensTheUpdateOfTheLatestVersion()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/fleet-item", new PipelineFleetItemDto
        {
            PipelineId = 5,
            PipelineName = "Deploy Prod",
            TemplateName = "ci",
            PinnedVersion = 1,
            LatestVersion = 4,
            Freshness = PipelineFleetFreshness.Outdated
        });
        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForElement(".omni-overflow-menu__trigger");
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.FindMenuItem("UpdatePipelineTemplate")));
        cut.FindMenuItem("UpdatePipelineTemplate")!.Click();

        Assert.EndsWith("/pipelines/5/template/update/4", navigation.Uri, StringComparison.Ordinal);
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
        // A missing required environment value suppresses resolution while the unexpanded template
        // remains available.
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
    public void R184_ParameterValues_NeverSendsAParameterTheTemplateDoesNotDeclare()
    {
        const string template = """
            name: host-bluegreen-deploy
            parameters:
              - name: revision
                default: main
            stages: []
            """;
        // The pipeline still carries keys of a previous template: the back answers 400 for unknown ones.
        const string pipeline = """
            name: aetheus-deploy-prod
            extends: host-bluegreen-deploy@1
            parameters:
              - name: revision
                default: candidate
              - name: allowBootstrap
                default: "true"
              - name: cacheScenario
                default: warm
            stages: []
            """;

        var values = PipelineTemplateEditorCoordinator.ParameterValues(template, pipeline);

        Assert.NotNull(values);
        Assert.Equal(["revision"], values.Keys);
        Assert.Equal("candidate", values["revision"]);
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
        SetupNewPipelineMocks(); // the edit tab loads the editor name lists
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
        OpenTab("edit");

        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("LegacyTemplateReferenceWarning"));
        Assert.Contains("'Basic CI'", Editor(cut).Value);
        var button = cut.FindAll("button").Single(element => element.TextContent.Contains("PinTemplateVersion"));
        button.Click();

        cut.WaitForAssertion(() => Assert.Contains("Basic CI@3", Editor(cut).Value));
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
        OpenTab("settings");

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForElement("#oe-formfield-pages-pipelines-pipelineedit-6");

        // The settings tab offers the repository's branches and keeps the pipeline's own branch selected.
        var branches = cut.FindComponents<OmniDropDown<string>>().Single(drop => drop.Instance.Id == "oe-formfield-pages-pipelines-pipelineedit-6");
        Assert.Equal("release/2026.07", branches.Instance.Value);
        Assert.Equal(["main", "release/2026.07"], branches.Instance.Options.Select(option => option.Value).ToArray());
    }

    [Fact]
    public async Task Submit_NewPipeline_NavigatesToCreated()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/pipelines", new PipelineDto { Id = 10, Name = "new" });
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        // Name is [Required]; type it so the template form validates and OnSubmit actually runs.
        cut.Find("input#Name").Input("new");

        var form = cut.Find("form");
        await form.SubmitAsync();

        // A successful create POSTs the new pipeline (with the typed name) and routes to its detail page.
        cut.WaitForState(() => nav.Uri.Contains("/pipelines/10"));
        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.EndsWith("api/pipelines"));
        Assert.Contains("\"new\"", post.Body);
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
        cut.WaitForState(() => cut.Markup.Contains("CI Pipeline"));
        Assert.Contains("CI Pipeline", cut.Markup);
    }

    [Fact]
    public async Task RunsGrid_LoadsRequestedServerPage()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/runs", new PaginatedResult<PipelineRunDto>
        {
            Items = [new PipelineRunDto { Id = 1, PipelineId = 5, Status = PipelineStatus.Success }],
            TotalCount = 26
        });
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        _handler.SetJsonResponse("api/pipelines/5/runs?page=2&pageSize=25",
            new PaginatedResult<PipelineRunDto>
            {
                Items = [new PipelineRunDto { Id = 26, PipelineId = 5, Status = PipelineStatus.Success }],
                TotalCount = 26,
                Page = 2,
                PageSize = 25
            });
        var grid = cut.FindComponent<Aetheus.Front.Components.Shared.AetheusDataGrid<PipelineRunDto>>();

        // The grid asks for its second block of 25 rows, the way scrolling or paging does.
        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs { Skip = 25, Top = 25 }));

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/5/runs?page=2&pageSize=25", StringComparison.Ordinal));
        Assert.Equal(26, grid.Instance.Count);
        Assert.Equal(26, Assert.Single(grid.Instance.Data!).Id);
    }

    [Fact]
    public void PipelineIdChange_ClearsPriorSourceAndBranches()
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
        OpenTab("settings");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForElement("#oe-formfield-pages-pipelines-pipelineedit-6");
        Assert.Equal("release", cut.FindComponents<OmniDropDown<string>>()
            .Single(drop => drop.Instance.Id == "oe-formfield-pages-pipelines-pipelineedit-6").Instance.Value);

        cut.Render(p => p.Add(x => x.Id, 6));

        // Pipeline 6 has no Git source: the previous pipeline's branches must not stay on offer.
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("api/pipelines/6/source", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#oe-formfield-pages-pipelines-pipelineedit-6")));
        Assert.DoesNotContain("release", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineIdChange_ClearsPriorBaseTemplate()
    {
        SetupEditPipelineMocks(5);
        SetupEditPipelineMocks(6);
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "Deploy Prod",
            YamlDefinition = "name: deploy\nextends: 'Basic CI@3'\nstages: []",
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
        _handler.SetJsonResponse("api/pipelines/templates/1/resolve?version=3", "name: old-template\nstages: []");
        OpenTab("visual");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        var visual = cut.WaitForComponent<VisualPipelineEditor>(TimeSpan.FromSeconds(2));
        cut.WaitForAssertion(() => Assert.Contains("old-template", visual.Instance.BaseTemplateYaml));

        cut.Render(p => p.Add(x => x.Id, 6));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("api/pipelines/6/runs", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Null(cut.FindComponent<VisualPipelineEditor>().Instance.BaseTemplateYaml));
    }

    [Fact]
    public async Task PipelineIdChange_ClearsPriorTemplateSelection()
    {
        SetupNewPipelineMocks();
        SetupEditPipelineMocks(5);
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 42, Name = "Basic CI", Version = 1 }
        });
        _handler.SetJsonResponse("api/pipelines/templates/42", new PipelineTemplateDto
        {
            Id = 42,
            Name = "Basic CI",
            Version = 1,
            YamlContent = "name: basic-ci\nstages: []"
        });
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        var picker = cut.WaitForComponent<OmniDropDown<int?>>(TimeSpan.FromSeconds(2));
        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(42));
        cut.WaitForAssertion(() => Assert.Contains("Basic CI", Editor(cut).Value));
        Assert.Equal(42, picker.Instance.Value);

        cut.Render(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        cut.Render(p => p.Add(x => x.Id, null));

        // Back on a new pipeline: no template is selected and the editor holds the starter definition.
        cut.WaitForAssertion(() => Assert.Null(cut.FindComponent<OmniDropDown<int?>>().Instance.Value));
        Assert.DoesNotContain("Basic CI", Editor(cut).Value);
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
        cut.WaitForState(() => cut.Markup.Contains("Project Pipeline"));
        Assert.Contains("Project Pipeline", cut.Markup);
    }

    // --- Editing the definition ---

    [Fact]
    public async Task OnYamlValueChanged_SetsYamlAndResetsValidation()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        await cut.InvokeAsync(() => Editor(cut).ValueChanged.InvokeAsync("new yaml content"));

        // The editor is given the new definition back, and no validity verdict is shown for it.
        Assert.Equal("new yaml content", Editor(cut).Value);
        Assert.False(ShowsBadge(cut, "Valid") || ShowsBadge(cut, "Invalid"));
    }

    [Fact]
    public async Task OnVisualYamlChanged_UpdatesModel()
    {
        SetupEditPipelineMocks();
        SetupNewPipelineMocks();
        OpenTab("visual");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        var visual = cut.WaitForComponent<VisualPipelineEditor>(TimeSpan.FromSeconds(2));

        await cut.InvokeAsync(() => visual.Instance.YamlDefinitionChanged.InvokeAsync("trigger: webhook\nstages: []"));

        // The change reaches the page's definition: the visual editor gets it back, and so does the YAML tab.
        Assert.Equal("trigger: webhook\nstages: []", cut.FindComponent<VisualPipelineEditor>().Instance.YamlDefinition);
        OpenTab("edit");
        cut.WaitForAssertion(() => Assert.Equal("trigger: webhook\nstages: []", Editor(cut).Value));
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
        Assert.Equal("3min", result);
    }

    [Theory]
    [InlineData(PipelineStatus.Success, OmniTone.Success)]
    [InlineData(PipelineStatus.Failed, OmniTone.Danger)]
    [InlineData(PipelineStatus.Running, OmniTone.Accent)]
    // Partial runs use amber and cancelled runs use neutral grey.
    [InlineData(PipelineStatus.Cancelled, OmniTone.Warning)]
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
        OpenTab("settings");

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));

        var dialog = Services.GetRequiredService<OmniDialogService>();
        var saveButton = ButtonLabelled(cut, "SaveSettings");
        var submit = ClickAsync(saveButton);
        await cut.InvokeAsync(() => dialog.Close(new Aetheus.Front.Components.Pipelines.PipelineSaveDecision("main")));
        await submit;

        Assert.False(IsBusy(ButtonLabelled(cut, "SaveSettings")));
        Assert.Contains(_handler.Requests, request => request.Method == "PUT" && request.Url.Contains("api/pipelines/5"));
    }

    // PLAN-002 B7: the confirmation dialog is the save handler's first await, so the render it triggers
    // comes before the save starts; the button must still read busy while the server has not answered.
    [Fact]
    public async Task OnSubmit_ExistingPipeline_TheButtonIsBusyWhileTheServerHasNotAnswered()
    {
        SetupEditPipelineMocks();
        OpenTab("settings");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        var putReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveResponse = new TaskCompletionSource<PipelineDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Put, "api/pipelines/5", _ =>
        {
            putReceived.TrySetResult();
            return saveResponse.Task;
        });

        var submit = ClickAsync(ButtonLabelled(cut, "SaveSettings"));
        await cut.InvokeAsync(() => Services.GetRequiredService<OmniDialogService>().Close(new PipelineSaveDecision("main")));
        await putReceived.Task.WaitAsync(BunitTestHelper.AsyncRenderWait, Xunit.TestContext.Current.CancellationToken);

        // No render is forced here: the markup is the page's own last render, the one the user sees.
        Assert.True(IsBusy(ButtonLabelled(cut, "SaveSettings")));
        saveResponse.SetResult(new PipelineDto { Id = 5, Name = "Deploy Prod" });
        await submit;
        cut.WaitForAssertion(() => Assert.False(IsBusy(ButtonLabelled(cut, "SaveSettings"))));
    }

    [Fact]
    public async Task Front009_ALoadedPipeline_IsCleanUntilOnlyItsSourceBranchIsEdited()
    {
        // A definition change announced by the hub reloads a page that holds no edit, and shows the
        // "changed elsewhere" banner once the only edit is the source branch.
        SetupNewPipelineMocks();
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto
        {
            RepositoryId = 9,
            Path = ".pipeline/deploy-prod.yaml",
            Branch = "release/2026.07"
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/9/branches", new List<GitLightBranchDto>
        {
            new() { Name = "main", IsDefault = true },
            new() { Name = "release/2026.07" }
        });
        var hubs = CaptureHubs();
        OpenTab("settings");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForElement("#oe-formfield-pages-pipelines-pipelineedit-6");
        cut.WaitForAssertion(() => Assert.True(hubs.ContainsKey("entities")));
        int PipelineReads() => _handler.Requests.Count(request => request.Method == "GET"
            && request.Url.EndsWith("api/pipelines/5", StringComparison.Ordinal));
        var readsBefore = PipelineReads();

        await cut.InvokeAsync(() => HubHandlerInvoker.InvokeAsync(hubs["entities"], "EntityChanged", ResourceType.Pipeline, 5, "Updated"));

        Assert.True(PipelineReads() > readsBefore);
        Assert.Empty(cut.FindAll(".pipeline-changed-elsewhere"));

        var branches = cut.FindComponents<OmniDropDown<string>>().Single(drop => drop.Instance.Id == "oe-formfield-pages-pipelines-pipelineedit-6");
        await cut.InvokeAsync(() => branches.Instance.ValueChanged.InvokeAsync("main"));
        await cut.InvokeAsync(() => HubHandlerInvoker.InvokeAsync(hubs["entities"], "EntityChanged", ResourceType.Pipeline, 5, "Updated"));

        Assert.NotEmpty(cut.FindAll(".pipeline-changed-elsewhere"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a-name-longer-than-one-hundred-characters-a-name-longer-than-one-hundred-characters-a-name-longer-than")]
    public async Task Front008_MonacoCtrlS_WithAnInvalidName_SendsNoMutation(string invalidName)
    {
        // FRONT-008: Monaco's OnSave called OnSubmit outside the form's validity-gated submit.
        SetupEditPipelineMocks();
        OpenTab("edit");
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string>());
        _handler.SetJsonResponse("api/vaults/names", new List<string>());
        _handler.SetJsonResponse("api/servers/names", new List<string>());
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 });
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        cut.Find("input#Name").Input(invalidName);
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
        // parameters, so Run proceeds all the way to actually triggering the run.
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));

        await ClickAsync(cut.FindAll(RunButton)[0]);

        // Run goes through the preflight gate, then POSTs the run trigger, then leaves the button free.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/pipelines/5/preflight"));
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/pipelines/5/run"));
        Assert.Empty(cut.FindAll("[aria-busy=true]"));
    }

    [Fact]
    public async Task R2_038_OnRun_StaysOnThePipeline_AndReloadsTheRunsGrid()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var uriBefore = navigation.Uri;
        var runLoadsBefore = _handler.Requests.Count(request => request.Method == "GET"
            && request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal));

        await ClickAsync(cut.FindAll(RunButton)[0]);

        // The launched run is read back into this page's grid; the page is not left for the run's.
        Assert.Equal(uriBefore, navigation.Uri);
        Assert.True(_handler.Requests.Count(request => request.Method == "GET"
            && request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal)) > runLoadsBefore);
        Assert.Contains(Services.Toasts(),
            notification => notification.Severity == OmniSeverity.Info);
    }

    [Fact]
    public async Task R2_038_WhileLaunching_TheRunButtonIsBusy_AndNoPageLoaderShows()
    {
        SetupEditPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        var launched = new TaskCompletionSource<PipelineRunDto>();
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/pipelines/5/run", _ => launched.Task);
        var cut = Render<PipelineEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        var loadersBefore = cut.FindAll("[role=status]").Count;

        // The launch is held back: the Run button is busy meanwhile, and no page loader replaces the page.
        var launch = ClickAsync(cut.FindAll(RunButton)[0]);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[aria-busy=true]")));
        Assert.Equal(loadersBefore, cut.FindAll("[role=status]").Count);

        launched.SetResult(new PipelineRunDto { Id = 1, PipelineId = 5 });
        await launch;
        Assert.Empty(cut.FindAll("[aria-busy=true]"));
    }

    [Fact]
    public async Task OnValidateYaml_EmptyYaml_LeavesTheFlagOff()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        await cut.InvokeAsync(() => Editor(cut).ValueChanged.InvokeAsync(""));
        var requestsBefore = _handler.Requests.Count;

        await ClickAsync(MenuItemLabelled(cut, "ValidateYaml"));

        // An empty definition is not sent for validation, shows no verdict and leaves the button free.
        Assert.DoesNotContain(_handler.Requests.Skip(requestsBefore), request => request.Url.Contains("validate", StringComparison.Ordinal));
        Assert.False(ShowsBadge(cut, "Valid") || ShowsBadge(cut, "Invalid"));
        Assert.False(MenuItemLabelled(cut, "ValidateYaml").HasAttribute("disabled"));
    }

    [Fact]
    public async Task OnDelete_NavigatesToPipelines()
    {
        SetupEditPipelineMocks();
        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/5", System.Net.HttpStatusCode.OK);
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var dialog = Services.GetRequiredService<OmniDialogService>();

        // Delete awaits a confirmation dialog that never resolves on its own: start it un-awaited,
        // confirm it (Close(true)), then await. The confirmed path deletes the pipeline and routes back.
        await ClickAsync(cut.WaitForElement(".omni-page-header__menu .omni-overflow-menu__trigger"));
        var delete = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item")
            .Single(item => item.TextContent.Trim() == "Delete");
        var task = ClickAsync(delete);
        await cut.InvokeAsync(() => dialog.Close(true));
        await task;

        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.EndsWith("api/pipelines/5"));
        Assert.EndsWith("/pipelines", nav.Uri);
    }

    /// <summary>R-34: after the confirmed deletion the page goes back to the list with a success toast,
    /// and a change event raised for the deleted pipeline is not answered by reloading it.</summary>
    [Fact]
    public async Task R34_OnDelete_GoesBackToTheListWithASuccessToast_AndNeverReloadsTheDeletedPipeline()
    {
        SetupEditPipelineMocks();
        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/5", System.Net.HttpStatusCode.OK);
        var hubs = CaptureHubs();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        cut.WaitForAssertion(() => Assert.True(hubs.ContainsKey("entities")));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var dialog = Services.GetRequiredService<OmniDialogService>();

        await ClickAsync(cut.WaitForElement(".omni-page-header__menu .omni-overflow-menu__trigger"));
        var task = ClickAsync(cut.FindAll(".omni-overflow-menu__popup .omni-menu__item").Single(item => item.TextContent.Trim() == "Delete"));
        await cut.InvokeAsync(() => dialog.Close(true));
        await task;

        Assert.EndsWith("/pipelines", nav.Uri);
        Assert.Contains(Services.Toasts(), notification => notification.Severity == OmniSeverity.Success);
        // The pipeline is gone: the hub's update event for it finds nothing to reload.
        _handler.SetResponse("api/pipelines/5", System.Net.HttpStatusCode.NotFound);
        var readsBefore = _handler.Requests.Count(request => request.Method == "GET" && request.Url.Contains("api/pipelines/5", StringComparison.Ordinal));
        await cut.InvokeAsync(() => HubHandlerInvoker.InvokeAsync(hubs["entities"], "EntityChanged", ResourceType.Pipeline, 5, "Updated"));
        Assert.Equal(readsBefore, _handler.Requests.Count(request => request.Method == "GET" && request.Url.Contains("api/pipelines/5", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='pipeline-not-found']"));
    }

    /// <summary>R-34: a failed deletion keeps the page, says so, and keeps the live updates running.</summary>
    [Fact]
    public async Task R34_OnDelete_WhenTheServerRefuses_StaysOnThePageWithAnErrorToast()
    {
        SetupEditPipelineMocks();
        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/5", System.Net.HttpStatusCode.Conflict);
        var hubs = CaptureHubs();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        cut.WaitForAssertion(() => Assert.True(hubs.ContainsKey("entities")));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var uriBefore = nav.Uri;
        var dialog = Services.GetRequiredService<OmniDialogService>();

        await ClickAsync(cut.WaitForElement(".omni-page-header__menu .omni-overflow-menu__trigger"));
        var task = ClickAsync(cut.FindAll(".omni-overflow-menu__popup .omni-menu__item").Single(item => item.TextContent.Trim() == "Delete"));
        await cut.InvokeAsync(() => dialog.Close(true));
        await task;

        Assert.Equal(uriBefore, nav.Uri);
        Assert.Contains(Services.Toasts(), notification => notification.Severity == OmniSeverity.Danger);
        Assert.Contains("Deploy Prod", cut.Markup, StringComparison.Ordinal);
        // The connections were restarted: the page keeps following the pipeline.
        Assert.True(hubs["entities"].Starts >= 1);
    }

    /// <summary>R-34: the page of a pipeline that does not exist says so and leads back to the list,
    /// instead of "an error occurred".</summary>
    [Fact]
    public void R34_AnUnknownPipeline_ShowsNotFoundWithALinkToTheList()
    {
        _handler.SetResponse("api/pipelines/43", System.Net.HttpStatusCode.NotFound);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 43));

        cut.WaitForElement("[data-testid='pipeline-not-found']");
        Assert.Contains("PipelineNotFound", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("/pipelines", cut.Find("[data-testid='pipeline-not-found-back']").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".omni-page-header__menu"));
    }

    /// <summary>R-34: a pipeline deleted from another session while its page is open ends on the same state.</summary>
    [Fact]
    public async Task R34_APipelineDeletedElsewhere_ShowsNotFoundOnTheNextUpdate()
    {
        SetupEditPipelineMocks();
        var hubs = CaptureHubs();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        cut.WaitForAssertion(() => Assert.True(hubs.ContainsKey("entities")));
        _handler.SetResponse("api/pipelines/5", System.Net.HttpStatusCode.NotFound);

        await cut.InvokeAsync(() => HubHandlerInvoker.InvokeAsync(hubs["entities"], "EntityChanged", ResourceType.Pipeline, 5, "Updated"));

        cut.WaitForElement("[data-testid='pipeline-not-found']");
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
        var picker = cut.WaitForComponent<OmniDropDown<int?>>(TimeSpan.FromSeconds(2));

        // The new-pipeline form loads the available templates for its template-picker dropdown.
        Assert.Equal(["Basic CI", "Docker Deploy"], picker.Instance.Options.Select(option => option.Text).ToArray());
    }

    [Fact]
    public async Task OnYamlValueChanged_ClearsValidation()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        await ClickAsync(MenuItemLabelled(cut, "ValidateYaml"));
        Assert.True(ShowsBadge(cut, "Valid"));

        await cut.InvokeAsync(() => Editor(cut).ValueChanged.InvokeAsync("new yaml content"));

        Assert.Equal("new yaml content", Editor(cut).Value);
        Assert.False(ShowsBadge(cut, "Valid") || ShowsBadge(cut, "Invalid"));
    }

    [Fact]
    public async Task OnDebouncedYamlChanged_CallsValidation()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));

        await cut.InvokeAsync(() => Editor(cut).DebouncedValueChanged.InvokeAsync("name: test"));

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith("api/pipelines/validate", StringComparison.Ordinal));
        Assert.True(ShowsBadge(cut, "Valid"));
        Assert.False(MenuItemLabelled(cut, "ValidateYaml").HasAttribute("disabled")); // free again after completion
    }

    [Fact]
    public async Task OnFormatYaml_WithNoEditor_LeavesTheStateUnchanged()
    {
        SetupNewPipelineMocks();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        var before = Editor(cut).Value;

        await ClickAsync(MenuItemLabelled(cut, "FormatYaml"));

        // Format only delegates formatting to the Monaco editor; it must not mutate the YAML model.
        Assert.Equal(before, Editor(cut).Value);
    }

    [Fact]
    public async Task OnValidateYaml_WithValidYaml_SetsYamlValidTrue()
    {
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        await cut.InvokeAsync(() => Editor(cut).ValueChanged.InvokeAsync("name: test\ntrigger: manual\nstages: []"));

        await ClickAsync(MenuItemLabelled(cut, "ValidateYaml"));

        Assert.True(ShowsBadge(cut, "Valid"));
        Assert.False(ShowsBadge(cut, "Invalid"));
    }

    [Fact]
    public async Task LoadAutocompleteSuggestionsAsync_PopulatesNames()
    {
        SetupEditPipelineMocks();
        SetupNewPipelineMocks();
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string> { "lib1", "lib2" });
        _handler.SetJsonResponse("api/vaults/names", new List<string> { "vault1" });
        _handler.SetJsonResponse("api/servers/names", new List<string> { "server1", "server2" });
        OpenTab("edit");

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("api/servers/names", StringComparison.Ordinal)));
        await Task.Yield();

        // The names the editor loaded are the ones the visual editor offers.
        OpenTab("visual");
        var visual = cut.WaitForComponent<VisualPipelineEditor>(TimeSpan.FromSeconds(2));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["lib1", "lib2"], visual.Instance.AvailableLibraries);
            Assert.Equal(["vault1"], visual.Instance.AvailableVaults);
            Assert.Equal(["server1", "server2"], visual.Instance.AvailableServers);
        });
    }

    // SITE-FUNC-AUTOCOMPLETE-001: the suggestion lookups run from OnAfterRenderAsync; a network
    // failure there replaced the whole form with the error boundary instead of only losing hints.
    [Fact]
    public void SuggestionNetworkFailure_KeepsTheForm()
    {
        SetupNewPipelineMocks();
        _handler.SetAsyncJsonResponse<List<string>>(HttpMethod.Get, "api/vaults/names",
            _ => throw new HttpRequestException("network error"));

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, null));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("api/vaults/names", StringComparison.Ordinal)));
        cut.Render();

        Assert.NotEmpty(cut.FindComponents<MonacoEditor>());
    }


    // SITE-FUNC-SELF-SAVE-001: the hub announces the page's own save, often while the save is still in
    // flight; the stored pipeline equal to what this page sent is not a change made elsewhere.
    [Theory]
    [InlineData("edited here", false)]
    [InlineData("edited by someone else", true)]
    public async Task DefinitionChangedDuringTheSave_ShowsTheBannerOnlyForARemoteChange(string storedDescription, bool expectBanner)
    {
        SetupEditPipelineMocks();
        var hubs = CaptureHubs();
        OpenTab("edit");
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string>());
        _handler.SetJsonResponse("api/vaults/names", new List<string>());
        _handler.SetJsonResponse("api/servers/names", new List<string>());
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 });
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        cut.WaitForAssertion(() => Assert.True(hubs.ContainsKey("entities")));
        cut.Find("input#Description").Input("edited here");
        PipelineDto Stored(string description) => new()
        {
            Id = 5,
            Name = "Deploy Prod",
            Description = description,
            YamlDefinition = "name: deploy\ntrigger: manual\nstages: []",
            SourceBranch = "main",
            TriggerType = PipelineTriggerType.Manual
        };
        var saveResponse = new TaskCompletionSource<PipelineDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        // PLAN-002 B7: the PUT is awaited where it arrives. A render-driven wait on the request list was
        // checked only on renders, and none follows the request until the server answers.
        var putReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Put, "api/pipelines/5", _ =>
        {
            putReceived.TrySetResult();
            return saveResponse.Task;
        });
        _handler.SetJsonResponse("api/pipelines/5", Stored(storedDescription));

        var save = cut.InvokeAsync(() => cut.FindComponent<MonacoEditor>().Instance.OnSave.InvokeAsync());
        await cut.InvokeAsync(() => Services.GetRequiredService<OmniDialogService>().Close(new PipelineSaveDecision("main")));
        await putReceived.Task.WaitAsync(BunitTestHelper.AsyncRenderWait, Xunit.TestContext.Current.CancellationToken);
        await cut.InvokeAsync(() => HubHandlerInvoker.InvokeAsync(hubs["entities"], "EntityChanged", ResourceType.Pipeline, 5, "Updated"));
        saveResponse.SetResult(Stored("edited here"));
        await save;

        Assert.Equal(expectBanner, cut.Markup.Contains("pipeline-changed-elsewhere", StringComparison.Ordinal));
    }
}
