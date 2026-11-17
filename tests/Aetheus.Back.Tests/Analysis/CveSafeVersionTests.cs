// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// The fixes an advisory states and the latest version no known advisory affects. The ranges are written the way
/// OSV publishes them (events in a SEMVER or ECOSYSTEM range); versions are ordered by their ecosystem, never as text.
/// </summary>
public sealed class CveSafeVersionTests
{
    private static AdvisoryRanges Ranges(string json, string? versions = null) => new(json, versions);

    // [0, 1.5.1) and [2.0.0, 2.3.0): two release branches, two fixes.
    private static readonly AdvisoryRanges TwoBranches = Ranges(
        """[{"type":"SEMVER","events":[{"introduced":"0"},{"fixed":"1.5.1"}]},{"type":"SEMVER","events":[{"introduced":"2.0.0"},{"fixed":"2.3.0"}]}]""");

    [Theory]
    [InlineData("1.4.0", "1.5.1,2.3.0")]
    [InlineData("1.5.0", "1.5.1,2.3.0")]
    [InlineData("2.1.0", "2.3.0")]
    [InlineData("1.5.1", "2.3.0")]
    [InlineData("2.3.0", "")]
    [InlineData("9.0.0", "")]
    public void FixesAbove_AreTheFixedEventsAboveTheCurrentVersion(string current, string expected) =>
        Assert.Equal(expected.Length == 0 ? Array.Empty<string>() : expected.Split(','), CveAdvisoryFixes.FixesAbove("npm", current, TwoBranches));

    [Fact]
    public void FixesAbove_NeverOffersAFixThatAnotherRangeOfTheSameAdvisoryStillAffects()
    {
        // 2.0.0 closes the first range but sits inside [1.0.0, 3.0.0): only 3.0.0 is safe.
        var advisory = Ranges(
            """[{"type":"SEMVER","events":[{"introduced":"0"},{"fixed":"2.0.0"}]},{"type":"SEMVER","events":[{"introduced":"1.0.0"},{"fixed":"3.0.0"}]}]""");

        Assert.Equal(["3.0.0"], CveAdvisoryFixes.FixesAbove("npm", "0.5.0", advisory));
    }

    [Fact]
    public void FixesAbove_OrdersMajorsAsNumbers() =>
        Assert.Equal(["10.0.0"], CveAdvisoryFixes.FixesAbove(
            "NuGet", "9.9.9", Ranges("""[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"10.0.0"}]}]""")));

    [Theory]
    [InlineData("""[{"type":"SEMVER","events":[{"introduced":"0"},{"last_affected":"1.2.0"}]}]""")]
    [InlineData("""[{"type":"GIT","events":[{"introduced":"0"},{"fixed":"abcdef0123456789abcdef0123456789abcdef01"}]}]""")]
    [InlineData("not json")]
    [InlineData("""{"type":"SEMVER"}""")]
    [InlineData("""[{"type":"SEMVER","events":[{"introduced":"0"},{"fixed":"???"}]}]""")]
    [InlineData("[]")]
    public void FixesAbove_NothingReadableOrFixed_IsEmpty(string ranges) =>
        Assert.Empty(CveAdvisoryFixes.FixesAbove("npm", "1.0.0", Ranges(ranges)));

    [Fact]
    public void FixesAbove_AdvisoryWithOnlyAVersionList_StatesNoFix() =>
        Assert.Empty(CveAdvisoryFixes.FixesAbove("npm", "1.0.0", new AdvisoryRanges(null, """["1.0.0","1.0.1"]""")));

    [Fact]
    public void FixesAbove_ACurrentVersionNobodyCanRead_IsEmpty() =>
        Assert.Empty(CveAdvisoryFixes.FixesAbove("npm", "not-a-version", TwoBranches));

    [Fact]
    public void FixesAbove_InAnEcosystemWithoutAnOrderOfVersions_IsEmpty() =>
        Assert.Empty(CveAdvisoryFixes.FixesAbove("Hex", "1.0.0", TwoBranches));

    // Fixed in 0.0.1: it affects none of the versions the tests publish.
    private static readonly AdvisoryRanges Harmless = Ranges(
        """[{"type":"SEMVER","events":[{"introduced":"0"},{"fixed":"0.0.1"}]}]""");

    private static readonly string[] Published = ["1.4.0", "1.5.0", "1.5.1", "1.6.0", "2.0.0", "2.2.0", "2.3.0", "3.0.0-beta.1"];

