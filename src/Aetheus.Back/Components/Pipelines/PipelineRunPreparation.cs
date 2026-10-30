// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Immutable run input resolved before authorization. The controller and non-interactive launch paths
/// authorize <see cref="TargetServerIds"/>, then the runner executes this exact YAML and commit snapshot.
/// </summary>
public sealed record PipelineRunPreparation
{
    public required int PipelineId { get; init; }
    public string? BranchName { get; init; }
    public string? CommitHash { get; init; }
    public string? RepositoryUrl { get; init; }

    /// <summary>The revision the definition was read at, when the workspace comes from another
    /// repository (<c>source:</c> block); null when both are one and <see cref="CommitHash"/> says it.</summary>
    public string? DefinitionCommitHash { get; init; }

    /// <summary>The branch the definition was read on, under the same condition.</summary>
    public string? DefinitionBranchName { get; init; }

    /// <summary>The version series (<c>major.minor</c> of the <c>VersionPrefix</c>) of the commit the
    /// run releases, read from the repository at that commit when the preparation is made. Empty when
    /// the code declares none or cannot be read (external repository, workspace from another
    /// repository). A run takes its release number in this series.</summary>
    public string ReleaseSeries { get; init; } = Aetheus.Shared.Components.Projects.ReleaseSeries.None;

    public required string YamlSnapshot { get; init; }
    public required IReadOnlyCollection<int> TargetServerIds { get; init; }

    /// <summary>Launch the run in "run to end" mode (<see cref="PipelineRun.RunToEnd"/>). Set by the
    /// manual launch once the platform setting allowed it, and passed on to the runs a trigger step
    /// starts from such a run.</summary>
    public bool RunToEnd { get; init; }

    /// <summary>R-72: the user deploying despite the quality gate, set by the manual launch once it checked
    /// the rights the override takes; null for every other launch. Never passed on to a child run.</summary>
    public string? GateOverrideBy { get; init; }
    internal Pipeline Pipeline { get; init; } = default!;
    internal PipelineYamlDefinition Definition { get; init; } = default!;
    internal List<PipelineStageDefinition> EffectiveStages { get; init; } = [];
    internal int? EffectiveProjectId { get; init; }

    /// <summary>Keys of the definition this backend does not know and skipped, copied into the
    /// run's warnings so the skip is visible where the run is read.</summary>
    internal IReadOnlyList<string> DefinitionWarnings { get; init; } = [];
}
