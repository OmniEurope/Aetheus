// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The ordering the CVE check decides by: a runtime is exposed to a CVE exactly when the release that fixes it is
/// greater than the installed one, so patch numbers compare as numbers (10.0.9 before 10.0.14) and a prerelease sorts
/// before the release it leads to.</summary>
public sealed class DotnetVersionTests
{
    private static DotnetVersion V(string text) => DotnetVersion.TryParse(text)!;

    [Theory]
    [InlineData("10.0.9", "10.0.14")]
    [InlineData("10.0.13", "10.0.14")]
    [InlineData("9.0.99", "10.0.0")]
    [InlineData("11.0.0-rc.1", "11.0.0-rc.2")]
    [InlineData("11.0.0-rc.2", "11.0.0")]
    [InlineData("11.0.0-preview.9", "11.0.0-preview.10")]
    [InlineData("11.0.0-preview.1", "11.0.0-rc.1")]
    public void Older_SortsBeforeNewer(string older, string newer)
    {
        Assert.True(V(older) < V(newer));
        Assert.True(V(newer) > V(older));
        Assert.Equal(0, V(older).CompareTo(V(older)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0")]
    [InlineData("10.0.x")]
    [InlineData("10.0.13.1")]
    [InlineData("10.0.13-")]
    [InlineData("-10.0.13")]
    [InlineData("10..13")]
    [InlineData("99999999999.0.0")]
    public void NotAVersion_IsRefused(string? text) => Assert.Null(DotnetVersion.TryParse(text));

    [Fact]
    public void Channel_IsMajorDotMinor_AndRoundTrips()
    {
        Assert.Equal("10.0", V("10.0.13").Channel);
        Assert.Equal("11.0.0-rc.1", V("11.0.0-rc.1").ToString());
    }

    [Fact]
    public void Advisories_AreReadFromTheOldestReleaseThatListsTheCve()
    {
        var advisories = DotnetReleaseAdvisories.Affecting(V("10.0.12"), DotnetReleaseMetadataFixture.Channel10);

        // Listed by 10.0.13 and again by 10.0.14: fixed in 10.0.13, and the move target is the latest security release.
        var repeated = Assert.Single(advisories, advisory => advisory.CveId == "CVE-2026-70197");
        Assert.Equal(V("10.0.13"), repeated.FixedIn);
        Assert.Equal(V("10.0.14"), repeated.LatestSecurity);
        var introduced = Assert.Single(advisories, advisory => advisory.CveId == "CVE-2026-111497");
        Assert.Equal(V("10.0.14"), introduced.FixedIn);
        Assert.Equal("2026-10-25", introduced.FixedOn);
    }

    [Fact]
    public void RuntimeNewerThanEveryKnownRelease_HasNoAdvisory() =>
        Assert.Empty(DotnetReleaseAdvisories.Affecting(V("10.0.15"), DotnetReleaseMetadataFixture.Channel10));
}
