// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;

namespace Aetheus.Front.Components.Alerts;

/// <summary>The page of one alert that fired (recette R-52): the address the notification mail's button
/// opens, and the one a stored alert is reached by.</summary>
public partial class AlertOccurrenceDetail
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    [Parameter] public long AlertId { get; set; }

    private AlertOccurrenceDto? _alert;
    private long? _loadedAlertId;
    private bool _loading = true;
    // Driven by the server's answer, not by a front-side role check: the API decides who may read the alert.
    private bool _forbidden;

    private string Title => _alert is null ? string.Empty : _alert.Operator is not null ? _alert.RuleName : MetricText;

    private OmniTone SeverityTone => _alert?.Severity switch
    {
        AlertSeverity.Critical => OmniTone.Danger,
        AlertSeverity.Warning => OmniTone.Warning,
        _ => OmniTone.Accent
    };

    /// <summary>A rule alert words its metric from the enum; a host check (OOM kill, sudoers drift...) from its own key.</summary>
    private string MetricText
    {
        get
        {
            var metric = _alert?.Metric ?? string.Empty;
            if (Enum.TryParse<MetricType>(metric, out var type) && Enum.IsDefined(type)) return L.Localize(type);
            var text = L["AlertMetric_" + metric];
            return text.ResourceNotFound ? metric : text.Value;
        }
    }

    /// <summary>The instant on the reader's own clock, like the other pages.</summary>
    private string TriggeredAtText =>
        _alert?.TriggeredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    // A metric this front does not know (a newer back) still shows its number rather than an empty tile.
    private string MeasuredValue => _alert is { ObservedValue: { } value }
        ? FormatValue(Math.Round(value, 1))
        : string.Empty;

    private string ThresholdText => _alert is { Threshold: { } threshold }
        ? $"{OperatorSymbol(_alert.Operator)} {FormatValue(threshold)}".TrimStart()
        : string.Empty;

    private string FormatValue(double value) => MetricKind is { } kind
        ? AlertThresholdUnit.Format(kind, value)
        : value.ToString("0.#", CultureInfo.CurrentCulture);

    private MetricType? MetricKind => _alert is not null && Enum.TryParse<MetricType>(_alert.Metric, out var type) && Enum.IsDefined(type)
        ? type
        : null;

    private static string OperatorSymbol(ComparisonOperator? comparison) => comparison switch
    {
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.GreaterThanOrEqual => "≥",
        ComparisonOperator.LessThanOrEqual => "≤",
        _ => string.Empty
    };

    private OmniTone RuleStateTone => _alert?.RuleIsEnabled switch
    {
        true => OmniTone.Success,
        false => OmniTone.Neutral,
        _ => OmniTone.Warning
    };

    private string RuleStateText => _alert?.RuleIsEnabled switch
    {
        true => L["Active"],
        false => L["Disabled"],
        _ => L["AlertRuleDeleted"]
    };

    protected override async Task OnParametersSetAsync()
    {
        Breadcrumb.Set(
            new BreadcrumbItem(L["Alerts"], "/alerts"),
            new BreadcrumbItem($"{L["Alert"]} #{AlertId}"));
        if (_loadedAlertId == AlertId) return;

        _loadedAlertId = AlertId;
        var alertId = AlertId;
        _loading = true;
        _alert = null;
        _forbidden = false;
        try
        {
            var outcome = await Api.Monitoring.GetAlertOccurrenceAsync(alertId);
            if (alertId != AlertId) return;
            _alert = outcome.Value;
            _forbidden = outcome.StatusCode == HttpStatusCode.Forbidden;
        }
        catch (HttpRequestException)
        {
            _alert = null;
        }
        finally
        {
            if (alertId == AlertId) _loading = false;
        }
    }
}
