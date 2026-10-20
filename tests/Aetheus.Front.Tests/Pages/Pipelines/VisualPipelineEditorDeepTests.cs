// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public class VisualPipelineEditorDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public VisualPipelineEditorDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<YamlSerializationService>();
    }

    private const string SimpleYaml = @"stages:
- name: build
  agent: linux
  steps:
  - name: run
    shell: echo hello";

    [Fact]
    public void Renders_WithEmptyYaml()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<VisualPipelineEditor>(p =>
            p.Add(x => x.YamlDefinition, "")
             .Add(x => x.AvailableServers, [])
             .Add(x => x.AvailableLibraries, [])
             .Add(x => x.AvailableVaults, []));
        // Empty YAML -> the empty-canvas placeholder renders, no stage nodes.
        Assert.Contains("EmptyPipeline", cut.Markup);
        Assert.Empty(cut.FindAll(".vp-node"));
    }

    [Fact]
    public void Renders_WithValidYaml()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<VisualPipelineEditor>(p =>
            p.Add(x => x.YamlDefinition, SimpleYaml)
             .Add(x => x.AvailableServers, ["linux"])
             .Add(x => x.AvailableLibraries, [])
             .Add(x => x.AvailableVaults, []));
        // Valid YAML parses into a stage node showing the "build" stage.
        Assert.Contains("build", cut.Markup);
        Assert.DoesNotContain("EmptyPipeline", cut.Markup);
    }

    [Fact]
    public async Task OnParametersSet_ParsesYaml_CorrectlyOnChange()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<VisualPipelineEditor>(p =>
            p.Add(x => x.YamlDefinition, "")
             .Add(x => x.AvailableServers, [])
             .Add(x => x.AvailableLibraries, [])
             .Add(x => x.AvailableVaults, []));

        // Force re-parse by resetting the last parsed yaml, then calling OnParametersSet
        typeof(VisualPipelineEditor).GetField("_lastParsedYaml", Priv)!.SetValue(cut.Instance, null);
        // Directly invoke OnParametersSet with YAML set on instance
        typeof(VisualPipelineEditor).GetProperty("YamlDefinition")!.SetValue(cut.Instance, SimpleYaml);
        var onParamsSet = typeof(VisualPipelineEditor).GetMethod("OnParametersSet",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => onParamsSet.Invoke(cut.Instance, []));
        var def = (PipelineYamlDefinition)typeof(VisualPipelineEditor).GetField("_definition", Priv)!.GetValue(cut.Instance)!;
        // The YAML change must be parsed into a "build" stage carrying its "run" step.
        Assert.NotNull(def);
        var stage = Assert.Single(def.Stages);
        Assert.Equal("build", stage.Name);
        Assert.Equal("run", Assert.Single(stage.Steps).Name);
    }

    [Fact]
    public void LateYamlChangeReloadsPersistedPositionsBeforeShowingNodes()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        JSInterop.Setup<LayoutResult>("visualPipeline.computeLayout", _ => true)
            .SetResult(new LayoutResult
            {
                Nodes = [new LayoutNode { Id = "0", X = 0, Y = 0 }]
            });
        // R1-66: the positions laid out by hand are a user preference of pipeline 42.
        var preferences = Services.GetRequiredService<Aetheus.Front.Tests.TestDoubles.InMemoryUserPreferences>();
        preferences.Seed(PreferenceKeys.VisualLayout(42), new VisualPipelineLayoutStore.LayoutBlob
        {
            Sig = "5:build",
            Pos = new Dictionary<string, double[]> { ["0"] = [100, 76.666666666666657] }
        });
        var cut = Render<VisualPipelineEditor>(parameters => parameters
            .Add(component => component.YamlDefinition, SimpleYaml)
            .Add(component => component.PipelineId, 42)
            .Add(component => component.AvailableServers, ["linux"])
            .Add(component => component.AvailableLibraries, [])
            .Add(component => component.AvailableVaults, []));

        cut.Render(parameters => parameters.Add(
            component => component.YamlDefinition,
            SimpleYaml.Replace("echo hello", "echo changed", StringComparison.Ordinal)));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, preferences.Reads.Count(key => key == PreferenceKeys.VisualLayout(42)));
            // The coordinates ride as data attributes and a script copies them into the custom
            // properties through the CSSOM, so the markup carries no style.
            var node = cut.Find(".vp-node");
            Assert.Equal("100", node.GetAttribute("data-node-x"));
            Assert.Equal("76.66666666666666", node.GetAttribute("data-node-y"));
            Assert.Null(node.GetAttribute("style"));
            Assert.DoesNotContain("vp-canvas-inner-loading", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task OnStageSelected_SelectsAndDeselects()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<VisualPipelineEditor>(p =>
            p.Add(x => x.YamlDefinition, SimpleYaml)
             .Add(x => x.AvailableServers, [])
             .Add(x => x.AvailableLibraries, [])
             .Add(x => x.AvailableVaults, []));

        var field = typeof(VisualPipelineEditor).GetField("_selectedStageId", Priv)!;
        var method = typeof(VisualPipelineEditor).GetMethod("OnStageSelected", Priv)!;
        await cut.InvokeAsync(() => method.Invoke(cut.Instance, ["0"]));

        var selected = (string?)field.GetValue(cut.Instance);
        Assert.Equal("0", selected);

        // Selecting the same stage again toggles it back to no selection.
        await cut.InvokeAsync(() => method.Invoke(cut.Instance, ["0"]));
        Assert.Null((string?)field.GetValue(cut.Instance));
    }

    [Fact]
    public async Task OnNodeMoved_UpdatesPosition()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<VisualPipelineEditor>(p =>
            p.Add(x => x.YamlDefinition, "")
             .Add(x => x.AvailableServers, [])
             .Add(x => x.AvailableLibraries, [])
             .Add(x => x.AvailableVaults, []));

        await cut.InvokeAsync(() => cut.Instance.OnNodeMoved("0", 100.0, 200.0));
        var positions = typeof(VisualPipelineEditor)
            .GetField("_nodePositions", Priv)!.GetValue(cut.Instance) as System.Collections.IDictionary;
        Assert.True(positions!.Contains("0"));
    }

    [Fact]
    public async Task OnCanvasClicked_ClearsSelection()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<VisualPipelineEditor>(p =>
            p.Add(x => x.YamlDefinition, "")
             .Add(x => x.AvailableServers, [])
             .Add(x => x.AvailableLibraries, [])
             .Add(x => x.AvailableVaults, []));

        typeof(VisualPipelineEditor).GetField("_selectedStageId", Priv)!.SetValue(cut.Instance, "0");
        await cut.InvokeAsync(() => cut.Instance.OnCanvasClicked());
        var selected = (string?)typeof(VisualPipelineEditor).GetField("_selectedStageId", Priv)!.GetValue(cut.Instance);
        Assert.Null(selected);
    }

    [Fact]
    public void GetStageWarnings_ReturnsMissingAgent_WhenEmpty()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<VisualPipelineEditor>(p =>
            p.Add(x => x.YamlDefinition, "")
             .Add(x => x.AvailableServers, [])
             .Add(x => x.AvailableLibraries, [])
             .Add(x => x.AvailableVaults, []));

        var method = typeof(VisualPipelineEditor).GetMethod("GetStageWarnings", Priv)!;
        var stage = new PipelineStageDefinition { Name = "x", Agent = "", Steps = [] };
        var warnings = (List<string>)method.Invoke(cut.Instance, [stage])!;
        Assert.Contains("ValidationMissingAgent", warnings);
    }

    [Fact]
    public void BuildEdgePath_UsesInvariantDecimalSeparator()
    {
        // The WASM client runs in the browser locale (e.g. fr-FR); the SVG path MUST still use
        // '.' as the decimal separator, never a comma - SVG parses a comma as a coordinate
        // separator, which silently corrupts the geometry. Locks the fr-FR regression.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var result = Aetheus.Front.Components.Pipelines.VisualPipelineGeometry.BuildEdgePath(0.0, 0.0, 100.0, 50.5);
            Assert.StartsWith("M", result);
            Assert.DoesNotContain(",", result);
            Assert.Contains("100.0", result);
            Assert.Contains("50.5", result);
            Assert.Contains("C", result); // S-DES-VPBZ: cubic Bézier, not a straight line
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
