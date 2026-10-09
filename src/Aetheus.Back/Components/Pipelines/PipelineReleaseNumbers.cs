// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R2-110: the release number of a run, the last number of the application version
/// (<c>major.minor.N</c>, composed by <c>deploy/scripts/app-version.sh</c>), exposed to its steps as
/// <see cref="VariableName"/>. It counts the releases of the project one by one instead of every run of
/// every pipeline:
/// <list type="bullet">
/// <item>a pipeline declaring <c>release_number: allocate</c> (candidate, release-fast) takes the
/// project's next number at each run;</item>
/// <item>a run started by another (a trigger step's child, an <c>on_success</c> downstream) builds
/// with the number of the run that started it, so a candidate's CI packages the candidate's number;</item>
/// <item>any other run (nightly, demo) reads the latest number handed out, and adds its own suffix.</item>
/// </list>
/// A number is taken when the run is created, so a run that fails afterwards still used it (numbers
/// never repeat, a failed release leaves a gap), and a replayed idempotent launch burns one, like the
/// per-pipeline build number.
/// </summary>
internal static class PipelineReleaseNumbers
{
    internal const string VariableName = "BUILD_RELEASENUMBER";
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
            throw new BadRequestException(InvalidModeMessage);
    }

    /// <summary>The release number of a run about to be created; null for a pipeline without a project.</summary>
    internal static async Task<int?> ResolveAsync(
        IPipelineRepository repo,
        PipelineYamlDefinition definition,
        int? projectId,
        IReadOnlyDictionary<string, string> launchVariables,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(launchVariables);
        EnsureValid(definition);
        if (projectId is not { } project) return null;
        if (Allocates(definition))
            return await repo.AllocateNextReleaseNumberAsync(project, ct).ConfigureAwait(false);
        if (launchVariables.TryGetValue(UpstreamRunVariable, out var upstream)
            && int.TryParse(upstream, NumberStyles.None, CultureInfo.InvariantCulture, out var upstreamRunId)
            && await repo.GetRunReleaseNumberAsync(upstreamRunId, ct).ConfigureAwait(false) is { } inherited)
            return inherited;
        return await repo.GetLatestReleaseNumberAsync(project, ct).ConfigureAwait(false);
    }

    /// <summary>Puts the run's release number among the variables a resolution starts from.</summary>
    internal static void Inject(IDictionary<string, string> variables, int? releaseNumber)
    {
        if (releaseNumber is { } number)
            variables[VariableName] = number.ToString(CultureInfo.InvariantCulture);
    }
}
