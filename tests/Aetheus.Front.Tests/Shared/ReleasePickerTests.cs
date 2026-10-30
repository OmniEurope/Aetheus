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

        ShowAll(cut);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));
        var rows = cut.FindAll("tr[data-omni-row-index]");
        Assert.Contains(published.ToLocalTime().ToString("g"), rows[0].TextContent, StringComparison.Ordinal);
        // The sentinel date is never shown in a row; the grid's date filter input carries min="0001-01-01" by design.
        Assert.DoesNotContain("0001", string.Concat(rows.Select(row => row.TextContent)), StringComparison.Ordinal);
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

        ShowAll(cut);
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

        ShowAll(cut);
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

        ShowAll(cut);
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

    /// <summary>Recette R-76: a server name is never a version, so the production server parameter every deployment
    /// pipeline also asks for does not make the version ambiguous. Two other required texts still do.</summary>
    [Fact]
    public void TheProductionServerParameter_NeverCompetesWithTheVersionForTheReleasePick()
    {
        PipelineRunParameterDto Required(string name) => new() { Name = name, Required = true, Type = "string" };

        Assert.Equal("candidateVersion", RunParameterTargets.ReleaseFillTarget([Required("candidateVersion"), Required("deployServer")]));
        Assert.Equal("candidateVersion", RunParameterTargets.ReleaseFillTarget([Required("deployServer"), Required("candidateVersion")]));
        Assert.Null(RunParameterTargets.ReleaseFillTarget([Required("deployServer")]));
        Assert.Null(RunParameterTargets.ReleaseFillTarget([Required("candidateVersion"), Required("deployServer"), Required("reason")]));
    }

    private static void ShowAll(IRenderedComponent<ReleasePickerGrid> cut) =>
        cut.Find("[data-testid='release-picker-show-all']").Click();

    // One release of each kind the default list must keep or leave out (recette R-22).
    private static List<ReleaseDto> MixedReleases() =>
    [
        new() { Id = 1, Version = "1.0.5", Status = ReleaseStatus.Published, PipelineRunStatus = PipelineStatus.Success, Deployable = true, AssuranceGrade = AnalysisGrade.A },
        new() { Id = 2, Version = "1.0.4", Status = ReleaseStatus.Published, PipelineRunStatus = PipelineStatus.Success, Deployable = false, AssuranceGrade = AnalysisGrade.C },
        new() { Id = 3, Version = "1.0.3", Status = ReleaseStatus.Detected, PipelineRunStatus = PipelineStatus.Success },
        new() { Id = 4, Version = "1.0.2", Status = ReleaseStatus.Published, PipelineRunStatus = PipelineStatus.Failed },
        new() { Id = 5, Version = "1.0.1", Status = ReleaseStatus.Deployed, Deployable = true, AssuranceGrade = AnalysisGrade.F }
    ];

    /// <summary>R-22: by default only the published releases whose run did not fail and that the Gate did
    /// not refuse are listed; the others wait behind "Tout afficher", and a second click brings the short
    /// list back.</summary>
    [Fact]
    public void ByDefault_OnlyPublishedSuccessfulReleasesAreListed_AndShowAllListsTheOthers()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases, MixedReleases()));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));
        var versions = cut.FindAll("tr[data-omni-row-index]").Select(row => row.TextContent).ToList();
        Assert.Contains(versions, text => text.Contains("1.0.5", StringComparison.Ordinal));
        Assert.Contains(versions, text => text.Contains("1.0.1", StringComparison.Ordinal));
        Assert.Contains("ReleasePickerHidden", cut.Find("[data-testid='release-picker-hidden']").TextContent, StringComparison.Ordinal);
        Assert.Contains("ReleasePickerShowAll", cut.Find("[data-testid='release-picker-show-all']").TextContent, StringComparison.Ordinal);

        ShowAll(cut);

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("tr[data-omni-row-index]").Count));
        Assert.Contains("ReleasePickerShowPublished", cut.Find("[data-testid='release-picker-show-all']").TextContent, StringComparison.Ordinal);

        ShowAll(cut);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));
    }

    [Fact]
    public void WhenEveryReleaseIsListedAlready_NoShowAllButtonIsOffered()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases,
            [new ReleaseDto { Id = 1, Version = "1.0.5", Status = ReleaseStatus.Published }]));

        Assert.Empty(cut.FindAll("[data-testid='release-picker-show-all']"));
    }

    /// <summary>R-22: each release says what the Gate decided and with which grade.</summary>
    [Fact]
    public void TheGateColumn_ShowsTheVerdictAndTheGradeOfEachRelease()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases, MixedReleases()));
        ShowAll(cut);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("tr[data-omni-row-index]").Count));

        var rows = cut.FindAll("tr[data-omni-row-index]");
        var accepted = rows.Single(row => row.TextContent.Contains("1.0.5", StringComparison.Ordinal));
        Assert.Contains("ReleaseGateAccepted", accepted.QuerySelector("[data-testid='release-gate']")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("A", accepted.QuerySelector(".grade-badge")!.TextContent, StringComparison.Ordinal);
        var rejected = rows.Single(row => row.TextContent.Contains("1.0.4", StringComparison.Ordinal));
        Assert.Contains("ReleaseGateRejected", rejected.QuerySelector("[data-testid='release-gate']")!.TextContent, StringComparison.Ordinal);
        var unqualified = rows.Single(row => row.TextContent.Contains("1.0.3", StringComparison.Ordinal));
        Assert.Contains("ReleaseNotQualified", unqualified.QuerySelector("[data-testid='release-gate']")!.TextContent, StringComparison.Ordinal);
        // R-59: "Not qualified" is a text badge, so it carries its icon.
        Assert.NotNull(unqualified.QuerySelector(".omni-badge[data-testid='release-not-qualified'] .omni-icon"));
    }

    /// <summary>R-27: below 40rem the same releases are one card each (version, date, status, verdict), as
    /// OE selectable cards; the card of the field's version is the chosen one and a click hands the full
    /// version over, like a row.</summary>
    [Fact]
    public void TheCardList_HasOneCardPerListedRelease_AndPicksLikeARow()
    {
        string? picked = null;
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, MixedReleases())
            .Add(grid => grid.SelectedVersion, "1.0.1")
            .Add(grid => grid.Pick, (string version) => picked = version));

        var cards = cut.FindAll("[data-testid='release-picker-card']");
        Assert.Equal(2, cards.Count);
        var first = cards.Single(card => card.TextContent.Contains("1.0.5", StringComparison.Ordinal));
        Assert.Contains("ReleaseGateAccepted", first.TextContent, StringComparison.Ordinal);
        Assert.Contains("ReleaseStatus", first.InnerHtml, StringComparison.Ordinal);
        var selected = cards.Single(card => card.TextContent.Contains("1.0.1", StringComparison.Ordinal));
        Assert.Equal("true", selected.GetAttribute("aria-checked"));

        first.Click();

        Assert.Equal("1.0.5", picked);
    }

    private static IReadOnlyList<string> SelectedRows(IRenderedComponent<ReleasePickerGrid> cut) =>
        [.. cut.FindAll("tr[data-omni-row-index]")
            .Where(row => row.GetAttribute("aria-selected") == "true")
            .Select(row => row.TextContent)];

    /// <summary>R-58: the table marks the row of the version already in the field as selected when the
    /// dialog opens, like the card list does.</summary>
    [Fact]
    public void ThePrefilledVersion_HasItsRowSelectedInTheTable()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, MixedReleases())
            .Add(grid => grid.SelectedVersion, "1.0.1")
            .Add(grid => grid.Pick, (string _) => { }));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));
        var selected = Assert.Single(SelectedRows(cut));
        Assert.Contains("1.0.1", selected, StringComparison.Ordinal);
        Assert.Contains("omni-data-grid__row--selected", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>R-58: a clicked row stays selected, and only that one: the field's version comes back as the
    /// grid's <c>SelectedVersion</c>, as it does in the page.</summary>
    [Fact]
    public void AClickedRow_StaysSelected_AndTheFormerOneIsReleased()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, MixedReleases())
            .Add(grid => grid.SelectedVersion, "1.0.1")
            .Add(grid => grid.Pick, (string version) => { }));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));

        cut.FindAll("tr[data-omni-row-index]").Single(row => row.TextContent.Contains("1.0.5", StringComparison.Ordinal)).Click();
        cut.Render(parameters => parameters.Add(grid => grid.SelectedVersion, "1.0.5"));

        var selected = Assert.Single(SelectedRows(cut));
        Assert.Contains("1.0.5", selected, StringComparison.Ordinal);
    }

    /// <summary>Clicking the row that is already chosen must not leave the field with a version no row shows.</summary>
    [Fact]
    public void ClickingTheChosenRowAgain_KeepsItSelected()
    {
        var picks = new List<string>();
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, MixedReleases())
            .Add(grid => grid.SelectedVersion, "1.0.1")
            .Add(grid => grid.Pick, (string version) => picks.Add(version)));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));

        cut.FindAll("tr[data-omni-row-index]").Single(row => row.TextContent.Contains("1.0.1", StringComparison.Ordinal)).Click();

        Assert.Contains("1.0.1", Assert.Single(SelectedRows(cut)), StringComparison.Ordinal);
        Assert.Empty(picks);
    }

    /// <summary>With no field to fill the rows are not selectable: no checkbox column, nothing to pick.</summary>
    [Fact]
    public void WithoutAPickTarget_TheTableOffersNoSelection()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases, MixedReleases()));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr[data-omni-row-index]").Count));
        Assert.Empty(cut.FindAll("tr[data-omni-row-index] input[type='checkbox']"));
    }

    /// <summary>R-57: "Tout afficher" is the user's explicit exception, a blue (Primary) button.</summary>
    [Fact]
    public void TheShowAllButton_IsBlue()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases, MixedReleases()));

        Assert.Contains("omni-button--primary", cut.Find("[data-testid='release-picker-show-all']").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardList_FollowsShowAll()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases, MixedReleases()));
        Assert.Equal(2, cut.FindAll("[data-testid='release-picker-card']").Count);

        ShowAll(cut);

        Assert.Equal(5, cut.FindAll("[data-testid='release-picker-card']").Count);
    }

    [Fact]
    public void TheVisibilityRules_KeepWhatIsPublishedSuccessfulAndNotRejected()
    {
        var all = MixedReleases();

        Assert.Equal([1, 5], all.Where(ReleasePickerVisibility.IsDefaultVisible).Select(release => release.Id));
        Assert.Equal(ReleaseGateConcern.Rejected, ReleasePickerVisibility.ConcernOf(all[1]));
        Assert.Equal(ReleaseGateConcern.GradeF, ReleasePickerVisibility.ConcernOf(all[4]));
        Assert.Equal(ReleaseGateConcern.None, ReleasePickerVisibility.ConcernOf(all[0]));
        Assert.Equal(ReleaseGateConcern.None, ReleasePickerVisibility.ConcernOf(all[2]));
    }

    [Fact]
    public void TheLatestRelease_IsTheMostRecentOneOfferedByDefault_OrNone()
    {
        var older = new ReleaseDto { Id = 1, Version = "1.0.1", Status = ReleaseStatus.Published, PublishedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) };
        var newer = new ReleaseDto { Id = 2, Version = "1.0.2", Status = ReleaseStatus.Published, PublishedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) };
        var rejectedNewest = new ReleaseDto { Id = 3, Version = "1.0.3", Status = ReleaseStatus.Published, PublishedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), Deployable = false };

        Assert.Equal("1.0.2", ReleasePickerVisibility.Latest([older, rejectedNewest, newer])!.Version);
        Assert.Null(ReleasePickerVisibility.Latest([rejectedNewest]));
        Assert.Null(ReleasePickerVisibility.Latest([]));
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
