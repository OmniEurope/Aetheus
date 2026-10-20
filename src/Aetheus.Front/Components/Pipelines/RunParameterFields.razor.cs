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

    private bool BoolValue(string name) => _bools.GetValueOrDefault(name) ?? false;
    private string? StringValue(string name) => _strings.GetValueOrDefault(name);
    private decimal? NumberValue(string name) => _numbers.GetValueOrDefault(name);

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
    }

    /// <summary>The one field a release click may fill; <c>null</c> when that target is ambiguous.</summary>
    private string? FillTargetName => RunParameterTargets.ReleaseFillTarget(Parameters);

    private void FillFromRelease(string version)
    {
        if (FillTargetName is { } target)
            _strings[target] = version;
    }

    /// <summary>
    /// Reads the current inputs. Returns <c>false</c> and a message when a required parameter is empty.
    /// </summary>
    public bool TryCollect(out Dictionary<string, string> values, out string? error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
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
                error = string.Format(L["RunParameterRequired"], ParameterText.Pick(p.DisplayName, p.DisplayNameFr));
                return false;
            }

            if (!string.IsNullOrWhiteSpace(value))
                values[p.Name] = value;
        }

        return true;
    }
}
