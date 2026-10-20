// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared.Maven;

namespace Aetheus.Back.Tests.Maven;

public sealed class MavenVersionSpecTests
{
    private static readonly string[] Available =
    [
        "1.0", "1.1", "1.2-SNAPSHOT", "1.2", "1.5", "2.0-rc1", "2.0", "2.1-SNAPSHOT", "1.10"
    ];

    [Theory]
    [InlineData("RELEASE", "2.0")]
    [InlineData("LATEST", "2.1-SNAPSHOT")]
    [InlineData("[1.0,2.0)", "2.0-rc1")]
    [InlineData("[1.0,1.99]", "1.10")]
    [InlineData("[1.0,1.5]", "1.5")]
    [InlineData("[1.0,1.5)", "1.2")]
    [InlineData("(,1.1]", "1.1")]
    [InlineData("[1.5,)", "2.0")]
    [InlineData("[1.2]", "1.2")]
    [InlineData("[1.0,1.1],[1.5,1.6)", "1.5")]
    [InlineData("(1.0,1.1)", null)]
    [InlineData("[1.2-SNAPSHOT,1.2-SNAPSHOT]", "1.2-SNAPSHOT")]
    [InlineData("[2.0-SNAPSHOT,3.0)", "2.1-SNAPSHOT")]
    [InlineData("1.1", "1.1")]
    [InlineData("1.1.0", "1.1")]
    [InlineData("9.9", null)]
    public void Select_PicksTheHighestMatchingVersion(string spec, string? expected)
        => Assert.Equal(expected, MavenVersionSpec.Parse(spec).Select(Available));

    [Theory]
    [InlineData("[1.0,2.0)", "2.0-rc1", true)]
    [InlineData("[1.0,2.0)", "2.0", false)]
    [InlineData("[1.0,2.0]", "2.0", true)]
    [InlineData("(1.0,2.0)", "1.0", false)]
    [InlineData("[1.0,2.0)", "1.5-SNAPSHOT", false)]
    [InlineData("RELEASE", "1.0-20261017.120000-1", false)]
    [InlineData("LATEST", "1.0-20261017.120000-1", true)]
    public void Matches_FollowsBoundsAndSnapshotRules(string spec, string version, bool expected)
        => Assert.Equal(expected, MavenVersionSpec.Parse(spec).Matches(version));

    [Theory]
    [InlineData("[1.0")]
    [InlineData("1.0]")]
    [InlineData("(1.0)")]
    [InlineData("[2.0,1.0]")]
    [InlineData("[1.0,1.0)")]
    [InlineData("[,1.0]")]
    [InlineData("[1.0,]")]
    [InlineData("[1.0,1.5],[1.2,2.0]")]
    [InlineData("[1.0,1.5],")]
    [InlineData("[1.0,1.5,2.0]")]
    public void Parse_RejectsMalformedRanges(string spec)
        => Assert.Throws<FormatException>(() => MavenVersionSpec.Parse(spec));
}
