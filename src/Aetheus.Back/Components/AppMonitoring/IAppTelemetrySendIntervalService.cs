// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>The send interval of a supervised application: the server's, and the one its samples show.</summary>
public interface IAppTelemetrySendIntervalService
{
    /// <summary>Null when the application does not exist.</summary>
    Task<AppTelemetrySendIntervalDto?> GetAsync(int appId, CancellationToken ct = default);
}
