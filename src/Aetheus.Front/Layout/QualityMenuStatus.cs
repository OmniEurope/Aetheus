// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Layout;

/// <summary>What the Quality entries of the menus say (recette R-6): a grade letter, never a red badge in place of it.
/// Open critical findings are counted in the grade itself (the general Findings line, R-7), so they stay visible
/// through the letter.</summary>
internal static class QualityMenuStatus
{
    /// <summary>R-6: the lowest grade among the projects that have one (the one the main Quality entry shows); null when
    /// no project has a grade yet.</summary>
    public static AnalysisGrade? LowestGrade(IEnumerable<AnalysisPortfolioProjectDto> projects) =>
        projects.Select(project => project.Grade?.OverallGrade).Where(grade => grade.HasValue).Max();

    /// <summary>R-7: the lowest grade among the servers that have one (what the Servers menu shows); null when no server has a
    /// grade yet. Servers only: a project's grade never enters it, nor a server's grade a project's.</summary>
    public static AnalysisGrade? LowestServerGrade(IEnumerable<ServerQualityDto> servers) =>
        servers.Select(server => server.Grade).Where(grade => grade.HasValue).Max();
}
