// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Projects;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Recette R-26 (user decision 2026-11-05): the project's overview, where the wizard ends, tells what the preparation of
/// the telemetry and the audience did for each item (done, already present, failed with its reason). It shows what the wizard
/// recorded and nothing else: no card without a recorded preparation, a failed item never shown as done, and a dismissed card
/// stays dismissed.</summary>
public sealed class ProjectCollectionSetupResultTests : BunitContext
{
    private const string Item = "[data-testid='collection-setup-item']";

    public ProjectCollectionSetupResultTests() => BunitTestHelper.RegisterServices(this);

    private static PipelineSetupCollectionItemDto Entry(
        PipelineSetupCollectionKind kind, PipelineSetupCollectionOutcome outcome, string? detail = null) =>
        new() { Kind = kind, Outcome = outcome, Detail = detail };

    private IRenderedComponent<ProjectCollectionSetupResult> Open(params PipelineSetupCollectionItemDto[] items)
    {
        if (items.Length > 0)
            Services.GetRequiredService<PipelineSetupCollectionState>().Record(3, new PipelineSetupCollectionResultDto { Items = [.. items] });
        return Render<ProjectCollectionSetupResult>(p => p.Add(x => x.ProjectId, 3));
    }

    [Fact]
    public void NothingIsShown_WhenTheWizardRecordedNothingForTheProject()
    {
        var cut = Open();

        Assert.Empty(cut.FindAll("[data-testid='collection-setup-result']"));
    }

    [Fact]
    public void EachItem_ShowsItsOutcome_ItsValue_AndAFailureItsReason()
    {
        var cut = Open(
            Entry(PipelineSetupCollectionKind.AppEnabled, PipelineSetupCollectionOutcome.Done),
            Entry(PipelineSetupCollectionKind.IngestKey, PipelineSetupCollectionOutcome.AlreadyPresent),
            Entry(PipelineSetupCollectionKind.SiteId, PipelineSetupCollectionOutcome.Done, "atlas"),
            Entry(PipelineSetupCollectionKind.Origins, PipelineSetupCollectionOutcome.Failed, "No production address is known yet"));

        var items = cut.FindAll(Item);
        Assert.Equal(4, items.Count);
        Assert.Equal(["Done", "AlreadyPresent", "Done", "Failed"], items.Select(item => item.GetAttribute("data-outcome")).ToArray());
        Assert.Contains("atlas", items[2].TextContent, StringComparison.Ordinal);
        Assert.Empty(items[0].QuerySelectorAll("[data-testid='collection-setup-detail']"));
        // A failure carries its reason and is never presented with the success badge.
        Assert.Contains("No production address is known yet", items[3].QuerySelector("[data-testid='collection-setup-detail']")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("CollectionSetupFailed", items[3].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectionSetupDone", items[3].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIngestionKey_IsNeverShown_OnlyItsOutcome()
    {
        var cut = Open(Entry(PipelineSetupCollectionKind.IngestKey, PipelineSetupCollectionOutcome.Done));

        var text = cut.Find(Item).TextContent;
        Assert.Contains("CollectionSetupIngestKey", text, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input, textarea"));
    }

    [Fact]
    public async Task Dismissing_HidesTheCard_AndForgetsTheRecord()
    {
        var cut = Open(Entry(PipelineSetupCollectionKind.AppEnabled, PipelineSetupCollectionOutcome.Done));

        await cut.Find("[data-testid='collection-setup-dismiss']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Empty(cut.FindAll("[data-testid='collection-setup-result']"));
        Assert.Null(Services.GetRequiredService<PipelineSetupCollectionState>().For(3));
    }
}
