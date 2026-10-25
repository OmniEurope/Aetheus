// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests.Pages;

public class StageEditPanelTests : BunitContext
{
    public StageEditPanelTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    private static PipelineStageDefinition CreateStage() => new()
    {
        Name = "build",
        Agent = "server-1",
        Variables = new Dictionary<string, string> { ["ENV"] = "dev" },
        Steps =
        [
            new PipelineStepDefinition { Name = "compile", Shell = "dotnet build", TimeoutSeconds = 300 },
            new PipelineStepDefinition { Name = "test", Shell = "dotnet test", TimeoutSeconds = 600 }
        ]
    };

    [Fact]
    public void Renders_StageName()
    {
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string> { "server-1", "server-2" })
            .Add(x => x.OtherStageNames, new List<string> { "deploy" }));

        Assert.Contains("build", cut.Markup);
        Assert.Contains("EditStage", cut.Markup);
    }

    [Fact]
    public void Renders_Steps()
    {
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>()));

        Assert.Contains("compile", cut.Markup);
        Assert.Contains("test", cut.Markup);
    }

    [Fact]
    public void Renders_AddStepButton()
    {
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>()));

        Assert.Contains("AddStep", cut.Markup);
    }

    [Fact]
    public void Renders_DeleteStageButton()
    {
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>()));

        Assert.Contains("DeleteStage", cut.Markup);
    }

    [Fact]
    public void Renders_CloseButton()
    {
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>()));

        var closeBtn = cut.FindComponents<OmniButton>().FirstOrDefault(button =>
            button.FindComponents<OmniIcon>().Any(icon => icon.Instance.Name == OmniIconName.Close));
        Assert.NotNull(closeBtn);
    }

    [Fact]
    public async Task CloseButton_InvokesOnClose()
    {
        var closed = false;
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>())
            .Add(x => x.OnClose, () => { closed = true; }));

        var closeBtn = cut.FindComponents<OmniButton>().First(button =>
            button.FindComponents<OmniIcon>().Any(icon => icon.Instance.Name == OmniIconName.Close));
        await cut.InvokeAsync(() => closeBtn.Find("button").Click());

        Assert.True(closed);
    }

    [Fact]
    public async Task DeleteStage_InvokesCallback()
    {
        var deleted = false;
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>())
            .Add(x => x.OnDeleteStage, () => { deleted = true; }));

        var deleteBtn = cut.FindAll("button").First(b => b.Names().Contains("DeleteStage"));
        await cut.InvokeAsync(() => deleteBtn.Click());

        Assert.True(deleted);
    }

    [Fact]
    public void Renders_EmptyStage()
    {
        var stage = new PipelineStageDefinition
        {
            Name = "empty",
            Agent = "",
            Variables = [],
            Steps = []
        };
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>()));

        Assert.Contains("empty", cut.Markup);
        Assert.Contains("AddStep", cut.Markup);
    }

    [Fact]
    public void Renders_StepMoveActionsInTheStepMenu()
    {
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>()));

        // R-2: each step keeps one visible action (Edit) and one menu holding move up, move down and delete.
        Assert.Equal(stage.Steps.Count, cut.FindAll(".omni-overflow-menu__trigger").Count);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Edit);
        Assert.NotNull(cut.FindMenuItem("MoveUp"));
        Assert.NotNull(cut.FindMenuItem("MoveDown"));
        Assert.NotNull(cut.FindMenuItem("Delete"));
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Upgrade);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.ArrowDown);
    }

    [Fact]
    public void MovingAStepDownFromItsMenu_ReordersTheSteps()
    {
        PipelineStageDefinition? changed = null;
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, CreateStage())
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>())
            .Add(x => x.StageChanged, EventCallback.Factory.Create<PipelineStageDefinition>(this, s => changed = s)));

        cut.FindMenuItem("MoveDown")!.Click();

        Assert.NotNull(changed);
        Assert.Equal(["test", "compile"], changed.Steps.Select(step => step.Name));
    }

    [Fact]
    public void Renders_VariablesFieldset()
    {
        var stage = CreateStage();
        var cut = Render<StageEditPanel>(p => p
            .Add(x => x.Stage, stage)
            .Add(x => x.AvailableServers, new List<string>())
            .Add(x => x.OtherStageNames, new List<string>()));

        Assert.Contains("StageVariables", cut.Markup);
    }
}
