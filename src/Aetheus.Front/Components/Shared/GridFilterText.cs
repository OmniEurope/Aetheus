// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Localize only the displayed filter label while keeping raw values unchanged for the
/// API; retain each function instance so grid renders see stable delegates.</summary>
public static class GridFilterText
{
    /// <summary>The two values of a yes/no column, in the order the filter lists them.</summary>
    public static IReadOnlyList<string> Booleans { get; } = [bool.TrueString, bool.FalseString];

    /// <summary>An enum member's <c>Enum_{Type}_{Member}</c> resource, or the member name while that
    /// resource does not exist.</summary>
    public static Func<string, string> ForEnum<TEnum>(IStringLocalizer localizer) where TEnum : struct, Enum =>
        value => Enum.TryParse<TEnum>(value, ignoreCase: true, out var member)
            && localizer[$"Enum_{typeof(TEnum).Name}_{member}"] is { ResourceNotFound: false } text
                ? text.Value
                : value;

    /// <summary> The operators of a number column, equality first. OE's Number filter also offers
    /// "contains" on the figure as shown, which the API's number columns (GridQueryMap.Number)
    /// refuse; the same list everywhere keeps local and remote grids alike. </summary>
    public static IReadOnlyList<OmniDataGridFilterOperator> NumberOperators { get; } =
    [
        OmniDataGridFilterOperator.Equals,
        OmniDataGridFilterOperator.NotEquals,
        OmniDataGridFilterOperator.GreaterThan,
        OmniDataGridFilterOperator.GreaterThanOrEquals,
        OmniDataGridFilterOperator.LessThan,
        OmniDataGridFilterOperator.LessThanOrEquals
    ];

    /// <summary>Yes or No for <c>True</c> and <c>False</c>.</summary>
    public static Func<string, string> YesNo(IStringLocalizer localizer) =>
        value => bool.TryParse(value, out var flag) ? localizer[flag ? "Yes" : "No"].Value : value;
}
