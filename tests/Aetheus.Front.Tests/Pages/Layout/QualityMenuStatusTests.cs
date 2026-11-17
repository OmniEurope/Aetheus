// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;

namespace Aetheus.Front.Tests.Pages;

public sealed class QualityMenuStatusTests
{
    private static AnalysisPortfolioProjectDto Project(AnalysisGrade? grade, int critical = 0) => new()
    {
        CriticalCount = critical,
        Grade = grade is null ? null : new AnalysisGradeSummaryDto { OverallGrade = grade }
    };

    /// <summary>R-6: the main Quality entry reads the lowest (worst) grade of the projects, whatever their critical counts.</summary>
    [Fact]
    public void LowestGrade_IsTheWorstGradeOfTheProjects()
    {
        Assert.Equal(AnalysisGrade.B, QualityMenuStatus.LowestGrade([Project(AnalysisGrade.A), Project(AnalysisGrade.B)]));
        Assert.Equal(AnalysisGrade.F, QualityMenuStatus.LowestGrade([Project(AnalysisGrade.A), Project(AnalysisGrade.F), Project(AnalysisGrade.C)]));
        // A critical finding no longer changes what the entry shows: it sits in the grade itself (R-7).
        Assert.Equal(AnalysisGrade.A, QualityMenuStatus.LowestGrade([Project(AnalysisGrade.A, critical: 3)]));
    }

    /// <summary>R-7: the Servers menu reads the worst grade of the servers; a server without a letter hides none.</summary>
    [Fact]
    public void LowestServerGrade_IsTheWorstGradeOfTheServers_AndNullWithoutAny()
    {
        Assert.Equal(AnalysisGrade.F, QualityMenuStatus.LowestServerGrade([new ServerQualityDto { Grade = AnalysisGrade.A }, new ServerQualityDto { Grade = AnalysisGrade.F }, new ServerQualityDto()]));
        Assert.Equal(AnalysisGrade.B, QualityMenuStatus.LowestServerGrade([new ServerQualityDto(), new ServerQualityDto { Grade = AnalysisGrade.B }]));
        Assert.Null(QualityMenuStatus.LowestServerGrade([]));
        Assert.Null(QualityMenuStatus.LowestServerGrade([new ServerQualityDto()]));
    }

    [Fact]
    public void LowestGrade_IsNull_WhenNoProjectHasAGrade()
    {
        Assert.Null(QualityMenuStatus.LowestGrade([]));
        Assert.Null(QualityMenuStatus.LowestGrade([Project(null), Project(null, critical: 1)]));
        // A project without a grade does not hide the grade of another.
        Assert.Equal(AnalysisGrade.C, QualityMenuStatus.LowestGrade([Project(null), Project(AnalysisGrade.C)]));
    }
}
