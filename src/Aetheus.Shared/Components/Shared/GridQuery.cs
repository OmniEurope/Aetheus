// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Shared;

/// <summary>Defines grid comparison operators, with accepted combinations determined by column
/// type. Unsupported operators return HTTP 400.</summary>
public enum GridFilterOperator
{
    Contains,
    DoesNotContain,
    Equals,
    NotEquals,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    IsNull,
    IsNotNull,
    IsEmpty,
    IsNotEmpty,

    /// <summary> The value carries several candidates, joined by <see
    /// cref="GridFilter.ListSeparator"/>, and a row matches any of them (a column's checkable
    /// header filter). </summary>
    In,

    /// <summary>A row matches none of the candidates of <see cref="In"/>.</summary>
    NotIn
}

/// <summary>How a filter's second condition joins its first one.</summary>
public enum GridFilterLogic
{
    And,
    Or
}

/// <summary>
/// One column filter of a server-side grid. <see cref="Field"/> is a key the endpoint declares in its
/// allow-list, never a property path: an unknown key is a 400, not a query. Values travel as text in
/// the query string; dates are ISO 8601 in UTC, numbers use the invariant culture.
/// </summary>
public sealed record GridFilter
{
    public const int MaximumValueLength = 200;

    /// <summary>Most candidates one <see cref="GridFilterOperator.In"/> filter may carry.</summary>
    public const int MaximumListCount = 50;

    /// <summary>Longest encoded value: a list of candidates, each at most <see cref="MaximumValueLength"/>.</summary>
    public const int MaximumEncodedLength = 4000;

    /// <summary>Joins the candidates of an <see cref="GridFilterOperator.In"/> value: the ASCII unit
    /// separator, which no typed value contains, as the grid of the front writes it.</summary>
    public const char ListSeparator = (char)0x1F;

    [Required]
    [StringLength(64)]
    public string Field { get; init; } = string.Empty;

    public GridFilterOperator Operator { get; init; }

    [StringLength(MaximumEncodedLength)]
    public string? Value { get; init; }

    /// <summary>Optional second condition on the same column, joined by <see cref="Logic"/>.</summary>
    public GridFilterOperator? SecondOperator { get; init; }

    [StringLength(MaximumValueLength)]
    public string? SecondValue { get; init; }

    public GridFilterLogic Logic { get; init; } = GridFilterLogic.And;
}

/// <summary>One sort key of a server-side grid, first key first. The field is an allow-list key.</summary>
public sealed record GridSort
{
    [Required]
    [StringLength(64)]
    public string Field { get; init; } = string.Empty;

    public bool Descending { get; init; }
}
