// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>How a pipeline run consumed an artifact it did not produce.</summary>
public enum ArtifactInputKind
{
    /// <summary>A <c>restore-artifacts</c> step brought the artifact onto the runner.</summary>
    Restore = 0,
    /// <summary>A <c>deploy</c> step shipped the artifact to a server.</summary>
    Deploy = 1
}
