// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppBackups;

/// <summary> The column header filters of the backups page, its policies grid and the run history
/// of one policy. Each key is the grid column's key. </summary>
internal static class BackupListQuery
{
    internal static readonly GridQueryMap<BackupPolicy> PolicyColumns = new GridQueryMap<BackupPolicy>()
        .Text("name", policy => policy.Name)
        .Text("projectName", policy => policy.Project.Name)
        .Text("serverName", policy => policy.Server.Name)
        .Enum("dbEngine", policy => policy.DbEngine)
        .Text("scheduleCron", policy => policy.ScheduleCron)
        .Number("retentionCount", policy => policy.RetentionCount)
        .Date("lastRunAt", policy => policy.LastRunAt)
        .Boolean("enabled", policy => policy.Enabled);

    internal static readonly GridQueryMap<BackupRun> RunColumns = new GridQueryMap<BackupRun>()
        .Date("startedAt", run => run.StartedAt)
        .Enum("status", run => run.Status)
        .Enum("restoreCheckStatus", run => run.RestoreCheckStatus)
        .Number("sizeBytes", run => run.SizeBytes)
        .Text("message", run => run.Message);
}
