// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the Gate said about a release, when it said something that deserves a warning.</summary>
internal enum ReleaseGateConcern
{
    None,
    /// <summary>The Gate refused the release (its sealed verdict is "not deployable").</summary>
    Rejected,
    /// <summary>The release graded F.</summary>
    GradeF
}

/// <summary>
/// Which releases the launch dialog offers first, and which of them it warns about.
///
/// "Published" and "Success" describe the build, not the quality: a release the Gate rejected is both.
/// The list therefore leaves out, by default, what is not published, failed or refused; "Tout afficher"
/// brings it all back, because any release must remain deployable from the dialog (the user's choice).
/// </summary>
internal static class ReleasePickerVisibility
{
    /// <summary>Published at least once (a release replaced or deployed since was published first).</summary>
    private static bool IsPublished(ReleaseDto release) => release.Status is
        ReleaseStatus.Published or ReleaseStatus.Promoted or ReleaseStatus.Deployed or ReleaseStatus.Superseded;

    /// <summary>The release as offered by default: published, its run did not fail, and the Gate did not
    /// refuse it. A run result that was never recorded (a release-fast release) is not a failure.</summary>
    internal static bool IsDefaultVisible(ReleaseDto release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return IsPublished(release)
            && (release.PipelineRunStatus is null or PipelineStatus.Success)
            && release.Deployable != false;
    }

    /// <summary>Rejected by the Gate or graded F: the only releases the dialog warns about. Any other
    /// grade deploys without a word.</summary>
    internal static ReleaseGateConcern ConcernOf(ReleaseDto release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return release.Deployable == false ? ReleaseGateConcern.Rejected
            : release.AssuranceGrade == AnalysisGrade.F ? ReleaseGateConcern.GradeF
            : ReleaseGateConcern.None;
    }

    /// <summary>The release the version field starts on: the most recent of those offered by default,
    /// or <c>null</c> when none is, so that nothing doubtful is ever chosen for the user.</summary>
    internal static ReleaseDto? Latest(IEnumerable<ReleaseDto> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        return releases
            .Where(IsDefaultVisible)
            .OrderByDescending(release => release.CreatedAt ?? DateTime.MinValue)
            .ThenByDescending(release => release.Id)
            .FirstOrDefault();
    }
}
