// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Covers shortened release names, picker rows and the decision to load available
/// releases.</summary>
public sealed class ReleasePickerTests : BunitContext
{
    public ReleasePickerTests() => BunitTestHelper.RegisterServices(this);

    [Theory]
    [InlineData("c-384f63d4a8c182e07de2e9444c25e0a5a9a28e8e", "c-384f63d4")]
    [InlineData("c-01299a281983331e947da3044db2128d9c348641-c338fb59646e", "c-01299a28")]
    [InlineData("1.1.57", "1.1.57")]
    [InlineData("c-384f63", "c-384f63")]
    [InlineData("384f63d4a8c182e07de2e9444c25e0a5a9a28e8e", "384f63d4")]
    [InlineData("", "")]
    public void ShortVersion_ShortensOnlyALongHexId(string version, string expected) =>
        Assert.Equal(expected, ReleaseHelper.ShortVersion(version));

    /// <summary>A published release without a detection date shows its publication, never 01/01/0001.</summary>
    [Fact]
    public void TheCreatedColumn_ShowsThePublicationOfAReleaseNeverDetected()
    {
        var published = new DateTime(2026, 8, 24, 10, 2, 0, DateTimeKind.Utc);
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases,
        [
            new ReleaseDto { Id = 1, Version = "c-9e4c66c2fd1bea865ce71c3b448f68edbd2dee7e", Status = ReleaseStatus.Deployed, PublishedAt = published },
            new ReleaseDto { Id = 2, Version = "1.0.0", Status = ReleaseStatus.Detected }
        ]));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));
        var rows = cut.FindAll("tr[data-omni-row-index]");
        Assert.Contains(published.ToLocalTime().ToString("g"), rows[0].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("0001", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(rows[1].QuerySelectorAll("td"), cell => cell.TextContent.Trim() == "-");
    }

    [Fact]
    public void CreatedAt_IgnoresLocalSentinelDates()
    {
        var sentinel = new DateTime(1, 1, 1, 1, 0, 0, DateTimeKind.Local);
        var detected = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local);

        Assert.Null(ReleaseHelper.CreatedAt(new ReleaseDto
        {
            PublishedAt = sentinel,
            DetectedAt = sentinel
        }));
        Assert.Equal(detected, ReleaseHelper.CreatedAt(new ReleaseDto
        {
            PublishedAt = sentinel,
            DetectedAt = detected
        }));
    }

    [Fact]
    public void CreatedColumn_SortsByTheDateItDisplays_WithoutChangingTheWireContract()
    {
        var unpublished = new ReleaseDto
        {
            Id = 1,
            Version = "unpublished",
            DetectedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc)
        };
        var published = new ReleaseDto
        {
            Id = 2,
            Version = "published",
            PublishedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases, [unpublished, published]));

        cut.Find("th[data-omni-col='CreatedAt'] .omni-data-grid__sort").Click();

        Assert.Contains("published", cut.FindAll("tr[data-omni-row-index]")[0].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatedAt", JsonSerializer.Serialize(unpublished), StringComparison.Ordinal);
    }

    private static List<ReleaseDto> Releases(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new ReleaseDto
        {
            Id = i,
            Version = $"c-{i:D2}4f63d4a8c182e07de2e9444c25e0a5a9a28e8e",
            Status = i == 1 ? ReleaseStatus.Deployed : i == 2 ? ReleaseStatus.Published : ReleaseStatus.Superseded,
            PipelineRunStatus = i == 2 ? PipelineStatus.Failed : PipelineStatus.Success,
            EnvironmentName = i == 1 ? "prod" : null,
            DetectedAt = new DateTime(2026, 9, 10 - (i % 9), 8, 0, 0, DateTimeKind.Utc)
        })];

    /// <summary>The release grid requests blocks of twenty with both publication and run status.
    /// The test host renders a page without virtualization, so scrolling is checked separately by
    /// the markup guard.</summary>
    [Fact]
    public void TheGrid_LoadsReleasesByBlocksOfTwenty_WithTheirStatusAndTheirLastRun()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, Releases(30))
            .Add(grid => grid.TotalCount, 30));

        cut.WaitForAssertion(() => Assert.Equal(20, cut.FindAll(".release-version").Count));
        Assert.Contains(cut.FindAll(".release-version"), version => version.TextContent == "c-014f63d4");
        Assert.Contains("title=\"c-014f63d4a8c182e07de2e9444c25e0a5a9a28e8e\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ReleaseStatusHelp_Superseded", cut.Markup, StringComparison.Ordinal);
        var secondRow = cut.FindAll("tr[data-omni-row-index]")[1];
        Assert.Contains(nameof(PipelineStatus.Failed),
            secondRow.QuerySelector("[data-testid='release-picker-run-status']")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='release-picker-more']"));
        Assert.Empty(cut.FindAll("[data-testid='release-picker-all']"));

        cut.FindAll(".omni-pager__button").Single(button => button.TextContent.Trim() == "›").Click();

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".release-version"), version => version.TextContent == "c-214f63d4"));
        Assert.Equal(10, cut.FindAll(".release-version").Count);
        Assert.Empty(cut.FindAll("[data-testid='release-picker-truncated']"));
    }

    /// <summary>Should a project ever retain more payloads than one page of the API, the grid says it
    /// shows the most recent ones instead of letting the others vanish.</summary>
    [Fact]
    public void MoreRestorableReleasesThanLoaded_AreAnnounced()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, Releases(3))
            .Add(grid => grid.TotalCount, 250));

        Assert.Contains("ReleasePickerTruncated", cut.Find("[data-testid='release-picker-truncated']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ClickingARow_HandsTheFullVersionToTheField()
    {
        string? picked = null;
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, Releases(2))
            .Add(grid => grid.Pick, (string version) => picked = version));

        cut.FindAll("tr[data-omni-row-index]")[1].Click();

        Assert.Equal("c-024f63d4a8c182e07de2e9444c25e0a5a9a28e8e", picked);
    }

    [Fact]
    public void Releases_AreWorthLoading_OnlyForASingleRequiredVersionParameter()
    {
        PipelineRunParameterDto Version(string name) => new() { Name = name, Required = true, Type = "string" };

        Assert.Equal("candidateVersion", RunParameterTargets.ReleaseFillTarget([Version("candidateVersion")]));
        Assert.Null(RunParameterTargets.ReleaseFillTarget([]));
        Assert.Null(RunParameterTargets.ReleaseFillTarget([new PipelineRunParameterDto { Name = "dryRun", Type = "boolean", Required = true }]));
        Assert.Null(RunParameterTargets.ReleaseFillTarget([Version("a"), Version("b")]));
        Assert.Null(RunParameterTargets.ReleaseFillTarget([new PipelineRunParameterDto { Name = "v", Required = true, Default = "x" }]));
    }

    [Fact]
    public void WithNoRestorableRelease_TheGridSaysSo()
    {
        var cut = Render<Aetheus.Front.Components.Pipelines.RunParameterFields>(parameters => parameters
            .Add(fields => fields.Parameters, [new PipelineRunParameterDto { Name = "candidateVersion", Type = "string", Required = true }])
            .Add(fields => fields.AvailableReleases, []));

        Assert.Contains("NoDeployableRelease", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".release-version"));
    }
}
