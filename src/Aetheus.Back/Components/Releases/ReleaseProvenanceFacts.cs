// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Releases;

/// <summary>The release columns the provenance view starts from.</summary>
public sealed record ReleaseProvenanceFacts(
    int ReleaseId,
    int ProjectId,
    int? CreatedByPipelineRunId,
    int? LastPipelineRunId,
    IReadOnlyList<ReleaseDeliverableFact> Deliverables);

/// <summary>One deliverable of the release and the run that produced it.</summary>
public sealed record ReleaseDeliverableFact(int ArtifactId, int PipelineRunId);

/// <summary>One run that consumed an artifact of the release, and how.</summary>
public sealed record ReleaseArtifactUseFact(int PipelineRunId, ArtifactInputKind Kind);
