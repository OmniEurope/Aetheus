// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// S-TECH-RPVI: single source of truth for the project-level system variables a run injects
/// (<c>REPOSITORY_URL</c> / <c>BUILD_REPOSITORY_URI</c> / branch / project name). Both the trigger-time
/// snapshot (<see cref="PipelineRunService"/>) and the per-run re-resolution
/// (<see cref="PipelineVariableResolver"/>) call this, so they can never diverge again. The divergence
/// is exactly what froze the attach-time <c>localhost:5300</c> authority on one path but rehomed it on the
/// other, breaking the first-stage clone on a remote agent.
/// </summary>
internal static class PipelineProjectVariables
{
    /// <summary>
    /// R-13: the variable the deployment templates read to know whether the expand/contract migration gate runs
    /// (<c>true</c> or <c>false</c>). It is the project's <see cref="Project.MigrationGateEnabled"/> setting read at every run,
    /// and, written over the template's own default, the setting stays the single truth: toggling it needs no pipeline rewrite.
    /// </summary>
    internal const string MigrationGateVariable = "APPLICATION_MIGRATION_GATE_ENABLED";

    /// <summary>
    /// Adds the project system variables into <paramref name="vars"/> with <c>TryAdd</c> semantics
    /// (caller-supplied overrides win). The mirror URL is re-homed onto the executing backend's clone
    /// base via <see cref="MirrorCloneUrl.Rehome"/> (a no-op for any non-mirror URL).
    /// </summary>
    public static void Inject(
        IDictionary<string, string> vars,
        Project? project,
        IConfiguration configuration,
        string? sourceBranch = null)
    {
        if (project is null) return;

        // R-4: a step is never handed an external address to clone, only Aetheus's own clone URL of the project mirror.
        var repositoryUrl = ExternalRepositoryAddress.IsUnmirrored(project.RepositoryUrl)
            ? string.Empty
            : MirrorCloneUrl.Rehome(configuration, project.RepositoryUrl) ?? string.Empty;
        var branch = string.IsNullOrWhiteSpace(sourceBranch)
            ? project.DefaultBranch ?? "main"
            : sourceBranch;
        vars.TryAdd("BUILD_REPOSITORY_URI", repositoryUrl);
        vars.TryAdd("BUILD_SOURCEBRANCH", branch);
        vars.TryAdd("BUILD_PROJECTNAME", project.Name);
        vars.TryAdd("REPOSITORY_URL", repositoryUrl);
        vars.TryAdd("DEFAULT_BRANCH", branch);
        vars.TryAdd(MigrationGateVariable, project.MigrationGateEnabled ? "true" : "false");
    }
}
