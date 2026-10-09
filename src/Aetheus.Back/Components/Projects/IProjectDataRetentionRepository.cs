// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
namespace Aetheus.Back.Components.Projects;

/// <summary>PLAN-013 lot 4: reads and writes a project's « Conservation des données » columns.</summary>
public interface IProjectDataRetentionRepository
{
    Task<Project?> FindAsync(int projectId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
