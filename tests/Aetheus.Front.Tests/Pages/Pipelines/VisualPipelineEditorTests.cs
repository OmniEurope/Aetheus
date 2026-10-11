// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class VisualPipelineEditorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VisualPipelineEditorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        Services.AddSingleton<YamlSerializationService>();
    }

    private IRenderedComponent<VisualPipelineEditor> RenderEditor(string yaml = "", List<string>? servers = null)
    {
        return Render<VisualPipelineEditor>(p => p
            .Add(x => x.YamlDefinition, yaml)
            .Add(x => x.AvailableServers, servers ?? [])
            .Add(x => x.AvailableLibraries, new List<string>())
            .Add(x => x.AvailableVaults, new List<string>()));
    }

    private static readonly string ValidYaml = """
        trigger: manual
        stages:
          - name: build
            agent: server-1
            steps:
              - name: compile
                shell: dotnet build
        """;

    private static readonly string MultiStageYaml = """
        trigger: manual
        stages:
          - name: build
            agent: server-1
            steps:
              - name: compile
                shell: dotnet build
          - name: test
            agent: server-1
            dependsOn:
              - build
            steps:
              - name: run-tests
                shell: dotnet test
          - name: deploy
            agent: prod-server
            dependsOn:
              - test
            steps:
              - name: publish
                shell: dotnet publish
        """;

    [Fact]
    public void Renders_EmptyPipeline_WhenNoYaml()
    {
        var cut = RenderEditor();
        Assert.Contains("EmptyPipeline", cut.Markup);
    }

    [Fact]
    public void Renders_ErrorBanner_WhenInvalidYaml()
    {
        var cut = RenderEditor("invalid: [yaml: broken");
        Assert.Contains("InvalidYamlCannotVisualize", cut.Markup);
    }

    [Fact]
    public void Renders_StageNodes_WhenValidYaml()
    {
        var cut = RenderEditor(ValidYaml, ["server-1"]);
        Assert.Contains("build", cut.Markup);
        Assert.Contains("compile", cut.Markup);
    }

    [Fact]
    public void Provenance_RendersInheritedOverriddenAndLocalStagesJobsAndSteps()
    {
        const string baseYaml = """
            name: base
            stages:
              - name: build
                steps:
                  - name: compile
                    shell: echo base
                  - name: inherited-step
                    shell: echo inherited
                jobs:
                  - name: package
                    steps:
                      - name: archive
                        shell: zip
              - name: inherited-stage
                steps:
                  - name: inherited-only
                    shell: echo inherited
            """;
        const string childYaml = """
            name: child
            extends: ci@1
            stages:
              - name: build
                steps:
                  - name: compile
                    shell: echo override
                  - name: local-step
                    shell: echo local
                jobs:
                  - name: package
                    steps:
                      - name: archive
                        shell: tar
              - name: deploy
                steps:
                  - name: publish
                    shell: echo publish
            """;

        var cut = Render<VisualPipelineEditor>(parameters => parameters
            .Add(component => component.YamlDefinition, childYaml)
            .Add(component => component.BaseTemplateYaml, baseYaml)
            .Add(component => component.AvailableServers, new List<string>())
            .Add(component => component.AvailableLibraries, new List<string>())
            .Add(component => component.AvailableVaults, new List<string>()));

        Assert.Contains("Overridden: stage:build", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Inherited: stage:build/step:inherited-step", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Overridden: stage:build/job:package/step:archive", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Local: stage:build/step:local-step", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Local: stage:deploy/step:publish", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"inherited-stage\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Inherited: stage:inherited-stage", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("inherited-only", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(
            "vp-provenance-inherited",
            cut.Find("[aria-label='inherited-stage']").ClassList);
    }

    [Fact]
    public async Task YamlRoundtrip_PreservesDefinition()
    {
        string? emittedYaml = null;
        var cut = Render<VisualPipelineEditor>(p => p
            .Add(x => x.YamlDefinition, ValidYaml)
            .Add(x => x.YamlDefinitionChanged, (string v) => { emittedYaml = v; })
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.AvailableLibraries, new List<string>())
            .Add(x => x.AvailableVaults, new List<string>()));

        var addButton = cut.FindAll("button").FirstOrDefault(b => b.Names().Contains("AddStage"));
        // The AddStage toolbar button must be present for valid YAML; guarantee it outside any branch
        // so a missing button fails the test instead of silently passing.
        Assert.NotNull(addButton);
        await cut.InvokeAsync(() => addButton.Click());
        // Adding a stage re-emits the whole definition, which still carries the original "build" stage.
        Assert.NotNull(emittedYaml);
        Assert.Contains("build", emittedYaml);
        Assert.DoesNotContain("echo \"Hello\"", emittedYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Toolbar_ContainsUndoRedoButtons()
    {
        var cut = RenderEditor("trigger: manual\nstages: []");
        var markup = cut.Markup;
        Assert.Contains("undo", markup.ToLower());
        Assert.Contains("redo", markup.ToLower());
    }

    // --- Multi-stage pipeline rendering ---

    [Fact]
    public void Renders_MultiStage_Pipeline()
    {
        var cut = RenderEditor(MultiStageYaml, ["server-1", "prod-server"]);
        Assert.Contains("build", cut.Markup);
        Assert.Contains("test", cut.Markup);
        Assert.Contains("deploy", cut.Markup);
    }

    // --- VisualPipelineGeometry.BuildEdgePath (S-DES-VPBZ / S-TECH-VPNH) ---

    [Fact]
    public void BuildEdgePath_EmitsCubicBezier_FromStartToEnd()
    {
        var result = VisualPipelineGeometry.BuildEdgePath(0, 0, 100, 100);
        Assert.StartsWith("M 0.0 0.0", result);
        Assert.Contains("C", result);           // curve, not a straight line
        Assert.EndsWith("100.0 100.0", result); // ends at the target anchor
    }

    [Fact]
    public void BuildEdgePath_HorizontalTangents_UseHalfTheSpanForControlPoints()
    {
        // dx = max(40, |200-0| * 0.5) = 100 -> first control point at x=100 sharing the start Y (flat tangent).
        var result = VisualPipelineGeometry.BuildEdgePath(0, 0, 200, 60);
        Assert.Contains("C 100.0 0.0", result);
        Assert.Contains("100.0 60.0", result);
    }

    [Fact]
    public void NodeHeight_GrowsPerAdditionalStep()
    {
        Assert.Equal(VisualPipelineGeometry.NodeBaseHeight, VisualPipelineGeometry.NodeHeight(1));
        Assert.Equal(VisualPipelineGeometry.NodeBaseHeight + VisualPipelineGeometry.StepRowHeight, VisualPipelineGeometry.NodeHeight(2));
        Assert.Equal(VisualPipelineGeometry.NodeBaseHeight, VisualPipelineGeometry.NodeHeight(0)); // clamped, never negative
    }

    // --- Internal state ---

    [Fact]
    public void SelectedStageId_InitiallyNull()
    {
        var cut = RenderEditor(ValidYaml, ["server-1"]);
        var selected = typeof(VisualPipelineEditor).GetField("_selectedStageId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(selected);
    }

    [Fact]
    public void IsCollapsed_InitiallyTrue()
    {
        var cut = RenderEditor(ValidYaml, ["server-1"]);
        var expanded = (bool)typeof(VisualPipelineEditor).GetField("_settingsExpanded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(expanded);
    }

    // --- OnStageSelected ---

    [Fact]
    public void OnStageSelected_TogglesSelection()
    {
        var cut = RenderEditor(ValidYaml, ["server-1"]);
        var method = typeof(VisualPipelineEditor).GetMethod("OnStageSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;

        method.Invoke(cut.Instance, ["0"]);
        var selected = (string?)typeof(VisualPipelineEditor).GetField("_selectedStageId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("0", selected);

        method.Invoke(cut.Instance, ["0"]);
        selected = (string?)typeof(VisualPipelineEditor).GetField("_selectedStageId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(selected);
    }

    // --- GetStageWarnings ---

    [Fact]
    public void GetStageWarnings_MissingAgent_ReturnsWarning()
    {
        var cut = RenderEditor(ValidYaml, ["server-1"]);

        var def = typeof(VisualPipelineEditor).GetField("_definition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var stages = ((PipelineYamlDefinition)def).Stages.ToList();
        stages[0] = stages[0] with { Agent = "" };
        typeof(VisualPipelineEditor).GetField("_definition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, ((PipelineYamlDefinition)def) with { Stages = stages });

        var method = typeof(VisualPipelineEditor).GetMethod("GetStageWarnings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var warnings = (List<string>)method.Invoke(cut.Instance, [stages[0]])!;
        Assert.Contains(warnings, w => w.Contains("Agent") || w.Contains("ValidationMissingAgent"));
    }

    [Fact]
    public void GetStageWarnings_NoSteps_ReturnsWarning()
    {
        var cut = RenderEditor(ValidYaml, ["server-1"]);

        var def = (PipelineYamlDefinition)typeof(VisualPipelineEditor).GetField("_definition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var stages = def.Stages.ToList();
        stages[0] = stages[0] with { Steps = [] };
        typeof(VisualPipelineEditor).GetField("_definition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, def with { Stages = stages });

        var method = typeof(VisualPipelineEditor).GetMethod("GetStageWarnings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var warnings = (List<string>)method.Invoke(cut.Instance, [stages[0]])!;
        Assert.Contains(warnings, w => w.Contains("Step") || w.Contains("ValidationNoSteps"));
    }

    [Fact]
    public void GetStageWarnings_UnknownDependency_ReturnsWarning()
    {
        var cut = RenderEditor(ValidYaml, ["server-1"]);

        var def = (PipelineYamlDefinition)typeof(VisualPipelineEditor).GetField("_definition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var stages = def.Stages.ToList();
        stages[0] = stages[0] with { DependsOn = ["nonexistent-stage"] };
        typeof(VisualPipelineEditor).GetField("_definition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, def with { Stages = stages });

        var method = typeof(VisualPipelineEditor).GetMethod("GetStageWarnings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var warnings = (List<string>)method.Invoke(cut.Instance, [stages[0]])!;
        Assert.NotEmpty(warnings);
    }

    // --- HasCircularDependency ---

    [Fact]
    public void HasCircularDependency_NoCycle_ReturnsFalse()
    {
        var cut = RenderEditor(MultiStageYaml, ["server-1", "prod-server"]);

        var definition = (PipelineYamlDefinition)typeof(VisualPipelineEditor)
            .GetField("_definition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var result = PipelineVisualValidator.HasCircularDependency("build", definition);
        Assert.False(result);
    }

    [Fact]
    public void HasCircularDependency_SecondaryCycle_ReturnsTrue()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages =
            [
                new PipelineStageDefinition { Name = "start", DependsOn = ["a"] },
                new PipelineStageDefinition { Name = "a", DependsOn = ["b"] },
                new PipelineStageDefinition { Name = "b", DependsOn = ["a"] }
            ]
        };

        Assert.True(PipelineVisualValidator.HasCircularDependency("start", definition));
    }

    // --- GetOtherStageNames ---

    [Fact]
    public void GetOtherStageNames_ExcludesCurrentIndex()
    {
        var cut = RenderEditor(MultiStageYaml, ["server-1", "prod-server"]);

        var method = typeof(VisualPipelineEditor).GetMethod("GetOtherStageNames", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var names = (List<string>)method.Invoke(cut.Instance, [0])!;

        Assert.DoesNotContain("build", names);
        Assert.Contains("test", names);
        Assert.Contains("deploy", names);
    }

    // --- Settings panel ---

    [Fact]
    public void SettingsCollapsed_DefaultTrue()
    {
        var cut = RenderEditor(ValidYaml);
        var expanded = (bool)typeof(VisualPipelineEditor).GetField("_settingsExpanded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(expanded);
    }

    // --- Trigger options ---

    [Fact]
    public void TriggerOptions_ContainsExpectedValues()
    {
        var options = (List<string>)typeof(VisualPipelineEditor).GetField("_triggerOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Contains("manual", options);
        Assert.Contains("webhook", options);
        Assert.Contains("schedule", options);
    }

    // --- Renders pipeline with variables ---

    [Fact]
    public void Renders_PipelineWithVariables()
    {
        var yaml = """
            trigger: manual
            variables:
              APP_NAME: my-app
              VERSION: "1.0"
            stages:
              - name: build
                agent: default
                steps:
                  - name: compile
                    shell: echo $APP_NAME
            """;
        var cut = RenderEditor(yaml);
        Assert.Contains("build", cut.Markup);
    }

    // --- Renders empty stages list ---

    [Fact]
    public void Renders_EmptyStagesList()
    {
        var yaml = "trigger: manual\nstages: []";
        var cut = RenderEditor(yaml);
        Assert.Contains("EmptyPipeline", cut.Markup);
    }

    // --- CanUndo/CanRedo ---

    [Fact]
    public void CanUndo_InitiallyFalse()
    {
        var cut = RenderEditor(ValidYaml);
        var canUndo = (bool)typeof(VisualPipelineEditor).GetProperty("CanUndo", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(canUndo);
    }

    [Fact]
    public void CanRedo_InitiallyFalse()
    {
        var cut = RenderEditor(ValidYaml);
        var canRedo = (bool)typeof(VisualPipelineEditor).GetProperty("CanRedo", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(canRedo);
    }

    // SITE-FUNC-AUTOCOMPLETE-001: the stage-template picker loads from OnAfterRenderAsync; a network
    // failure there escaped the render cycle and took the editor down.
    [Fact]
    public void StageTemplateNetworkFailure_KeepsTheEditor()
    {
        _handler.SetAsyncJsonResponse<List<PipelineTemplateSummaryDto>>(HttpMethod.Get, "api/pipelines/templates",
            _ => throw new HttpRequestException("network error"));

        var cut = RenderEditor(ValidYaml);
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("api/pipelines/templates", StringComparison.Ordinal)));

        cut.Render();

        Assert.Contains("build", cut.Markup, StringComparison.Ordinal);
    }
}
