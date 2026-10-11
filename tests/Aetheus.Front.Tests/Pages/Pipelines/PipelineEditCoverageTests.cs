// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Extra coverage for PipelineEdit - focuses on uncovered branches:
/// HandleSaveOutcome (error + null paths), OnTemplateSelected, IsConfigFailure/IsExecutionFailure,
/// IsFailed, new pipeline with pre-selected project/environment/projectServer, new pipeline
/// navigation params, OnSubmit failure path, GetRunBadge, VerifyElapsedSeconds edge cases.
/// </summary>
public class PipelineEditCoverageTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineEditCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        SetupCommonStubs();
    }

    private void SetupCommonStubs()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "My Project" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 1, Name = "Basic CI" },
            new() { Id = 2, Name = "Docker Deploy" }
        });
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string> { "lib1" });
        _handler.SetJsonResponse("api/vaults/names", new List<string> { "vault1" });
        _handler.SetJsonResponse("api/servers/names", new List<string> { "srv1" });
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
    }

    private void SetupEditStubs(int id = 30)
    {
        _handler.SetJsonResponse($"api/pipelines/{id}", new PipelineDto
        {
            Id = id,
            Name = "Coverage Pipeline",
            Description = "Coverage desc",
            YamlDefinition = "name: coverage\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual,
            ProjectId = 1
        });
        _handler.SetJsonResponse($"api/pipelines/{id}/runs", new PaginatedResult<PipelineRunDto>
        {
            Items =
            [
                new() { Id = 1, PipelineId = id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddHours(-2), CompletedAt = DateTime.UtcNow.AddHours(-1) },
                new() { Id = 2, PipelineId = id, Status = PipelineStatus.Failed, StartedAt = DateTime.UtcNow.AddMinutes(-30), CompletedAt = DateTime.UtcNow,
                    Warnings = ["no online server matched stage build"] },
                new() { Id = 3, PipelineId = id, Status = PipelineStatus.Failed, StartedAt = DateTime.UtcNow.AddMinutes(-20), CompletedAt = DateTime.UtcNow,
                    Warnings = ["Step failed: deploy script exited with code 1"] },
                new() { Id = 4, PipelineId = id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = null }
            ],
            TotalCount = 4
        });
    }

    // ── IsFailed / IsConfigFailure / IsExecutionFailure ───────────────────────

    [Theory]
    [InlineData(PipelineStatus.Success, false)]
    [InlineData(PipelineStatus.Running, false)]
    [InlineData(PipelineStatus.Failed, true)]
    [InlineData(PipelineStatus.Cancelled, false)]
    public void IsFailed_ReturnsExpected(PipelineStatus status, bool expected)
    {
        var run = new PipelineRunDto { Status = status, Warnings = [] };
        Assert.Equal(expected, PipelineRunPresentation.IsFailed(run));
    }

    [Fact]
    public void IsConfigFailure_NoOnlineServer_ReturnsTrue()
    {
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.Failed,
            Warnings = ["aucun serveur disponible"],
            Steps = [new PipelineStepRunDto { Status = TaskExecutionStatus.Failed, TaskId = null }]
        };
        Assert.True(PipelineRunPresentation.IsConfigFailure(run));
    }

    [Fact]
    public void IsConfigFailure_IsBlocked_ReturnsTrue()
    {
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.Failed,
            Warnings = ["beliebige Meldung"],
            Steps = [new PipelineStepRunDto { Status = TaskExecutionStatus.Failed, TaskId = null }]
        };
        Assert.True(PipelineRunPresentation.IsConfigFailure(run));
    }

    [Fact]
    public void IsConfigFailure_NoExecutableSteps_ReturnsTrue()
    {
        var run = new PipelineRunDto { Status = PipelineStatus.Failed, Warnings = ["no executable steps found"] };
        Assert.True(PipelineRunPresentation.IsConfigFailure(run));
    }

    [Fact]
    public void IsConfigFailure_Success_ReturnsFalse()
    {
        var run = new PipelineRunDto { Status = PipelineStatus.Success, Warnings = [] };
        Assert.False(PipelineRunPresentation.IsConfigFailure(run));
    }

    [Fact]
    public void IsExecutionFailure_RegularFailure_ReturnsTrue()
    {
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.Failed,
            Warnings = ["step exited with code 1"],
            Steps = [new PipelineStepRunDto { Status = TaskExecutionStatus.Failed, TaskId = 42 }]
        };
        Assert.True(PipelineRunPresentation.IsExecutionFailure(run));
    }

    [Fact]
    public void IsExecutionFailure_ConfigFailure_ReturnsFalse()
    {
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.Failed,
            Warnings = ["texte libre sans contrat"],
            Steps = [new PipelineStepRunDto { Status = TaskExecutionStatus.Failed, TaskId = null }]
        };
        Assert.False(PipelineRunPresentation.IsExecutionFailure(run));
    }

    // ── FormatDuration edge cases ─────────────────────────────────────────────

    [Fact]
    public void FormatDuration_ExactlyOneMinute_FormatsAsMinutes()
    {
        var start = DateTime.UtcNow;
        var end = start.AddMinutes(1);
        var result = PipelineRunPresentation.FormatDuration(start, end);
        Assert.Contains("m", result);
    }

    // ── New pipeline with pre-selected IDs ────────────────────────────────────
    // ProjectId/EnvironmentId/ProjectServerId are [SupplyParameterFromQuery]: they are bound from
    // the URL query string, so we drive them by navigating the fake NavigationManager before render.

    private static string ModelYaml(IRenderedComponent<PipelineEdit> cut)
    {
        var model = typeof(PipelineEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        return (string)model.GetType().GetProperty("YamlDefinition")!.GetValue(model)!;
    }

    [Fact]
    public void NewPipeline_Renders_WithDefaultYaml()
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        // A brand-new pipeline seeds the scaffold YAML template into the model.
        var yaml = ModelYaml(cut);
        Assert.Contains("name: my-pipeline", yaml);
        Assert.Contains("trigger: manual", yaml);
        Assert.Contains("stages:", yaml);
    }

    [Fact]
    public void NewPipeline_InputEvents_AreSentByTheCreateForm()
    {
        _handler.SetJsonResponse("api/pipelines", new PipelineDto { Id = 91, Name = "template-qa" });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://test/pipelines/new?projectId=1");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        cut.Find("input#Name").Input("template-qa");
        cut.Find("input#Description").Input("Local template reuse validation.");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            _handler.Requests,
            request => request.Method == "POST" && request.Url.EndsWith("api/pipelines", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("api/pipelines", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreatePipelineRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("template-qa", request!.Name);
        Assert.Equal("Local template reuse validation.", request.Description);
        Assert.Equal(1, request.ProjectId);
    }

    [Fact]
    public void NewPipeline_WithProjectIdQuery_PreSelectsProjectInModel()
    {
        // ?projectId=1 matches a known project → OnInitializedAsync pre-selects it on the model.
        // ProjectId is [SupplyParameterFromQuery] (not a [Parameter]), so it must come from the URL.
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://test/pipelines/new?projectId=1");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        var model = typeof(PipelineEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var projectId = (int?)model.GetType().GetProperty("ProjectId")!.GetValue(model);
        Assert.Equal(1, projectId);
    }

    [Fact]
    public void NewPipeline_WithUnknownProjectIdQuery_DoesNotPreSelect()
    {
        // ?projectId=999 has no matching project → the model stays unselected (no owner forced).
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://test/pipelines/new?projectId=999");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        var model = typeof(PipelineEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var projectId = (int?)model.GetType().GetProperty("ProjectId")!.GetValue(model);
        Assert.Null(projectId);
    }

    // ── OnTemplateSelected ────────────────────────────────────────────────────

    [Fact]
    public async Task OnTemplateSelected_NonIntValue_LeavesTheStateUnchanged()
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        var before = ModelYaml(cut);
        var method = typeof(PipelineEdit).GetMethod("OnTemplateSelected", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [null])!);

        // A non-int selection value is a no-op: the YAML must be byte-for-byte unchanged.
        Assert.Equal(before, ModelYaml(cut));
    }

    [Fact]
    public async Task OnTemplateSelected_ValidTemplateId_UpdatesYaml()
    {
        _handler.SetJsonResponse("api/pipelines/templates/1", new PipelineTemplateDto
        {
            Id = 1,
            Name = "Basic CI",
            Version = 3,
            YamlContent = "name: basic-ci\ntrigger: manual\nstages: []"
        });
        _handler.SetJsonResponse(
            "api/pipelines/templates/1/resolve?version=3",
            "name: basic-ci\ntrigger: manual\nstages: []");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        var method = typeof(PipelineEdit).GetMethod("OnTemplateSelected", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [(object)1])!);

        var model = typeof(PipelineEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var yaml = (string)model.GetType().GetProperty("YamlDefinition")!.GetValue(model)!;
        Assert.Contains("extends: Basic CI@3", yaml);
        Assert.DoesNotContain("stages:\n- name:", yaml);
    }

    /// <summary>
    /// PLAN-005 lot 6 / D42 on the template parameters of a new pipeline: a described parameter has
    /// the help icon and the field points to the hidden description; no caption under the field.
    /// </summary>
    [Fact]
    public void TemplateParameters_CarryTheirDescriptionInTheHelpIcon_NotACaption()
    {
        const string description = "Where the application is published.";
        const string templateYaml =
            "name: basic-ci\ntrigger: manual\nparameters:\n" +
            "  - name: target\n    display_name: Target\n    type: string\n    description: " + description + "\n" +
            "  - name: plain\n    type: string\nstages: []";
        _handler.SetJsonResponse("api/pipelines/templates/1", new PipelineTemplateDto
        {
            Id = 1,
            Name = "Basic CI",
            Version = 3,
            YamlContent = templateYaml
        });
        _handler.SetJsonResponse("api/pipelines/templates/1/resolve?version=3", templateYaml);
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://test/pipelines/new?projectId=1&templateId=1");

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));

        var help = cut.WaitForElements(".param-help");
        Assert.Single(help);
        Assert.Equal(description, help[0].GetAttribute("aria-label"));
        var descriptionId = ParameterHelpRow.DescribedBy("tpl-param", "target", description)!;
        Assert.Equal(description, cut.Find($"#{descriptionId}").TextContent);
        Assert.Single(cut.FindAll($"[aria-describedby='{descriptionId}']"));
        Assert.DoesNotContain(cut.FindAll(".rz-text-caption"), caption => caption.TextContent.Contains(description));
    }

    [Fact]
    public void NewPipeline_TemplateAndProjectQuery_AppliesTemplateToSelectedProject()
    {
        _handler.SetJsonResponse("api/pipelines/templates/1", new PipelineTemplateDto
        {
            Id = 1,
            Name = "Basic CI",
            Version = 3,
            YamlContent = "name: basic-ci\ntrigger: manual\nstages: []"
        });
        _handler.SetJsonResponse(
            "api/pipelines/templates/1/resolve?version=3",
            "name: basic-ci\ntrigger: manual\nstages: []");
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://test/pipelines/new?projectId=1&templateId=1");

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => ModelYaml(cut).Contains("extends: Basic CI@3", StringComparison.Ordinal));

        var model = typeof(PipelineEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(1, (int?)model.GetType().GetProperty("ProjectId")!.GetValue(model));
        Assert.Contains("extends: Basic CI@3", ModelYaml(cut), StringComparison.Ordinal);
    }

    // ── HandleSaveOutcome ─────────────────────────────────────────────────────

    [Fact]
    public void HandleSaveOutcome_WithError_ShowsErrorToast()
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));

        var method = typeof(PipelineEdit).GetMethod("HandleSaveOutcome", Priv)!;
        var called = false;
        method.Invoke(cut.Instance, [
            new ApiOutcome<PipelineDto, YamlValidationResultDto>(
                null,
                new YamlValidationResultDto { Errors = ["Missing stages section"] },
                false),
            "PipelineSaved",
            "Saved",
            (Action<PipelineDto>)(p => called = true)
        ]);

        Assert.False(called); // No success callback
    }

    [Fact]
    public void HandleSaveOutcome_WithNullErrorAndNullValue_ShowsGenericError()
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));

        var method = typeof(PipelineEdit).GetMethod("HandleSaveOutcome", Priv)!;
        var called = false;
        // Should not throw
        method.Invoke(cut.Instance, [
            new ApiOutcome<PipelineDto, YamlValidationResultDto>(null, null, false),
            "PipelineSaved",
            "Saved",
            (Action<PipelineDto>)(p => called = true)
        ]);

        Assert.False(called);
    }

    [Fact]
    public void HandleSaveOutcome_WithValue_CallsOnSuccess()
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));

        var method = typeof(PipelineEdit).GetMethod("HandleSaveOutcome", Priv)!;
        var called = false;
        var dto = new PipelineDto { Id = 99, Name = "success" };
        method.Invoke(cut.Instance, [
            new ApiOutcome<PipelineDto, YamlValidationResultDto>(dto, null, false),
            "PipelineSaved",
            "Saved",
            (Action<PipelineDto>)(p => called = true)
        ]);

        Assert.True(called);
    }

    // ── OnSubmit new pipeline ────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_NewPipeline_CreatesAndNavigates()
    {
        _handler.SetJsonResponse("api/pipelines", new PipelineDto { Id = 88, Name = "created" });
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 100, TimeSpan.FromSeconds(2));

        var model = typeof(PipelineEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "new-pipeline");

        var method = typeof(PipelineEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // A successful create POSTs to api/pipelines and routes to the created pipeline's detail page.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/pipelines"));
        Assert.EndsWith("/pipelines/88", nav.Uri);
        Assert.False((bool)typeof(PipelineEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!);
    }

    // ── Edit pipeline runs ────────────────────────────────────────────────────

    [Fact]
    public void EditPipeline_WithConfigFailureRun_RendersRuns()
    {
        SetupEditStubs(30);
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 30));
        cut.WaitForState(() => cut.Markup.Contains("Coverage Pipeline"), TimeSpan.FromSeconds(2));
        Assert.Contains("Coverage Pipeline", cut.Markup);
    }

    // ── IsNew ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IsNew_NullId_ReturnsTrue()
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)null));
        var val = (bool)typeof(PipelineEdit).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.True(val);
    }

    [Fact]
    public void IsNew_ZeroId_ReturnsTrue()
    {
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, (int?)0));
        var val = (bool)typeof(PipelineEdit).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.True(val);
    }

    [Fact]
    public void RunRowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        SetupEditStubs(30);

        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 30));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)), TimeSpan.FromSeconds(3));

        // The run links and the cancel button of the running run.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 4);
    }
}
