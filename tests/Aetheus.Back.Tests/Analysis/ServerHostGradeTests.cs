// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Analysis;

/// <summary>R-7 (decision of 2026-11-10): a server's grade comes from its host vulnerabilities on the ladder of the projects'
/// Findings line, counting only those with a published fix; a vulnerability with no published fix stays displayed but does not
/// count in the grade at all.</summary>
public sealed class ServerHostGradeTests
{
    private static AnalysisGradeFindingCountsDto Counts(int critical = 0, int high = 0, int medium = 0, int low = 0, int info = 0) =>
        new() { Critical = critical, High = high, Medium = medium, Low = low, Info = info };

    [Theory]
    [InlineData(0, 0, 0, 0, AnalysisGrade.A)]
    [InlineData(0, 0, 1, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 5, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 4, 2, AnalysisGrade.C)]
    [InlineData(0, 1, 0, 0, AnalysisGrade.D)]
    [InlineData(1, 0, 0, 0, AnalysisGrade.F)]
    [InlineData(1, 3, 9, 9, AnalysisGrade.F)]
    public void AVulnerabilityWithAFix_GradesOnTheProjectsLadder(int critical, int high, int medium, int low, AnalysisGrade expected) =>
        Assert.Equal(expected, ServerHostGrade.Of(Counts(critical, high, medium, low), Counts()));

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 0, 9, 9)]
    [InlineData(3, 4, 5, 6)]
    public void VulnerabilitiesWithNoFix_AloneLeaveTheGradeAtA_WhateverTheirSeverityOrNumber(int critical, int high, int medium, int low)
    {
        var all = Counts(critical, high, medium, low);

        Assert.Equal(AnalysisGrade.A, ServerHostGrade.Of(all, all));
    }

    [Fact]
    public void ACriticalWithNoFix_IsAnA_ButTheSameCriticalOnceAFixIsPublishedIsF()
    {
        var critical = Counts(critical: 1);

        Assert.Equal(AnalysisGrade.A, ServerHostGrade.Of(critical, critical));
        Assert.Equal(AnalysisGrade.F, ServerHostGrade.Of(critical, Counts()));
    }

    [Fact]
    public void ANoFixFinding_NeverSoftensNorWorsensTheGradeOfTheFixableOnes()
    {
        // One critical with a fix and one without: the fixable one gives F.
        Assert.Equal(AnalysisGrade.F, ServerHostGrade.Of(Counts(critical: 2), Counts(critical: 1)));
        // A high with a fix beside a critical without one: D, not F.
        Assert.Equal(AnalysisGrade.D, ServerHostGrade.Of(Counts(critical: 1, high: 1), Counts(critical: 1)));
        // Fixable mediums beyond five give C even with a no-fix finding beside them.
        Assert.Equal(AnalysisGrade.C, ServerHostGrade.Of(Counts(medium: 7, critical: 1), Counts(critical: 1)));
    }

    [Fact]
    public void ANoFixFinding_DoesNotCountInTheNumberOfFixableFindings_ThatDecidesBVersusC()
    {
        // Five fixable mediums are a B; a sixth that has no fix cannot make it a C.
        Assert.Equal(AnalysisGrade.B, ServerHostGrade.Of(Counts(medium: 6), Counts(medium: 1)));
        Assert.Equal(AnalysisGrade.C, ServerHostGrade.Of(Counts(medium: 6), Counts()));
    }
}
