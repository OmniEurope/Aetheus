// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Shared;

internal sealed class ApplicationVersionMonitor(
    IHttpClientFactory httpFactory,
    Uri baseAddress,
    string currentVersion,
    Func<Task> onNewVersionAvailable) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start() => _ = RunAsync(_cts.Token);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    public static async Task<bool> CheckAsync(
        HttpClient http,
        string currentVersion,
        Func<Task> onNewVersionAvailable,
        CancellationToken ct)
    {
        var json = await http.GetStringAsync($"appsettings.json?_={Environment.TickCount64}", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("App", out var appSection)
            || !appSection.TryGetProperty("Version", out var versionProperty))
            return false;

        var remoteVersion = versionProperty.GetString();
        if (string.IsNullOrEmpty(remoteVersion) || remoteVersion == currentVersion)
            return false;

        await onNewVersionAvailable();
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var http = httpFactory.CreateClient();
        http.BaseAddress = baseAddress;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                if (await CheckAsync(http, currentVersion, onNewVersionAvailable, ct))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A transient network error is retried on the next interval.
            }
        }
    }
}