    [Fact]
    public void Select_IsTheNewestPublishedVersionNoAdvisoryAffects_AndTheNewestOfTheSameMajor()
    {
        var safe = CveSafeVersionSelector.Select("npm", "1.4.0", Published, [TwoBranches]);

        Assert.Equal("1.6.0", safe.SameMajor);
        Assert.Equal("2.3.0", safe.Newest);
    }

    [Fact]
    public void Select_NeverOffersAPreRelease_WhenTheProjectIsOnAStableVersion()
    {
        var safe = CveSafeVersionSelector.Select("npm", "2.1.0", Published, [TwoBranches]);

        Assert.Equal("2.3.0", safe.SameMajor);
        Assert.Equal("2.3.0", safe.Newest);
    }

    [Fact]
    public void Select_OffersPreReleases_WhenTheProjectIsAlreadyOnOne()
    {
        var advisory = Ranges("""[{"type":"SEMVER","events":[{"introduced":"0"},{"fixed":"3.0.0-beta.2"}]}]""");
        string[] published = ["2.3.0", "3.0.0-beta.1", "3.0.0-beta.2", "3.0.0-beta.3"];

        var safe = CveSafeVersionSelector.Select("npm", "3.0.0-beta.1", published, [advisory]);

        Assert.Equal("3.0.0-beta.3", safe.SameMajor);
        Assert.Equal("3.0.0-beta.3", safe.Newest);
    }

    [Fact]
    public void Select_ASecondAdvisoryThatAffectsALaterVersion_RemovesIt()
    {
        var later = Ranges("""[{"type":"SEMVER","events":[{"introduced":"1.6.0"},{"fixed":"1.7.0"}]}]""");

        var safe = CveSafeVersionSelector.Select("npm", "1.4.0", Published, [TwoBranches, later]);

        Assert.Equal("1.5.1", safe.SameMajor);
        Assert.Equal("2.3.0", safe.Newest);
    }

    [Fact]
    public void Select_AVersionTheAdvisoryCannotBeEvaluatedOn_IsNotSafe()
    {
        // The ranges of the second advisory cannot be read: no version can be said to be free of it.
        var safe = CveSafeVersionSelector.Select("npm", "1.4.0", Published, [TwoBranches, Ranges("not json")]);

        Assert.Null(safe.SameMajor);
        Assert.Null(safe.Newest);
    }

    [Fact]
    public void Select_OnlyVersionsAboveTheCurrentOneAreOffered()
    {
        var safe = CveSafeVersionSelector.Select("npm", "2.3.0", Published, [Harmless]);

        Assert.Null(safe.SameMajor);
        Assert.Null(safe.Newest);
    }

    [Fact]
    public void Select_ComparesNumbersNotText()
    {
        string[] published = ["9.9.9", "10.0.0", "10.0.10", "10.0.9"];
        var advisory = Ranges("""[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"9.9.9"}]}]""");

        var safe = CveSafeVersionSelector.Select("NuGet", "9.0.0", published, [advisory]);

        Assert.Equal("9.9.9", safe.SameMajor);
        Assert.Equal("10.0.10", safe.Newest);
    }

    [Fact]
    public void Select_ListedVersionsOfAnAdvisoryAreAffected_EvenWithoutARange()
    {
        var listed = new AdvisoryRanges(null, """["1.5.0","1.5.1"]""");

        var safe = CveSafeVersionSelector.Select("npm", "1.4.0", Published, [listed]);

        Assert.Equal("1.6.0", safe.SameMajor);
    }

    [Fact]
    public void Select_VersionsTheComparatorCannotReadAreNeverOffered()
    {
        string[] published = ["1.5.0", "banana", "1.5.1"];

        var safe = CveSafeVersionSelector.Select("npm", "1.4.0", published, [Harmless]);

        Assert.Equal("1.5.1", safe.Newest);
    }

    [Fact]
    public void Select_ForAnEcosystemWithoutAnOrderOfVersions_OffersNothing()
    {
        var safe = CveSafeVersionSelector.Select("Hex", "1.4.0", Published, [Harmless]);

        Assert.Null(safe.Newest);
        Assert.Null(safe.SameMajor);
    }

    [Fact]
    public void Select_ComposerOrderPutsAnRcBelowItsRelease_AndASecondMajorIsAnotherMajor()
    {
        string[] published = ["1.27.1", "3.0.0-RC1", "3.0.0", "3.0.2", "2.9.0"];
        var advisory = Ranges("""[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"1.27.1"}]}]""");

        var safe = CveSafeVersionSelector.Select("Packagist", "1.27.0", published, [advisory]);

        Assert.Equal("1.27.1", safe.SameMajor);
        Assert.Equal("3.0.2", safe.Newest);
    }
}
