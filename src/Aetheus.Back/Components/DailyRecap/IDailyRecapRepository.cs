// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// Reads what the daily recap tells about (each query bounded to one UTC window or one horizon) and
/// keeps its settings and its last sent day in the platform settings table.
/// </summary>
internal interface IDailyRecapRepository
{
    Task<IReadOnlyDictionary<string, string>> GetSettingRowsAsync(CancellationToken ct = default);
    Task SaveSettingRowsAsync(IReadOnlyDictionary<string, string> rows, DateTime nowUtc, CancellationToken ct = default);

    Task<List<string>> GetActiveAdministratorEmailsAsync(CancellationToken ct = default);

    /// <summary>Runs that reached a final status in the window and that no other run triggered.</summary>
    Task<List<RecapPipelineRun>> GetParentRunsCompletedAsync(DateTime startUtc, DateTime endUtc, int limit, CancellationToken ct = default);

    /// <summary>Every active server, its OS-update probe and its latest disk measure; agent facts come apart.</summary>
    Task<List<RecapServerFacts>> GetServerFactsAsync(CancellationToken ct = default);
    Task<List<ServerDto>> GetAgentCompatibilityFactsAsync(CancellationToken ct = default);

    Task<List<RecapRelease>> GetReleaseEventsAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
    Task<List<RecapBackup>> GetBackupsFinishedAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
    Task<List<RecapExpiring>> GetCertificatesExpiringBeforeAsync(DateTime limitUtc, CancellationToken ct = default);
    Task<List<RecapExpiring>> GetSecretsExpiringBeforeAsync(DateTime limitUtc, CancellationToken ct = default);
    Task<RecapFindings> GetFindingChangesAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
    Task<List<RecapMailServer>> GetMailServersAsync(CancellationToken ct = default);

    /// <summary>The open matches of the vulnerability watch as they stand now, and those that appeared or closed in the window.</summary>
    Task<RecapCveWatch> GetCveWatchAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default);

    /// <summary>The open affected matches of each project that has any, by project (recette R-25).</summary>
    Task<List<RecapProjectCve>> GetOpenCvesByProjectAsync(CancellationToken ct = default);

    /// <summary>R-41: the affected findings of the host system of each server that has any, by server.</summary>
    Task<List<RecapServerCve>> GetOpenCvesByServerAsync(CancellationToken ct = default);

    /// <summary>The login of the active user with this address (compared without case), or null: an address with no account reads no project.</summary>
    Task<string?> FindActiveUsernameByEmailAsync(string email, CancellationToken ct = default);
}

/// <summary>A server as stored, before its agent verdict and disk level are worked out.</summary>
internal sealed record RecapServerFacts(
    int Id,
    string Name,
    ServerStatus Status,
    DateTime LastHeartbeatUtc,
    string AgentVersion,
    RecapOsUpdates? OsUpdates,
    double? DiskUsedGb,
    double? DiskTotalGb,
    DateTime? DiskMeasuredAtUtc);
