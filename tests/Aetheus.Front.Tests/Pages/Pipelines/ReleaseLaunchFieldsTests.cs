// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// What the launch dialog does around the release version (recette R-22, R-25, R-26, R-27): it opens on
/// the latest release worth deploying, warns without blocking about a rejected or grade F release, folds
/// the advanced parameters, tells a missing value under its own field, and keeps its actions at the bottom.
/// </summary>
public sealed class ReleaseLaunchFieldsTests : BunitContext
{
    public ReleaseLaunchFieldsTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static List<PipelineRunParameterDto> VersionParameter() =>
        [new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true }];

    private static ReleaseDto Release(int id, string version, DateTime published, bool? deployable = null,
        AnalysisGrade? grade = null, ReleaseStatus status = ReleaseStatus.Published, PipelineStatus? run = PipelineStatus.Success) =>
        new()
        {
            Id = id,
            Version = version,
            Status = status,
            PublishedAt = published,
            Deployable = deployable,
            AssuranceGrade = grade,
            PipelineRunStatus = run
        };

    private static readonly DateTime Day1 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day3 = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

    private IRenderedComponent<RunParameterFields> RenderFields(
        List<ReleaseDto>? releases, IReadOnlyDictionary<string, string>? prefill = null, List<PipelineRunParameterDto>? parameters = null) =>
        Render<RunParameterFields>(p => p
            .Add(x => x.Parameters, parameters ?? VersionParameter())
            .Add(x => x.AvailableReleases, releases)
            .Add(x => x.Prefill, prefill));

    [Fact]
    public void TheVersionField_OpensOnTheLatestReleaseWorthDeploying()
    {
        var cut = RenderFields([
            Release(1, "1.0.1", Day1, deployable: true),
            Release(2, "1.0.2", Day2, deployable: true),
            Release(3, "1.0.3", Day3, deployable: false)]);

        Assert.Equal("1.0.2", cut.Find("input#candidateVersion").GetAttribute("value"));
        Assert.True(cut.Instance.TryCollect(out var values, out _));
        Assert.Equal("1.0.2", values["candidateVersion"]);
    }

    [Fact]
    public void TheVersionField_StaysEmpty_WhenNoReleaseIsWorthDeploying()
    {
        var cut = RenderFields([Release(1, "1.0.1", Day1, deployable: false)]);

        Assert.Null(cut.Find("input#candidateVersion").GetAttribute("value"));
        Assert.False(cut.Instance.TryCollect(out _, out _));
    }

    [Fact]
    public void ARerunsValue_IsNeverReplacedByTheLatestRelease()
    {
        var cut = RenderFields(
            [Release(1, "1.0.1", Day1), Release(2, "1.0.2", Day2)],
            new Dictionary<string, string> { ["candidateVersion"] = "1.0.1" });

        Assert.Equal("1.0.1", cut.Find("input#candidateVersion").GetAttribute("value"));
    }

    [Fact]
    public void ThePrefilledVersion_RemainsChangeableByClickingAnotherRelease()
    {
        var cut = RenderFields([Release(1, "1.0.1", Day1), Release(2, "1.0.2", Day2)]);

        cut.FindAll("tr[data-omni-row-index]").First(row => row.TextContent.Contains("1.0.1", StringComparison.Ordinal)).Click();

        Assert.Equal("1.0.1", cut.Find("input#candidateVersion").GetAttribute("value"));
    }

    [Theory]
    [InlineData(false, null, "ReleaseGateWarningRejected")]
    [InlineData(true, AnalysisGrade.F, "ReleaseGateWarningGradeF")]
    public void ARejectedOrGradeFRelease_RaisesANonBlockingWarning(bool deployable, AnalysisGrade? grade, string expectedKey)
    {
        var cut = RenderFields([Release(1, "1.0.1", Day1, deployable: deployable, grade: grade)]);
        // Offered by "Tout afficher" only when rejected; chosen by a click here, since it is not the default.
        if (!deployable)
        {
            cut.Find("[data-testid='release-picker-show-all']").Click();
            cut.FindAll("tr[data-omni-row-index]").First().Click();
        }

        Assert.Contains(expectedKey, cut.Find("[data-testid='release-gate-warning']").TextContent, StringComparison.Ordinal);
        // Not blocking: the value is collected and the run can go on.
        Assert.True(cut.Instance.TryCollect(out var values, out _));
        Assert.Equal("1.0.1", values["candidateVersion"]);
    }

    [Fact]
    public void AnAcceptedRelease_RaisesNoWarning()
    {
        var cut = RenderFields([Release(1, "1.0.1", Day1, deployable: true, grade: AnalysisGrade.B)]);

        Assert.Empty(cut.FindAll("[data-testid='release-gate-warning']"));
    }

    [Fact]
    public void AdvancedParameters_AreInAFoldedZoneWithTheirHelp_AndTheOthersStayOutside()
    {
        var parameters = new List<PipelineRunParameterDto>
        {
            new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true },
            new() { Name = "retention", DisplayName = "Retention", Type = "boolean", Advanced = true, Description = "Preview only" }
        };
        var cut = RenderFields(null, parameters: parameters);

        var zone = cut.Find("[data-testid='run-params-advanced']");
        Assert.Contains("RunParametersAdvanced", zone.TextContent, StringComparison.Ordinal);
        var details = zone.TagName.Equals("DETAILS", StringComparison.OrdinalIgnoreCase) ? zone : zone.QuerySelector("details")!;
        Assert.False(details.HasAttribute("open"));
        Assert.NotNull(zone.QuerySelector("#retention"));
        Assert.Equal("Preview only", zone.QuerySelector($"#{RunParameterFields.DescriptionId("retention", "Preview only")}")!.TextContent.Trim());
        Assert.Null(zone.QuerySelector("#candidateVersion"));
        Assert.NotNull(cut.Find("#candidateVersion"));
    }

    [Fact]
    public void WithoutAdvancedParameters_NoAdvancedZoneIsDrawn()
    {
        var cut = RenderFields(null);

        Assert.Empty(cut.FindAll("[data-testid='run-params-advanced']"));
    }

    /// <summary>R-26: the missing value is told under its own field once the user submits, and clears as
    /// soon as the field is edited. Nothing is said before.</summary>
    [Fact]
    public void ARefusedSubmit_TellsTheMissingValueUnderItsField_AndAnEditClearsIt()
    {
        var cut = RenderFields(null);
        Assert.DoesNotContain("RunParameterRequired", cut.Markup, StringComparison.Ordinal);

        Assert.False(cut.Instance.TryCollect(out _, out _));

        cut.WaitForAssertion(() => Assert.Contains("RunParameterRequired", cut.Find("#run-param-candidateVersion-field").TextContent, StringComparison.Ordinal));

        cut.Find("input#candidateVersion").Input("1.0.9");

        cut.WaitForAssertion(() => Assert.DoesNotContain("RunParameterRequired", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.Instance.TryCollect(out var values, out _));
        Assert.Equal("1.0.9", values["candidateVersion"]);
    }

    /// <summary>R-26/R-27: the dialog shows no alert for the refused value (the field says it) and its
    /// actions sit in the footer container the stylesheet pins to the bottom.</summary>
    [Fact]
    public async Task TheParametersDialog_TellsTheErrorUnderTheField_AndPinsItsActions()
    {
        var cut = Render<RunParametersDialog>(p => p.Add(x => x.Parameters, VersionParameter()));
        Assert.NotNull(cut.Find("[data-testid='run-params-footer'] button[type='submit']"));

        await cut.Find("form").SubmitAsync();

        cut.WaitForAssertion(() => Assert.Contains("RunParameterRequired", cut.Find("#run-param-candidateVersion-field").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".omni-alert"));
    }

    [Fact]
    public async Task TheLaunchDialog_TellsTheErrorUnderTheField_AndPinsItsActions()
    {
        var cut = Render<PipelineLaunchDialog>(p => p.Add(x => x.Parameters, VersionParameter()));
        Assert.NotNull(cut.Find("[data-testid='run-params-footer'] button[type='submit']"));

        await cut.Find("form").SubmitAsync();

        cut.WaitForAssertion(() => Assert.Contains("RunParameterRequired", cut.Find("#run-param-candidateVersion-field").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".omni-alert"));
    }
}
