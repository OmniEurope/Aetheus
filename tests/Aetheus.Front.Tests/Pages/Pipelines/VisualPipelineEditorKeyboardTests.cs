// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>On the canvas, Delete removes the selected stage once confirmed and drops it from the other
/// stages' dependencies; Ctrl+Z and Ctrl+Y undo and redo it.</summary>
public sealed class VisualPipelineEditorKeyboardTests : BunitContext
{
    private const string Yaml = """
        trigger: manual
        stages:
          - name: build
            agent: server-1
            steps:
              - name: compile
                shell: dotnet build
          - name: test
            agent: server-1
            depends_on:
              - build
            steps:
              - name: run-tests
                shell: dotnet test
          - name: deploy
            agent: prod-server
            depends_on:
              - test
              - build
            steps:
              - name: publish
                shell: dotnet publish
        """;

    private readonly List<string> _emitted = [];

    public VisualPipelineEditorKeyboardTests()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        Services.AddSingleton<YamlSerializationService>();
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private PipelineYamlDefinition Parse(string yaml) => Services.GetRequiredService<YamlSerializationService>().Parse(yaml)!;

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private IRenderedComponent<VisualPipelineEditor> RenderEditor() =>
        Render<VisualPipelineEditor>(p => p
            .Add(x => x.YamlDefinition, Yaml)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.AvailableLibraries, new List<string>())
            .Add(x => x.AvailableVaults, new List<string>())
            .Add(x => x.YamlDefinitionChanged, yaml => _emitted.Add(yaml)));

    private static Task SelectAsync(IRenderedComponent<VisualPipelineEditor> cut, int index) =>
        cut.InvokeAsync(() => cut.FindComponents<StageNode>()[index].Instance.OnSelect.InvokeAsync());

    private static Task PressAsync(IRenderedComponent<VisualPipelineEditor> cut, string key, bool ctrl = false) =>
        cut.Find(".vp-canvas-outer").KeyDownAsync(new KeyboardEventArgs { Key = key, CtrlKey = ctrl });

    [Fact]
    public async Task DeleteOnTheSelectedStage_RemovesItAndItsDependencies_ThenUndoAndRedo()
    {
        Dialog.ConfirmResult = true;
        var cut = RenderEditor();
        await SelectAsync(cut, 1);

        await PressAsync(cut, "Delete");

        var removed = Assert.Single(_emitted);
        var stages = Parse(removed).Stages;
        Assert.Equal(["build", "deploy"], stages.Select(stage => stage.Name));
        Assert.Equal(["build"], stages[1].DependsOn);

        await PressAsync(cut, "z", ctrl: true);
        Assert.Equal(["build", "test", "deploy"], Parse(_emitted[^1]).Stages.Select(stage => stage.Name));

        await PressAsync(cut, "y", ctrl: true);
        Assert.Equal(removed, _emitted[^1]);
    }

    [Fact]
    public async Task ADeclinedDelete_KeepsTheStage()
    {
        Dialog.ConfirmResult = false;
        var cut = RenderEditor();
        await SelectAsync(cut, 1);

        await PressAsync(cut, "Backspace");

        Assert.NotNull(Dialog.LastConfirmMessage);
        Assert.Empty(_emitted);
    }

    [Fact]
    public async Task DeleteWithNothingSelected_AsksNothing()
    {
        var cut = RenderEditor();

        await PressAsync(cut, "Delete");
        await PressAsync(cut, "z", ctrl: true);

        Assert.Null(Dialog.LastConfirmMessage);
        Assert.Empty(_emitted);
    }

    [Fact]
    public async Task DuplicatingAStage_InsertsANamedCopyAfterIt()
    {
        var cut = RenderEditor();

        await cut.InvokeAsync(() => cut.FindComponents<StageNode>()[0].Instance.OnDuplicate.InvokeAsync());
        await cut.InvokeAsync(() => cut.FindComponents<StageNode>()[0].Instance.OnDuplicate.InvokeAsync());

        Assert.Equal(["build", "build-copy-2", "build-copy", "test", "deploy"], Parse(_emitted[^1]).Stages.Select(stage => stage.Name));
    }

    [Fact]
    public async Task AddingAStep_AppendsTheDialogsStep()
    {
        Dialog.OpenResult = new PipelineStepDefinition { Name = "lint", Shell = "dotnet format --verify-no-changes" };
        var cut = RenderEditor();

        await cut.InvokeAsync(() => cut.FindComponents<StageNode>()[2].Instance.OnAddStep.InvokeAsync());

        Assert.Equal(typeof(StepEditDialog), Dialog.LastComponent);
        Assert.Equal(["publish", "lint"], Parse(Assert.Single(_emitted)).Stages[2].Steps.Select(step => step.Name));
    }

    [Fact]
    public async Task EditingAStep_ReplacesItWithTheDialogsStep()
    {
        Dialog.OpenResult = new PipelineStepDefinition { Name = "compile-release", Shell = "dotnet build -c Release" };
        var cut = RenderEditor();

        await cut.InvokeAsync(() => cut.FindComponents<StageNode>()[0].Instance.OnEditStep.InvokeAsync(0));

        Assert.Equal("compile", ((PipelineStepDefinition)Dialog.LastParameters!["Step"]!).Name);
        var step = Assert.Single(Parse(Assert.Single(_emitted)).Stages[0].Steps);
        Assert.Equal("dotnet build -c Release", step.Shell);
    }

    [Fact]
    public async Task ADismissedStepDialog_ChangesNothing()
    {
        Dialog.OpenResult = null;
        var cut = RenderEditor();

        await cut.InvokeAsync(() => cut.FindComponents<StageNode>()[0].Instance.OnAddStep.InvokeAsync());
        await cut.InvokeAsync(() => cut.FindComponents<StageNode>()[0].Instance.OnEditStep.InvokeAsync(0));
        await cut.InvokeAsync(() => cut.FindComponents<StageNode>()[0].Instance.OnEditStep.InvokeAsync(5));

        Assert.Equal(2, Dialog.OpenCount);
        Assert.Empty(_emitted);
    }
}
