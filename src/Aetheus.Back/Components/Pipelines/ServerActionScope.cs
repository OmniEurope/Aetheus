// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// What a caller may see in the planner besides the pipelines (recette R-8), by the right each source already asks for.
/// The servers' scans: the server ids it may read (<see cref="ReadableServerIds"/>, the right every server section asks
/// for) and, among them, the ids it administers (<see cref="AdministeredServerIds"/>, the right the schedule write asks
/// for, which decides whether a scan is movable). The AI tasks follow their owner, a project or a server: read on the
/// owner to see one (<see cref="ReadableProjectIds"/>, <see cref="ReadableServerIds"/>), write on the owner to move it
/// (<see cref="WritableProjectIds"/>, <see cref="WritableServerIds"/>), as the AI task endpoints do. The backups follow
/// their project: read to see, write to move; their restore-check cadence is listed with them, read only. The CVE watch is
/// listed for site administrators only (<see cref="SeesCveWatch"/>), who also move it (its endpoint is administrator only). A null list means every id (a site-wide wildcard); an empty one, none.
/// </summary>
public sealed record ServerActionScope(IReadOnlyCollection<int>? ReadableServerIds, IReadOnlyCollection<int>? AdministeredServerIds)
{
    /// <summary>No action at all.</summary>
    public static ServerActionScope None { get; } = new([], []);

    /// <summary>Every source of an organization, all of it movable. Not the CVE watch: it belongs to no organization and is
    /// asked for explicitly (<see cref="SeesCveWatch"/>).</summary>
    public static ServerActionScope All { get; } = new(null, null)
    {
        ReadableProjectIds = null,
        WritableProjectIds = null,
        WritableServerIds = null
    };

    /// <summary>The projects whose AI tasks and backups the caller may read. Empty unless set: a scope built from the
    /// servers alone shows scans only.</summary>
    public IReadOnlyCollection<int>? ReadableProjectIds { get; init; } = [];

    /// <summary>The projects whose AI tasks and backups the caller may write, hence move.</summary>
    public IReadOnlyCollection<int>? WritableProjectIds { get; init; } = [];

    /// <summary>The servers whose AI tasks the caller may write, hence move.</summary>
    public IReadOnlyCollection<int>? WritableServerIds { get; init; } = [];

    /// <summary>Whether the caller may see, and move, the CVE watch: site administrators only, as the page of its sources is (R-8).
    /// Not a right on a resource: the watch is global data, never an organization's.</summary>
    public bool SeesCveWatch { get; init; }

    public bool SeesNoScan => ReadableServerIds is { Count: 0 };

    public bool SeesNoAiTask => ReadableServerIds is { Count: 0 } && ReadableProjectIds is { Count: 0 };

    public bool SeesNoBackup => ReadableProjectIds is { Count: 0 };

    /// <summary>The restore-check cadence is a column of the backup policy: it follows its project like the backup does.</summary>
    public bool SeesNoRestoreCheck => SeesNoBackup;

    public bool CanMove(int serverId) => AdministeredServerIds is null || AdministeredServerIds.Contains(serverId);

    /// <summary>An AI task is moved with the Write right on its owner: its project, else its server.</summary>
    public bool CanMoveAiTask(int? projectId, int? serverId) => projectId is { } project
        ? WritableProjectIds is null || WritableProjectIds.Contains(project)
        : serverId is { } server && (WritableServerIds is null || WritableServerIds.Contains(server));

    /// <summary>A backup is moved with the Write right on its project, as the policy endpoints ask.</summary>
    public bool CanMoveBackup(int projectId) => WritableProjectIds is null || WritableProjectIds.Contains(projectId);
}
