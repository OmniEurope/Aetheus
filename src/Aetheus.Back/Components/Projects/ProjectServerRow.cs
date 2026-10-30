// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// One line of a project's server list (recette R-83): either a server the project is linked to directly (a
/// <c>ProjectServer</c> row, <see cref="Id"/> is that row's id) or a server that belongs to it only because it is
/// attached to one of the project's environments (<see cref="ViaEnvironmentId"/> is set and <see cref="Id"/> is
/// 0: there is no <c>ProjectServer</c> row to edit or detach).
/// </summary>
public sealed class ProjectServerRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int? ServerId { get; init; }
    public ProjectServerType Type { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int? Port { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? ServerName { get; init; }
    public string? ServerHostname { get; init; }
    public ServerStatus? ServerStatus { get; init; }

    /// <summary>The environment of the project the server is attached to, when that is the only reason it is
    /// listed; null for a server linked to the project itself.</summary>
    public int? ViaEnvironmentId { get; init; }

    public string? ViaEnvironmentName { get; init; }
}
