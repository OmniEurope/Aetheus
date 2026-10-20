// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Layout;

/// <summary> Whether a newer Aetheus front than the one running in this tab has been deployed. The
/// layout's <see cref="ApplicationVersionMonitor"/> marks it on its periodic check; a page about to
/// let the user answer an approval asks for an immediate check, because the approval that confirms
/// a production switch is requested right after the switch, sooner than the next periodic check.
/// </summary>
public sealed class ApplicationVersionState(Func<HttpClient> createClient, Uri baseAddress, string currentVersion)
{
    /// <summary>True once the deployed version differs from the one this tab runs; only a reload clears it.</summary>
    public bool NewVersionAvailable { get; private set; }

    /// <summary>Raised once, when <see cref="NewVersionAvailable"/> becomes true.</summary>
    public event Action? Changed;

    public void MarkNewVersionAvailable()
    {
        if (NewVersionAvailable) return;
        NewVersionAvailable = true;
        Changed?.Invoke();
    }

    /// <summary>Reads the deployed version now. An unreachable or unreadable version file leaves the
    /// state as it was: the periodic check tries again.</summary>
    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        if (NewVersionAvailable) return;
        using var http = createClient();
        http.BaseAddress = baseAddress;
        try
        {
            await ApplicationVersionMonitor.CheckAsync(http, currentVersion, () =>
            {
                MarkNewVersionAvailable();
                return Task.CompletedTask;
            }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Not known: the approval stays answerable, as it was before this check existed.
        }
    }
}
