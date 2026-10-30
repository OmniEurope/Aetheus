// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Releases;
using Bunit;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Releases;

/// <summary>R-78 and R-79 (recette of 2026-10-25): the width of the release tiles and the copy button of the full version.</summary>
public class ReleaseDetailTilesAndCopyTests : BunitContext
{
    private const string FullVersion = "c-4310fadf0613b97ac42977bde708396a3ddca213";

    private readonly BunitTestHelper.TestHandler _handler;

    public ReleaseDetailTilesAndCopyTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
    }

    private static ReleaseDto Sample(bool withArtifact) => new()
    {
        Id = 7,
        ProjectId = 1,
        ProjectName = "W6",
        Version = FullVersion,
        BranchName = "main",
        DetectedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        BuildNumber = 3,
        CommitHash = "4310fadf0613b97ac42977bde708396a3ddca213",
        TagName = "vc-4310fadf",
        Artifacts = withArtifact ? [new ArtifactLinkDto { Id = 41, Name = "release.zip", SizeBytes = 2048 }] : []
    };

    private IRenderedComponent<ReleaseDetail> RenderRelease(bool withArtifact)
    {
        _handler.SetJsonResponse("api/releases/7", Sample(withArtifact));
        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));
        cut.WaitForAssertion(() => Assert.Contains("Source", cut.Markup));
        return cut;
    }

    [Theory]
    [InlineData(true, 4)]
    [InlineData(false, 3)]
    public void R78_TheSourceTile_IsDoubleWidth_InAGridThatFollowsItsOwnWidth(bool withArtifact, int columns)
    {
        var cut = RenderRelease(withArtifact);

        var gridComponent = cut.FindComponent<OmniTileGrid>();
        var grid = gridComponent.Instance;
        Assert.False(string.IsNullOrWhiteSpace(grid.MinColumnWidth));
        Assert.Equal(columns, grid.Columns);
        // Equal heights stay on (R-45): no natural heights.
        Assert.True(grid.EqualHeights);

        var tiles = gridComponent.FindComponents<OmniCard>();
        var source = tiles[0];
        Assert.Contains("Source", source.Markup, StringComparison.Ordinal);
        Assert.True(source.Instance.DoubleWidth);
        Assert.Contains("omni-tile--double", source.Find("section").ClassList);
        Assert.All(tiles.Skip(1), card => Assert.False(card.Instance.DoubleWidth));
    }

    [Fact]
    public void R31_TheHeaderHasNoName_AndTheSourceTileCopiesNameAndTagWithBlueButtons()
    {
        var cut = RenderRelease(withArtifact: false);

        // The name left the page header (title, subtitle and badges).
        var header = cut.Find(".omni-page-header");
        Assert.DoesNotContain(FullVersion, header.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("c-4310fadf", header.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".omni-page-header [data-testid='release-copy-version']"));

        // It is in the Source tile, with a blue Copy button; the tag has one too.
        var source = cut.FindComponents<OmniCard>()[0];
        Assert.Contains(FullVersion, source.Find("[data-testid='release-name']").TextContent, StringComparison.Ordinal);
        var copyName = source.Find("[data-testid='release-copy-version']");
        var copyTag = source.Find("[data-testid='release-copy-tag']");
        foreach (var button in new[] { copyName, copyTag })
        {
            Assert.Contains("omni-button--primary", button.ClassList);
            Assert.Equal("Copy", button.GetAttribute("aria-label"));
        }

        copyName.Click();
        copyTag.Click();

        var written = JSInterop.Invocations["navigator.clipboard.writeText"].Select(i => i.Arguments[0]).ToList();
        Assert.Equal([FullVersion, "vc-4310fadf"], written);
    }
}
