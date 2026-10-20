// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared.Maven;

namespace Aetheus.Back.Tests.Maven;

/// <summary>
/// Maven's version ordering, checked against the vectors of Maven's own
/// <c>ComparableVersionTest</c>: each list is strictly increasing, and every pair inside it is
/// compared both ways.
/// </summary>
public sealed class MavenComparableVersionTests
{
    private static readonly string[] Qualifiers =
    [
        "1-alpha2snapshot", "1-alpha2", "1-alpha-123", "1-beta-2", "1-beta123", "1-m2", "1-m11", "1-rc", "1-cr2",
        "1-rc123", "1-SNAPSHOT", "1", "1-sp", "1-sp2", "1-sp123", "1-abc", "1-def", "1-pom-1", "1-1-snapshot",
        "1-1", "1-2", "1-123"
    ];

    private static readonly string[] Numbers =
    [
        "2.0", "2-1", "2.0.a", "2.0.0.a", "2.0.2", "2.0.123", "2.1.0", "2.1-a", "2.1b", "2.1-c", "2.1-1", "2.1.0.1",
        "2.2", "2.123", "11.a2", "11.a11", "11.b2", "11.b11", "11.m2", "11.m11", "11", "11.a", "11b", "11c", "11m"
    ];

    public static TheoryData<string> Lists => new() { "qualifiers", "numbers" };

    [Theory]
    [MemberData(nameof(Lists))]
    public void EveryListIsStrictlyIncreasing(string list)
    {
        var versions = list == "qualifiers" ? Qualifiers : Numbers;
        for (var i = 0; i < versions.Length; i++)
        {
            for (var j = i + 1; j < versions.Length; j++)
            {
                Assert.True(MavenComparableVersion.Compare(versions[i], versions[j]) < 0,
                    $"{versions[i]} should be lower than {versions[j]}");
                Assert.True(MavenComparableVersion.Compare(versions[j], versions[i]) > 0,
                    $"{versions[j]} should be greater than {versions[i]}");
            }
        }
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1", "1.0.0")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1", "1-0")]
    [InlineData("1", "1.0-0")]
    [InlineData("1a", "1-a")]
    [InlineData("1a", "1.0-a")]
    [InlineData("1a", "1.0.0-a")]
    [InlineData("1.0a", "1-a")]
    [InlineData("1x", "1-x")]
    [InlineData("1ga", "1")]
    [InlineData("1release", "1")]
    [InlineData("1final", "1")]
    [InlineData("1cr", "1rc")]
    [InlineData("1a1", "1-alpha-1")]
    [InlineData("1b2", "1-beta-2")]
    [InlineData("1m3", "1-milestone-3")]
    [InlineData("1X", "1x")]
    [InlineData("1A", "1a")]
    [InlineData("1-SNAPSHOT", "1-snapshot")]
    [InlineData("1.0.0-GA", "1")]
    public void EquivalentSpellingsAreEqual(string left, string right)
    {
        Assert.Equal(0, MavenComparableVersion.Compare(left, right));
        Assert.Equal(new MavenComparableVersion(left), new MavenComparableVersion(right));
        Assert.Equal(new MavenComparableVersion(left).GetHashCode(), new MavenComparableVersion(right).GetHashCode());
    }

    [Theory]
    [InlineData("1.10", "1.9")]
    [InlineData("1.0.0", "1.0.0-SNAPSHOT")]
    [InlineData("1.0.0", "1.0.0-rc1")]
    [InlineData("1.0.0-rc2", "1.0.0-rc1")]
    [InlineData("10", "9.99")]
    [InlineData("1.0.1", "1.0")]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567889")]
    public void NumbersCompareNumericallyAndReleasesBeatTheirQualifiers(string greater, string lower)
        => Assert.True(MavenComparableVersion.Compare(greater, lower) > 0, $"{greater} > {lower}");

    [Fact]
    public void Ordering_SortsLikeMaven()
    {
        var sorted = new[] { "1.10", "1.0-SNAPSHOT", "1.9", "1.0", "1.0-beta-1", "1.0-alpha" }
            .OrderBy(version => version, MavenComparableVersion.Ordering)
            .ToArray();
        Assert.Equal(["1.0-alpha", "1.0-beta-1", "1.0-SNAPSHOT", "1.0", "1.9", "1.10"], sorted);
    }
}
