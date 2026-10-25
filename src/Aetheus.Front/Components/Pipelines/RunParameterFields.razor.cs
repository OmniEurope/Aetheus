// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The queue-time parameter inputs, without a dialog around them. Extracted so the plain parameters
/// dialog and the unified launch dialog edit parameters the same way instead of drifting apart.
/// Server-side validation stays authoritative; this only gathers input and checks required fields.
/// </summary>
public partial class RunParameterFields
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public List<PipelineRunParameterDto> Parameters { get; set; } = [];

    /// <summary>
    /// Optional seed values (the source run's parameters on re-run, or the values already chosen)
    /// that override the declared defaults.
    /// </summary>
    [Parameter] public IReadOnlyDictionary<string, string>? Prefill { get; set; }

    /// <summary>
    /// The releases of the pipeline's project a deployment can restore, shown as a picking aid under
    /// the inputs; <c>null</c> when no parameter takes a version (D37). Clicking
    /// one fills the single required-without-default text parameter - the field that forced this
    /// dialog open in the deploy case - and nothing else: the value stays visible, editable and
    /// explicitly confirmed by the user, so M-051 is untouched. With zero or several such fields the
    /// list is informational only.
    /// </summary>
    [Parameter] public List<ReleaseDto>? AvailableReleases { get; set; }

    /// <summary>How many releases the project can restore in all, when more exist than were loaded.</summary>
    [Parameter] public int AvailableReleaseTotal { get; set; }

    private readonly Dictionary<string, string?> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal?> _numbers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// <c>null</c> means "not answered yet": a required boolean with no default must stay unanswered
    /// until the user touches the switch, otherwise seeding <c>false</c> pre-answers it and the
    /// required check can never fail - the confirmation M-051 asks for would be an illusion.
    /// </summary>
    private readonly Dictionary<string, bool?> _bools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The id of a parameter's field, whose Help text OE renders as <c>{id}-help-content</c>.</summary>
    internal static string FieldId(string parameterName) => $"run-param-{parameterName}-field";

    /// <summary>The id of a parameter's description behind the "?" of its field, named by the control's
    /// <c>aria-describedby</c>; <c>null</c> without a description.</summary>
    internal static string? DescriptionId(string parameterName, string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : $"{FieldId(parameterName)}-help-content";

    /// <summary>The message under each field a refused submit found empty; an edit clears its own.</summary>
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    private bool BoolValue(string name) => _bools.GetValueOrDefault(name) ?? false;
    private string? StringValue(string name) => _strings.GetValueOrDefault(name);
    private decimal? NumberValue(string name) => _numbers.GetValueOrDefault(name);
    private string? ErrorOf(string name) => _errors.GetValueOrDefault(name);

    private void SetString(string name, string? value) { _strings[name] = value; _errors.Remove(name); }
    private void SetNumber(string name, decimal? value) { _numbers[name] = value; _errors.Remove(name); }
    private void SetBool(string name, bool value) { _bools[name] = value; _errors.Remove(name); }

    protected override void OnInitialized()
    {
        foreach (var p in Parameters)
        {
            // Prefer the prefill value (source-run snapshot) over the declared default.
            var seed = Prefill is not null && Prefill.TryGetValue(p.Name, out var pv) ? pv : p.Default;
            switch (p.Type?.ToLowerInvariant())
            {
                case "boolean":
                    _bools[p.Name] = bool.TryParse(seed, out var b) ? b : null;
                    break;
                case "number":
                    _numbers[p.Name] = decimal.TryParse(seed, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                        ? d : null;
                    break;
                default:
                    _strings[p.Name] = seed;
                    break;
            }
        }

        // R-22: the version starts on the latest release worth deploying at every opening; it stays
        // editable. A value the caller or the declaration already gave is never overridden, and with no
        // such release the field stays empty rather than starting on a doubtful one.
        if (AvailableReleases is not null && FillTargetName is { } target && string.IsNullOrWhiteSpace(_strings.GetValueOrDefault(target))
            && ReleasePickerVisibility.Latest(AvailableReleases) is { } latest)
        {
            _strings[target] = latest.Version;
        }
    }

    /// <summary>The one field a release click may fill; <c>null</c> when that target is ambiguous.</summary>
    private string? FillTargetName => RunParameterTargets.ReleaseFillTarget(Parameters);

    private void FillFromRelease(string version)
    {
        if (FillTargetName is { } target)
            SetString(target, version);
    }

    /// <summary>The non-blocking warning about the release now in the version field, when the Gate
    /// refused it or graded it F; <c>null</c> for any other release or a version typed by hand.</summary>
    private string? ConcernText
    {
        get
        {
            if (AvailableReleases is null || FillTargetName is not { } target || StringValue(target)?.Trim() is not { Length: > 0 } version)
                return null;
            var release = AvailableReleases.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.Ordinal));
            return release is null ? null : ReleasePickerVisibility.ConcernOf(release) switch
            {
                ReleaseGateConcern.Rejected => L["ReleaseGateWarningRejected"].Value,
                ReleaseGateConcern.GradeF => L["ReleaseGateWarningGradeF"].Value,
                _ => null
            };
        }
    }

    /// <summary>
    /// Reads the current inputs. Returns <c>false</c> when a required parameter is empty: the first
    /// message comes back in <paramref name="error"/> and every empty required field shows its own
    /// under it.
    /// </summary>
    public bool TryCollect(out Dictionary<string, string> values, out string? error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        _errors.Clear();
        foreach (var p in Parameters)
        {
            var value = p.Type?.ToLowerInvariant() switch
            {
                "boolean" => _bools.GetValueOrDefault(p.Name) is { } b
                    ? (b ? "true" : "false")
                    : string.Empty,
                "number" => _numbers.TryGetValue(p.Name, out var d) && d.HasValue
                    ? d.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                _ => _strings.TryGetValue(p.Name, out var s) ? s ?? string.Empty : string.Empty
            };

            if (p.Required && string.IsNullOrWhiteSpace(value))
            {
                var message = string.Format(L["RunParameterRequired"], ParameterText.Pick(p.DisplayName, p.DisplayNameFr));
                _errors[p.Name] = message;
                error ??= message;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
                values[p.Name] = value;
        }

        if (error is null) return true;
        values.Clear();
        _ = InvokeAsync(StateHasChanged);
        return false;
    }
}
