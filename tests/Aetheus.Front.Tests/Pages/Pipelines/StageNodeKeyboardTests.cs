// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>A stage node is a keyboard button: Enter and Space select it, any other key does not.</summary>
public sealed class StageNodeKeyboardTests : BunitContext
{
    private int _selected;

    public StageNodeKeyboardTests() => BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<StageNode> RenderNode() => Render<StageNode>(p => p
        .Add(x => x.Stage, new PipelineStageDefinition { Name = "build" })
        .Add(x => x.ElementId, "stage-build")
        .Add(x => x.OnSelect, EventCallback.Factory.Create(this, () => _selected++)));

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    [InlineData("Spacebar")]
    public async Task EnterOrSpace_SelectsTheStage(string key)
    {
        var cut = RenderNode();

        await cut.Find("#stage-build").KeyDownAsync(new KeyboardEventArgs { Key = key });

        Assert.Equal(1, _selected);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Tab")]
    [InlineData("Delete")]
    public async Task AnyOtherKey_LeavesTheSelectionAlone(string key)
    {
        var cut = RenderNode();

        await cut.Find("#stage-build").KeyDownAsync(new KeyboardEventArgs { Key = key });

        Assert.Equal(0, _selected);
    }
}
