// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-72 (owner decision of 2026-10-28): a manual launch may deploy a release despite the quality gate. The
/// override is checked at the launch, before the run exists: it applies only to a deployment pipeline (one
/// declaring <c>candidateVersion</c>) launched with a release of its own project, and never to a release that
/// keeps no artifact. The rights it takes are checked by the controller (Write on the project, on top of the
/// launch's own). Accepted, it is written on the run (who, when, the reasons) and in the audit log; the grade
/// gate then lets the run start (<see cref="PipelineDeploymentGradeGate"/>). Approval stages still apply.
/// </summary>
internal static class PipelineGateOverride
{
    /// <summary>The reasons the gate holds against the release, or null when the launch asks no override.
    /// Throws the launch refusal when the override cannot apply.</summary>
    public static async Task<DeploymentGateAssessment?> CheckAsync(
        PipelineRunPreparation preparation, PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> variables, IPipelineRepository repo, CancellationToken ct)
    {
        if (preparation.GateOverrideBy is null) return null;

        var assessment = await PipelineDeploymentGateWarnings.AssessAsync(
            definition, variables, preparation.EffectiveProjectId, repo, ct).ConfigureAwait(false);
        if (assessment is null)
            throw PipelineProblem.Of(
                PipelineProblemCodes.GateOverrideNotADeployment,
                "The quality gate can only be overridden for a deployment pipeline of a project (one declaring "
                    + $"'{PipelineReleaseGateVariables.CandidateVersionParameter}') launched with a release version.").ToRefusal();
        if (assessment.Release is null)
            throw PipelineProblem.Of(
                PipelineProblemCodes.GateOverrideReleaseUnknown,
                $"Release '{assessment.Selector}' does not exist in this project, so it cannot be deployed.",
                ("release", assessment.Selector)).ToRefusal();
        // The one refusal the override never lifts: without its artifact the release cannot be deployed.
        if (assessment.Release.KeptArtifactCount == 0)
            throw PipelineProblem.Of(
                PipelineProblemCodes.GateOverrideNoArtifact,
                $"Release '{assessment.Release.Version}' keeps no artifact, so it cannot be deployed, even despite the quality gate.",
                ("release", assessment.Release.Version)).ToRefusal();
        return assessment;
    }

    /// <summary>Writes the override on the run about to be created.</summary>
    public static void Apply(PipelineRun run, string by, DeploymentGateAssessment assessment, DateTime now)
    {
        run.GateOverrideBy = by;
        run.GateOverrideAt = now;
        run.GateOverrideWarningsJson = JsonSerializer.Serialize(assessment.Warnings);
    }

    /// <summary>The audit line: the release and the reasons the gate had, in English.</summary>
    public static string AuditDetails(string pipelineName, DeploymentGateAssessment assessment) =>
        $"{pipelineName} | release {assessment.Release?.Version ?? assessment.Selector} deployed despite the quality gate: "
        + (assessment.Warnings.Count == 0
            ? "no reason at launch"
            : string.Join(", ", assessment.Warnings.Select(warning => warning.Kind)));

    /// <summary>The recorded reasons (written by <see cref="Apply"/>), empty for a run without an override.</summary>
    public static List<DeploymentGateWarningDto> ReadWarnings(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<DeploymentGateWarningDto>>(json) ?? [];
}
