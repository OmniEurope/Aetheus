// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

/// <summary> The "hosting server" of a monitored application, as the supervision list and the
/// application overview show it. an application registered without a server is probed by the
/// backend, yet usually runs on a fleet server; when the backend deduced that server from its
/// Apache virtual hosts, it is named (with the backend probe mentioned) instead of "off-fleet".
/// </summary>
internal static class MonitoredAppServerLabel
{
    /// <summary>The server page to link to: the registered server, else the deduced one.</summary>
    internal static int? ServerId(MonitoredAppDto app) => app.ServerId ?? app.HostingServer?.ServerId;

    internal static string Format(MonitoredAppDto app, IStringLocalizer<AppStrings> localizer) =>
        app.ServerId is { } serverId ? app.ServerName ?? $"#{serverId}"
        : app.HostingServer is { } hosting ? localizer["HostedOnServerBackendProbe", hosting.ServerName]
        : localizer["OffFleetBackendProbe"];
}
