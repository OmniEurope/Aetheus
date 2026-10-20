// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The frozen ID column of a pipeline's runs is as wide as its id and its row action, not
/// a fixed width nor an equal share of the table.</summary>
public sealed class PipelineRunIdColumnWidthTests
{
    [Fact]
    public void TheWidth_FollowsTheLongestId()
    {
        var shortIds = PipelineRunPresentation.IdColumnWidth([new PipelineRunDto { Id = 7, Status = PipelineStatus.Success }]);
        var longIds = PipelineRunPresentation.IdColumnWidth(
        [
            new PipelineRunDto { Id = 7, Status = PipelineStatus.Success },
            new PipelineRunDto { Id = 24690, Status = PipelineStatus.Success }
        ]);

        Assert.Equal("calc(max(3ch, 2.5rem) + 2 * var(--omni-space-md))", shortIds);
        Assert.Equal("calc(max(7ch, 2.5rem) + 2 * var(--omni-space-md))", longIds);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Running)]
    public void ARowAction_AddsOneActionButton(PipelineStatus status)
    {
        var width = PipelineRunPresentation.IdColumnWidth(
        [
            new PipelineRunDto { Id = 2469, Status = PipelineStatus.Success },
            new PipelineRunDto { Id = 2470, Status = status }
        ]);

        Assert.Equal("calc(max(6ch, 2.5rem) + var(--omni-badge-height) + var(--omni-space-xs) + 2 * var(--omni-space-md))", width);
    }

    [Fact]
    public void ACancellationAlreadyRequested_OffersNoActionAndTakesNoRoomForIt()
    {
        var width = PipelineRunPresentation.IdColumnWidth(
            [new PipelineRunDto { Id = 2470, Status = PipelineStatus.Running, CancellationRequested = true }]);

        Assert.DoesNotContain("--omni-badge-height", width, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePipelineEditGrid_BindsTheIdColumnToIt()
    {
        var markup = File.ReadAllText(Path.Combine(Architecture.RepositoryScan.Root,
            "src", "Aetheus.Front", "Components", "Pipelines", "PipelineEdit.razor"));

        Assert.Contains(
            "Property=\"Id\" Title=\"@L[\"ID\"]\" Width=\"@PipelineRunPresentation.IdColumnWidth(_runs.Concat(_firstRunsPage))\"",
            markup, StringComparison.Ordinal);
    }
}
