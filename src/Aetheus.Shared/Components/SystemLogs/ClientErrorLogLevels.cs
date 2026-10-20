// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.SystemLogs;

/// <summary>The levels a client report may ask the system log for (<see cref="ClientErrorLogRequest.Level"/>).</summary>
public static class ClientErrorLogLevels
{
    /// <summary>Something failed in the browser; the default when a report names no level.</summary>
    public const string Error = "Error";

    /// <summary>Something went wrong and recovered on its own, such as a realtime outage that was restored.</summary>
    public const string Warning = "Warning";
}
