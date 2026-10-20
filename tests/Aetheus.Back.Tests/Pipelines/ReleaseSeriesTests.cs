// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The version series is the <c>major.minor</c> of the code's single <c>VersionPrefix</c>, read
/// under the same rule as the app-version function so the server counts a release in the series that
/// function composes the version from.</summary>
public sealed class ReleaseSeriesTests
{
    [Theory]
    [InlineData("<Project><PropertyGroup><VersionPrefix>1.4.0</VersionPrefix></PropertyGroup></Project>", "1.4")]
    [InlineData("<Project>\r\n  <VersionPrefix>2.10</VersionPrefix>\r\n</Project>", "2.10")]
    [InlineData("<VersionPrefix>1.2</VersionPrefix><VersionPrefix>3.4</VersionPrefix>", "3.4")]
    public void FromVersionProps_IsTheMajorAndMinorOfTheVersionPrefix(string props, string expected) =>
        Assert.Equal(expected, ReleaseSeries.FromVersionProps(props));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<Project />")]
    [InlineData("<VersionPrefix>1</VersionPrefix>")]
    [InlineData("<VersionPrefix>1.4</VersionPrefix>\n<VersionPrefix>1.5</VersionPrefix>")]
    public void FromVersionProps_WithoutOneReadablePrefix_IsNoSeries(string? props) =>
        Assert.Equal(ReleaseSeries.None, ReleaseSeries.FromVersionProps(props));

    [Theory]
    [InlineData("1.4", true)]
    [InlineData("12.345", true)]
    [InlineData("1.4.0", false)]
    [InlineData("1", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_AcceptsOnlyMajorDotMinor(string? series, bool expected) =>
        Assert.Equal(expected, ReleaseSeries.IsValid(series));
}
