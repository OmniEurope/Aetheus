// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>While the viewer is open and auto-refresh enabled, fetch a short journal snapshot every
/// 30 seconds. Closing the viewer or leaving the page stops the work.</summary>
internal sealed class ServiceLogAutoRefresh(Func<Task> tick) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private Timer? _timer;

    public void Start()
    {
        Stop();
        _timer = new Timer(_ => _ = tick(), null, Interval, Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// S-UX-M6T2: journalctl run by an agent outside the systemd-journal/adm groups returns a
    /// benign-looking notice instead of entries; these are its known forms.
    /// </summary>
    public static bool ShowsInsufficientJournalPermissions(string content) =>
        !string.IsNullOrEmpty(content) &&
        (content.Contains("No journal files were opened due to insufficient permissions", StringComparison.OrdinalIgnoreCase)
         || content.Contains("Failed to open files: Permission denied", StringComparison.OrdinalIgnoreCase)
         || (content.Contains("not seeing messages", StringComparison.OrdinalIgnoreCase)
             && content.Contains("systemd-journal", StringComparison.OrdinalIgnoreCase)));
}
