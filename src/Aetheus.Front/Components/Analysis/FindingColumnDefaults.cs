// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary> The filters the findings grid's columns open with. Open findings by default, or what a
/// summary shortcut asked for; either way they sit in the column headers, where the user sees and
/// lifts them, rather than in a hidden query parameter. </summary>
public sealed record FindingColumnDefaults(AnalysisFindingStatus? StatusValue, AnalysisSeverity? SeverityValue, bool? IsNewValue)
{
    /// <summary>Key of the "new" column: the flag is read from the latest occurrence.</summary>
    public const string IsNewColumn = "IsNew";

    public static FindingColumnDefaults OpenOnly { get; } = new(AnalysisFindingStatus.Open, null, null);

    public string? Status => StatusValue?.ToString();
    public string? Severity => SeverityValue?.ToString();
    public string? IsNew => IsNewValue?.ToString();

    /// <summary>Column key to filter value, a null value clearing that column.</summary>
    public IReadOnlyDictionary<string, string?> AsColumnValues() => new Dictionary<string, string?>
    {
        [nameof(AnalysisFindingDto.Status)] = Status,
        [nameof(AnalysisFindingDto.Severity)] = Severity,
        [IsNewColumn] = IsNew
    };

    /// <summary>The same filters as the grid sends them for these columns (checkable lists for status
    /// and severity, a closed choice for "new").</summary>
    public IReadOnlyList<GridFilterDescriptor> AsFilters() => [.. AsColumnValues()
        .Where(pair => pair.Value is not null)
        .Select(pair => new GridFilterDescriptor(pair.Key, pair.Value,
            pair.Key == IsNewColumn ? OmniDataGridFilterOperator.Equals : OmniDataGridFilterOperator.In))];
}
