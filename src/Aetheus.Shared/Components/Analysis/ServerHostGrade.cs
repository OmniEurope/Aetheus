// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// Recette R-7: the quality grade of a server, from the open vulnerabilities of its host system.
/// It is separate from the projects' grades: a host vulnerability never counts in a project's grade, and a project's
/// findings never count here. The ladder is the one of the projects' Findings line (<see cref="AnalysisFindingsGrade"/>),
/// applied to the vulnerabilities that have a published fix only (decision of 2026-11-10, replacing the cap at B of
/// 2026-11-09): a vulnerability with no fix yet (the vendor published no patched version) stays displayed and counted as
/// information, but nobody can fix it, so it does not count in the grade at all.
/// </summary>
public static class ServerHostGrade
{
    /// <param name="open">Every open vulnerability of the server, by severity.</param>
    /// <param name="withoutFix">Those of them with no published fix, by severity (a subset of <paramref name="open"/>); they
    /// are left out of the grade.</param>
    public static AnalysisGrade Of(AnalysisGradeFindingCountsDto open, AnalysisGradeFindingCountsDto withoutFix)
    {
        return AnalysisFindingsGrade.Of(ServerHostFixability.Fixable(open, withoutFix));
    }
}
