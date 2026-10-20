// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary> Records, at dispatch, the artifact a restore or deploy step hands to the agent.
/// Recorded when the task is created rather than when it succeeds: the release detail lists what a
/// run consumed or tried to consume, and the run's own status says whether that attempt succeeded.
/// </summary>
internal static class ArtifactInputRecorder
{
    public static void Track(
        IPipelineRepository repo,
        int runId,
        PipelineStepRun stepRun,
        PipelineArtifact artifact,
        ArtifactInputKind kind,
        int? releaseId,
        DateTime now) =>
        repo.TrackArtifactInput(new PipelineRunArtifactInput
        {
            PipelineRunId = runId,
            StepName = stepRun.StepName,
            Kind = kind,
            ArtifactId = artifact.Id,
            ArtifactName = artifact.Name,
            Sha256 = artifact.Sha256,
            SourcePipelineRunId = artifact.PipelineRunId,
            ReleaseId = releaseId,
            RecordedAt = now
        });
}
