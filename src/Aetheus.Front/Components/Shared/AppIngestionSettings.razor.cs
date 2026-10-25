// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Front.Components.Shared;

public partial class AppIngestionSettings
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;

    [Parameter] public int AppId { get; set; }
    [Parameter] public MonitoredAppDto? App { get; set; }
    [Parameter] public bool CanWrite { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }

    private List<AppMetricThresholdDto> _thresholds = [];
    // Header filter texts, built once so the columns see the same delegate on every render.
    private Func<string, string>? _operatorText;
    private Func<string, string>? _breachedText;
    private Func<string, string> OperatorText => _operatorText ??= GridFilterText.ForEnum<ComparisonOperator>(L);
    private Func<string, string> BreachedText => _breachedText ??= value =>
        bool.TryParse(value, out var breached) ? L[breached ? "Breached" : "OK"].Value : value;
    private string? _generatedKey;
    private bool _busy;
    private bool _analyticsBusy;
    private bool _analyticsLoading = true;
    private bool _analyticsEnabled;
    private bool _analyticsPublicIngestEnabled;
    private string _analyticsSiteId = string.Empty;
    private string _analyticsOrigins = string.Empty;
    private long _analyticsStorageBudgetBytes = AppMonitoringDefaults.DefaultAnalyticsStorageBudgetBytes;
    private AppWebAnalyticsConfigurationDto? _analyticsConfiguration;
    private AppWebAnalyticsSummaryDto? _audience;
    private int _lastAppId = -1;
    private int _loadGeneration;

    private readonly CreateMetricThresholdRequest _newThreshold = new();
    private static readonly List<ComparisonOperator> _operators = Enum.GetValues<ComparisonOperator>().ToList();

    private string IngestEndpoint =>
        $"{(Config["ApiBaseUrl"] ?? LocalDevelopmentEndpoints.ApiHttpsBaseUrl).TrimEnd('/')}/api/ingest/otlp/v1";
    private string MetricsEndpoint => IngestEndpoint + "/metrics";
    private string LogsEndpoint => IngestEndpoint + "/logs";
    private string TracesEndpoint => IngestEndpoint + "/traces";
    private string PublicAnalyticsEndpoint =>
        $"{IngestEndpoint[..^"/otlp/v1".Length]}/web-analytics/v1/public/{Uri.EscapeDataString(_analyticsSiteId)}";

    protected override async Task OnParametersSetAsync()
    {
        if (AppId == _lastAppId) return;
        _lastAppId = AppId;
        var generation = ++_loadGeneration;
        _generatedKey = null;
        await Task.WhenAll(
            LoadThresholdsAsync(AppId, generation),
            LoadAnalyticsConfigurationAsync(AppId, generation),
            LoadAudienceStorageAsync(AppId, generation));
    }

    private Task LoadThresholdsAsync() => LoadThresholdsAsync(AppId, _loadGeneration);

    private async Task LoadThresholdsAsync(int appId, int generation)
    {
        try
        {
            var thresholds = await Api.Monitoring.GetAppThresholdsAsync(appId);
            if (generation == _loadGeneration && appId == AppId) _thresholds = thresholds;
        }
        catch (HttpRequestException)
        {
            if (generation == _loadGeneration && appId == AppId) _thresholds = [];
        }
    }

    private async Task LoadAnalyticsConfigurationAsync(int appId, int generation)
    {
        if (IsCurrentLoad(appId, generation))
            _analyticsLoading = true;
        try
        {
            var configuration = await Api.Monitoring.GetAppWebAnalyticsConfigurationAsync(appId);
            if (!IsCurrentLoad(appId, generation))
                return;
            ApplyAnalyticsConfiguration(configuration);
        }
        catch (HttpRequestException)
        {
            if (IsCurrentLoad(appId, generation))
                _analyticsConfiguration = null;
        }
        finally
        {
            if (IsCurrentLoad(appId, generation))
                _analyticsLoading = false;
        }
    }

    // A load still answers for the application on screen, not one the page has since left.
    private bool IsCurrentLoad(int appId, int generation) => generation == _loadGeneration && appId == AppId;

    // The form fields of a configuration read; none (no configuration yet) gives the defaults.
    private void ApplyAnalyticsConfiguration(AppWebAnalyticsConfigurationDto? configuration)
    {
        _analyticsConfiguration = configuration;
        _analyticsEnabled = configuration?.Enabled ?? false;
        _analyticsPublicIngestEnabled = configuration?.PublicIngestEnabled ?? false;
        _analyticsSiteId = configuration?.SiteId ?? string.Empty;
        _analyticsOrigins = string.Join(Environment.NewLine, configuration?.AllowedOrigins ?? []);
        _analyticsStorageBudgetBytes = configuration?.StorageBudgetBytes
            ?? AppMonitoringDefaults.DefaultAnalyticsStorageBudgetBytes;
    }

    /// <summary> The audience summary, read for its storage figures only (estimate, budget, refused
    /// events), the one line under the budget field. A failed read leaves the line out. </summary>
    private async Task LoadAudienceStorageAsync(int appId, int generation)
    {
        try
        {
            var audience = await Api.Monitoring.GetAppWebAnalyticsAsync(appId);
            if (generation == _loadGeneration && appId == AppId) _audience = audience;
        }
        catch (HttpRequestException)
        {
            if (generation == _loadGeneration && appId == AppId) _audience = null;
        }
    }

    /// <summary>"Used: 12 % (1 Mo estimated of 100 Mo) ...", or null while the figures are unknown.</summary>
    private string? StorageUsageText => _audience is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, L["AnalyticsStorageUsageLine"],
            _audience.StorageUsagePercent,
            FormatBytes(_audience.EstimatedStorageBytes),
            FormatBytes(_audience.StorageBudgetBytes),
            _audience.RejectedEvents);

    private static string FormatBytes(long bytes) => DisplayFormatting.Size(bytes);

    private async Task GenerateKeyAsync()
    {
        // The first generation replaces nothing; a regeneration kills the current key at once, so it is
        // confirmed like any destructive action (red "Régénérer", "Revenir" to dismiss).
        if (App?.HasIngestKey == true)
        {
            var confirmed = await Dialog.Confirm(L["RegenerateIngestKeyConfirm"].Value, L["RegenerateKey"].Value,
                new OmniConfirmOptions { Destructive = true, OkButtonText = L["Regenerate"].Value, CancelButtonText = L["GoBack"].Value, ConfirmIcon = OmniIconName.Key });
            if (confirmed != true) return;
        }

        _busy = true;
        try
        {
            var resp = await Api.Monitoring.GenerateIngestKeyAsync(AppId);
            if (resp is not null)
            {
                _generatedKey = resp.Key;
                Toast.Success("Created", "IngestKeyGenerated");
                await OnChanged.InvokeAsync();
            }
            else
            {
                Toast.Error("Error", "OperationFailed");
            }
        }
        finally { _busy = false; }
    }

    private async Task RevokeKeyAsync()
    {
        var confirmed = await Dialog.Confirm(L["RevokeIngestKeyConfirm"].Value, L["RevokeIngestKey"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Revoke"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var status = await Api.Monitoring.RevokeIngestKeyAsync(AppId);
        if (status.Success)
        {
            _generatedKey = null;
            Toast.Success("Deleted", "IngestKeyRevoked");
            await OnChanged.InvokeAsync();
        }
    }

    private async Task CopyKeyAsync()
    {
        if (_generatedKey is not null)
            await Clipboard.CopyAsync(_generatedKey);
    }

    private async Task SaveAnalyticsConfigurationAsync()
    {
        var origins = _analyticsOrigins
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(_analyticsSiteId) || origins.Count == 0)
        {
            Toast.Warning("ValidationError", "RequiredFields");
            return;
        }

        _analyticsBusy = true;
        try
        {
            var configured = await Api.Monitoring.ConfigureAppWebAnalyticsAsync(AppId, new ConfigureAppWebAnalyticsRequest
            {
                Enabled = _analyticsEnabled,
                PublicIngestEnabled = _analyticsPublicIngestEnabled,
                SiteId = _analyticsSiteId.Trim(),
                AllowedOrigins = origins,
                StorageBudgetBytes = _analyticsStorageBudgetBytes
            });
            if (configured is null)
            {
                Toast.Error("Error", "OperationFailed");
                return;
            }
            _analyticsConfiguration = configured;
            Toast.Success("Saved", "WebAnalyticsConfigurationSaved");
            // A new budget changes the share the storage line gives.
            await LoadAudienceStorageAsync(AppId, _loadGeneration);
            await OnChanged.InvokeAsync();
        }
        finally
        {
            _analyticsBusy = false;
        }
    }

    private async Task RotateAnalyticsKeyAsync()
    {
        var confirmed = await Dialog.Confirm(
            L["RotateAnalyticsKeyConfirm"].Value,
            L["RotateAnalyticsKey"].Value,
            new OmniConfirmOptions { OkButtonText = L["Rotate"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true)
            return;

        _analyticsBusy = true;
        try
        {
            _analyticsConfiguration = await Api.Monitoring.RotateAppWebAnalyticsKeyAsync(AppId);
            if (_analyticsConfiguration is null)
            {
                Toast.Error("Error", "OperationFailed");
                return;
            }
            Toast.Success("Saved", "AnalyticsKeyRotated");
        }
        finally
        {
            _analyticsBusy = false;
        }
    }

    private async Task AddThresholdAsync()
    {
        if (string.IsNullOrWhiteSpace(_newThreshold.MetricName))
        {
            Toast.Warning("ValidationError", "RequiredFields");
            return;
        }
        var created = await Api.Monitoring.CreateAppThresholdAsync(AppId, _newThreshold);
        if (created is not null)
        {
            _newThreshold.MetricName = string.Empty;
            _newThreshold.Threshold = 0;
            Toast.Success("Created", "ThresholdCreated");
            await LoadThresholdsAsync();
        }
    }

    private async Task DeleteThresholdAsync(int thresholdId)
    {
        var status = await Api.Monitoring.DeleteAppThresholdAsync(thresholdId);
        if (status.Success)
        {
            Toast.Success("Deleted", "ThresholdDeleted");
            await LoadThresholdsAsync();
        }
    }
}
