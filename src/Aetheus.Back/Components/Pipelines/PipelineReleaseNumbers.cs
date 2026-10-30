// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Release numbers are allocated per project and per version series (the <c>major.minor</c>
/// of the released commit, each series counting from 0), inherited by child runs or read from the
/// latest allocation according to the definition. Allocation occurs at run creation, so failed and
/// replayed launches can leave gaps and numbers are never reused.</summary>
internal static class PipelineReleaseNumbers
{
    internal const string VariableName = "BUILD_RELEASENUMBER";

    /// <summary>The <c>major.minor</c> series the release number counts in. The app-version function
    /// refuses a series that differs from the one its <c>Directory.Build.props</c> declares.</summary>
    internal const string SeriesVariableName = "BUILD_RELEASESERIES";
    internal const string Allocate = "allocate";
    private const string UpstreamRunVariable = "UPSTREAM_RUN_ID";
    internal const string InvalidModeMessage =
        "`release_number:` accepts only `allocate` (each run takes the project's next release number).";

    internal static bool Allocates(PipelineYamlDefinition definition) =>
        string.Equals(definition.ReleaseNumber?.Trim(), Allocate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a mode other than <c>allocate</c>. The definition is read from Git at launch, where the
    /// save-time validation never ran: a misspelt mode must refuse the launch before anything is reserved,
    /// not quietly build every release with the previous number.
    /// </summary>
    internal static void EnsureValid(PipelineYamlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.ReleaseNumber is not null && !Allocates(definition))
            throw new PipelineProblem(
                PipelineProblemCodes.ReleaseNumberModeInvalid, new Dictionary<string, string>(), InvalidModeMessage).ToRefusal();
    }

    /// <summary>
    /// The release number of a run about to be created, with the version series it counts in; null for
    /// a pipeline without a project. <paramref name="series"/> is the <c>major.minor</c> of the commit
    /// the run releases (<see cref="ReleaseSeries"/>): a run that allocates takes the next number of
    /// that series, a run started by another inherits that run's number and series, any other run
    /// reads the series' latest number.
    /// </summary>
    internal static async Task<ReleaseNumberAssignment?> ResolveAsync(
        IPipelineReleaseNumberRepository repo,
        PipelineYamlDefinition definition,
        int? projectId,
        IReadOnlyDictionary<string, string> launchVariables,
        string series,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(launchVariables);
        ArgumentNullException.ThrowIfNull(series);
        EnsureValid(definition);
        if (projectId is not { } project) return null;
        if (Allocates(definition))
            return await repo.AllocateNextReleaseNumberAsync(project, series, ct).ConfigureAwait(false) is { } allocated
                ? new ReleaseNumberAssignment(series, allocated)
                : null;
        if (launchVariables.TryGetValue(UpstreamRunVariable, out var upstream)
            && int.TryParse(upstream, NumberStyles.None, CultureInfo.InvariantCulture, out var upstreamRunId)
            && await repo.GetRunReleaseAsync(upstreamRunId, ct).ConfigureAwait(false) is { } inherited)
            return inherited with { Series = inherited.Series ?? series };
        return await repo.GetLatestReleaseNumberAsync(project, series, ct).ConfigureAwait(false) is { } latest
            ? new ReleaseNumberAssignment(series, latest)
            : null;
    }

    /// <summary>Puts the run's release number and series among the variables a resolution starts from.</summary>
    internal static void Inject(IDictionary<string, string> variables, int? releaseNumber, string? releaseSeries)
    {
        if (releaseNumber is { } number)
            variables[VariableName] = number.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(releaseSeries))
            variables[SeriesVariableName] = releaseSeries;
    }
}
