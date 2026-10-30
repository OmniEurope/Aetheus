// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>Recette R-51 (ADR-058): how a deletion ended. <see cref="PendingCleanup"/>: something the project
/// deployed is still on a server, so the project waits, unusable, for the cleanup to be relaunched.</summary>
public enum ProjectDeletionOutcome
{
    Deleted,
    PendingCleanup,
}
