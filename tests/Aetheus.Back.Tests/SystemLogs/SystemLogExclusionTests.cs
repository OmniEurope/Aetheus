// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.SystemLogs;

namespace Aetheus.Back.Tests.SystemLogs;

/// <summary>R-28: which tab of the logs page an entry belongs to.</summary>
public class SystemLogExclusionTests
{
    private static SystemLogEntryDto Entry(string message, string category = "Aetheus.Back.Services.Foo") =>
        new(1, DateTime.UtcNow, "Warning", category, message, null, null);

    private static SystemLogExclusionRuleDto Rule(string pattern, string category = "", bool enabled = true) =>
        new(1, 7, pattern, category, enabled, "admin", DateTime.UtcNow);

    [Fact]
    public void ARule_MatchesTheMessagesThatContainItsPattern_IgnoringCase() =>
        Assert.True(SystemLogExclusion.Matches(Entry("Slow request POST /api/tasks/claim: 1628 ms"), Rule("slow request post /api/tasks/claim")));

    [Fact]
    public void ARule_DoesNotMatchAnotherMessage() =>
        Assert.False(SystemLogExclusion.Matches(Entry("Acquired lease"), Rule("Slow request")));

    [Fact]
    public void ADisabledRule_MatchesNothing_SoNothingLeavesTheFirstTab() =>
        Assert.False(SystemLogExclusion.Matches(Entry("Slow request"), Rule("Slow request", enabled: false)));

    [Fact]
    public void ARuleWithACategory_OnlyMatchesThatCategory()
    {
        var rule = Rule("failed", "PostgresLeaderLease");

        Assert.True(SystemLogExclusion.Matches(Entry("worker failed", "Aetheus.Back.Services.PostgresLeaderLease"), rule));
        Assert.False(SystemLogExclusion.Matches(Entry("worker failed", "Aetheus.Back.Services.Other"), rule));
    }

    [Fact]
    public void MatchesAny_IsTrueWhenOneRuleOfTheListMatches() =>
        Assert.True(SystemLogExclusion.MatchesAny(Entry("Slow request"), [Rule("nothing"), Rule("Slow")]));

    [Fact]
    public void MatchesAny_IsFalseWithoutRules() =>
        Assert.False(SystemLogExclusion.MatchesAny(Entry("Slow request"), []));
}
