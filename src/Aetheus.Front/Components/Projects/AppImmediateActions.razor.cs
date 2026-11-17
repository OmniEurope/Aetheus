// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// R-29: probe now and ask for the telemetry now, for the application selected in the Supervision. The state of the
/// last request of each kind is read when the application is shown, then re-read every few seconds while one waits
/// for its answer (an agent probe, the telemetry package's poll and export), until it arrives or times out.
/// </summary>
public partial class AppImmediateActions : IAsyncDisposable
{
    internal static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(5);

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    /// <summary>Shown at the left of the probe button on the same row (the application picker).</summary>
    [Parameter] public RenderFragment? Leading { get; set; }
    [Parameter, EditorRequired] public MonitoredAppDto App { get; set; } = default!;

    private readonly CancellationTokenSource _lifetime = new();
    private AppProbeNowDto? _probe;
    private AppTelemetryExportRequestDto? _telemetry;
    private string? _refusal;
    private bool _busy;
    private int? _loadedAppId;
    private bool _polling;

    private bool CanProbe => App.Enabled && !string.IsNullOrWhiteSpace(App.ProbeUrl);

    private string? Prerequisite =>
        !App.Enabled ? L["AppImmediateDisabled"].Value
        : string.IsNullOrWhiteSpace(App.ProbeUrl) ? L["AppImmediateNoProbeUrl"].Value
        : !App.HasIngestKey ? L["AppImmediateNoIngestKey"].Value
        : null;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedAppId == App.Id) return;
        _loadedAppId = App.Id;
        _probe = null;
        _telemetry = null;
        _refusal = null;
        await ReadStatesAsync(App.Id);
        StartPollingIfWaiting();
    }

    private async Task ProbeNowAsync()
    {
        var appId = App.Id;
        await RunAsync(async ct => _probe = await Api.Monitoring.ProbeMonitoredAppNowAsync(appId, ct));
    }

    private async Task RequestTelemetryAsync()
    {
        var appId = App.Id;
        await RunAsync(async ct =>
        {
            _telemetry = await Api.Monitoring.RequestMonitoredAppTelemetryExportAsync(appId, ct);
            // Says the request is recorded, not answered: the answer comes later, in the alert.
            Toast.Info("AppTelemetryNowTitle", "AppTelemetryNowRequested");
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> send)
    {
        if (_busy) return;
        _busy = true;
        _refusal = null;
        try
        {
            await send(_lifetime.Token);
        }
        catch (HttpRequestException ex)
        {
            _refusal = ex.StatusCode switch
            {
                HttpStatusCode.Forbidden => L["AppImmediateForbidden"].Value,
                HttpStatusCode.Conflict => L["AppImmediateConflict"].Value,
                _ => L["AppImmediateFailed"].Value
            };
        }
        finally
        {
            _busy = false;
        }
        StartPollingIfWaiting();
    }

    internal static bool IsWaiting(AppProbeNowDto? probe) =>
        probe is { RequestedAt: not null, Result: null, TimedOut: false };

    internal static bool IsWaiting(AppTelemetryExportRequestDto? telemetry) =>
        telemetry is { RequestedAt: not null, ReceivedAt: null, TimedOut: false };

    private void StartPollingIfWaiting()
    {
        if (_polling || (!IsWaiting(_probe) && !IsWaiting(_telemetry))) return;
        _polling = true;
        _ = PollAsync(App.Id);
    }

    private async Task PollAsync(int appId)
    {
        try
        {
            using var timer = new PeriodicTimer(PollPeriod);
            while (await timer.WaitForNextTickAsync(_lifetime.Token) && appId == _loadedAppId
                   && (IsWaiting(_probe) || IsWaiting(_telemetry)))
            {
                await ReadStatesAsync(appId);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // The component went away.
        }
        finally
        {
            _polling = false;
        }
    }

    // A read that fails keeps the last state on screen; the next tick reads again.
    private async Task ReadStatesAsync(int appId)
    {
        try
        {
            var probe = Api.Monitoring.GetMonitoredAppProbeNowAsync(appId, _lifetime.Token);
            var telemetry = Api.Monitoring.GetMonitoredAppTelemetryExportAsync(appId, _lifetime.Token);
            await Task.WhenAll(probe, telemetry);
            if (appId != _loadedAppId) return;
            _probe = probe.Result;
            _telemetry = telemetry.Result;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            // Kept as it was.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
    }
}
