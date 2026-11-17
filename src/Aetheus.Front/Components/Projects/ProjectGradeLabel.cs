// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// R-12, R-20: names what a project's overall grade is from, for the tooltip of its badge (project list,
/// overview) and the sentence of the Quality page. The letter is the sealed verdict of the release deployed in
/// production; when there is no such letter, the sentence says why instead of showing a fake A.
/// </summary>
internal static class ProjectGradeLabel
{
    /// <summary>True when the letter is the last known verdict of an earlier release, not the deployed
    /// release's own grade (user option B of R-12, R-20): the badge then carries the "old" mark.</summary>
    internal static bool IsOld(AnalysisQualificationDto? qualification) =>
        qualification is { State: AnalysisQualificationState.DeployedLastKnownGrade };

    internal static string For(IStringLocalizer<AppStrings> localizer, AnalysisQualificationDto? qualification)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (qualification is null) return localizer["AnalysisGlobalGrade"];

        var release = ShortId.Shorten(qualification.ReleaseVersion);
        var run = qualification.PipelineRunNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
        return qualification.State switch
        {
            AnalysisQualificationState.NothingDeployed => localizer["ProjectGradeNothingDeployed"],
            AnalysisQualificationState.DeployedNotDeployable => string.Format(
                System.Globalization.CultureInfo.CurrentCulture, localizer["ProjectGradeDeployedNotDeployable"], release, run),
            AnalysisQualificationState.DeployedLastKnownGrade => string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                localizer["ProjectGradeLastKnown"],
                release,
                qualification.KnownReleaseVersion is { Length: > 0 } known ? ShortId.Shorten(known) : "-",
                run,
                qualification.KnownAt?.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture) ?? localizer["NotAvailable"].Value),
            AnalysisQualificationState.DeployedSealed => string.Format(
                System.Globalization.CultureInfo.CurrentCulture, localizer["ProjectGradeDeployedRelease"], release, run),
            _ => string.Format(
                System.Globalization.CultureInfo.CurrentCulture, localizer["ProjectGradeDeployedWithoutVerdict"], release)
        };
    }
}
