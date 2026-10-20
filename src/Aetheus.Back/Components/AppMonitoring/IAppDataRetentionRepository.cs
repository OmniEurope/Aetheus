// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary> The « Conservation des données » values each monitored application applies, read from
/// its project. </summary>
public interface IAppDataRetentionRepository
{
    Task<IReadOnlyList<AppDataRetention>> GetAllAsync(CancellationToken ct = default);
    Task<DataRetentionValues?> GetForAppAsync(int appId, CancellationToken ct = default);

    /// <summary>The profile and the overrides of the application's project; null for an unknown application.</summary>
    Task<AppDataRetentionSetting?> GetSettingForAppAsync(int appId, CancellationToken ct = default);
}

/// <summary>One application and the values of its project.</summary>
public sealed record AppDataRetention(int AppId, int ProjectId, DataRetentionValues Values);

/// <summary>The setting of an application's project, before it is resolved into values.</summary>
public sealed record AppDataRetentionSetting(DataRetentionProfile Profile, DataRetentionOverrides Overrides);
