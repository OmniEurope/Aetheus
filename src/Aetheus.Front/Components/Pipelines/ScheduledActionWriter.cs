// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Where a move of a scheduled action is written (recette R-8): each sort keeps its schedule in its own place and is
/// stored through its own endpoint, which checks again the right the reader has on it (a scan the Administrator right on
/// the server, an AI task the Write right on its owner, a backup the Write right on its project). The expression is
/// the one of the action's sort (<see cref="ServerActionDefaults.ToStoredFields(ServerActionKind, string)"/>).
/// </summary>
internal static class ScheduledActionWriter
{
    public static Task<ApiStatus> StoreAsync(ApiClient api, ScheduledServerActionDto action, string cron, CancellationToken ct = default) =>
        action.Kind switch
        {
            ServerActionKind.RkhunterScan => api.Security.SetRkhunterScheduleAsync(action.SourceId, new RkhunterScheduleRequest { CronExpression = cron }, ct),
            ServerActionKind.AiTask => api.Ai.SetAiTaskScheduleAsync(action.SourceId, new SetAiTaskScheduleRequest { Schedule = cron }, ct),
            ServerActionKind.AppBackup => api.Security.SetBackupScheduleAsync(action.SourceId, new SetBackupScheduleRequest { ScheduleCron = cron }, ct),
            // The CVE watch keeps one local time of day, HH:mm, not a cron expression.
            ServerActionKind.CveWatch => api.Analysis.SetVulnerabilityPassTimeAsync(new SetVulnerabilityPassTimeRequest { PassTime = cron }, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Every kind of action is stored through its own endpoint.")
        };
}
