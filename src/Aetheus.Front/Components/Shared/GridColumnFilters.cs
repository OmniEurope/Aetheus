// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using ApiGridFilter = Aetheus.Shared.Components.Shared.GridFilter;
using ApiGridFilterOperator = Aetheus.Shared.Components.Shared.GridFilterOperator;

namespace Aetheus.Front.Components.Shared;

/// <summary>Send grid column filters through the generic API contract so endpoints can enforce
/// their field allow-list; encode checked values with the unit separator and date ranges as
/// resolved bounds.</summary>
public static class GridColumnFilters
{
    /// <summary>The grid's header filters as API column filters, the columns in <paramref name="except"/>
    /// left out (a page that still maps one of them to a typed parameter of its own).</summary>
    public static List<ApiGridFilter> ToApiFilters(this GridLoadArgs args, params string[] except) =>
        [.. (args.Filters ?? [])
            .Where(filter => !string.IsNullOrWhiteSpace(filter.Property)
                && !except.Contains(filter.Property, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(filter.FilterValue?.ToString()))
            .Select(filter => new ApiGridFilter
            {
                Field = filter.Property,
                Operator = Map(filter.Operator),
                Value = ApiValue(filter.FilterValue?.ToString(), filter.Operator),
                SecondOperator = filter.SecondOperator is { } second ? Map(second) : null,
                SecondValue = ApiValue(filter.SecondValue, filter.SecondOperator)
            })];

    private static string? ApiValue(string? value, OmniDataGridFilterOperator? operation) =>
        operation is OmniDataGridFilterOperator.GreaterThanOrEquals or OmniDataGridFilterOperator.LessThan
        && DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : value;

    /// <summary>Appends the filters in the indexed form ASP.NET binds to a list of records.</summary>
    public static string AddTo(string url, IReadOnlyList<ApiGridFilter>? filters)
    {
        if (filters is null) return url;
        for (var index = 0; index < filters.Count; index++)
        {
            var filter = filters[index];
            var prefix = $"Filters[{index}].";
            url = QueryHelpers.AddQueryString(url, prefix + nameof(ApiGridFilter.Field), filter.Field);
            url = QueryHelpers.AddQueryString(url, prefix + nameof(ApiGridFilter.Operator), filter.Operator.ToString());
            if (filter.Value is not null)
                url = QueryHelpers.AddQueryString(url, prefix + nameof(ApiGridFilter.Value), filter.Value);
            if (filter.SecondOperator is { } second)
            {
                url = QueryHelpers.AddQueryString(url, prefix + nameof(ApiGridFilter.SecondOperator), second.ToString());
                url = QueryHelpers.AddQueryString(url, prefix + nameof(ApiGridFilter.Logic), filter.Logic.ToString());
                if (filter.SecondValue is not null)
                    url = QueryHelpers.AddQueryString(url, prefix + nameof(ApiGridFilter.SecondValue), filter.SecondValue);
            }
        }

        return url;
    }

    /// <summary>A stable text for a cache key: the same filters always read the same.</summary>
    public static string CacheText(IReadOnlyList<ApiGridFilter>? filters) => filters is null || filters.Count == 0
        ? string.Empty
        : string.Join('|', filters.Select(filter => $"{filter.Field}:{filter.Operator}:{filter.Value}:{filter.SecondOperator}:{filter.SecondValue}"));

    private static readonly IReadOnlyDictionary<OmniDataGridFilterOperator, ApiGridFilterOperator> Operators =
        new Dictionary<OmniDataGridFilterOperator, ApiGridFilterOperator>
        {
            [OmniDataGridFilterOperator.Contains] = ApiGridFilterOperator.Contains,
            [OmniDataGridFilterOperator.DoesNotContain] = ApiGridFilterOperator.DoesNotContain,
            [OmniDataGridFilterOperator.Equals] = ApiGridFilterOperator.Equals,
            [OmniDataGridFilterOperator.NotEquals] = ApiGridFilterOperator.NotEquals,
            [OmniDataGridFilterOperator.StartsWith] = ApiGridFilterOperator.StartsWith,
            [OmniDataGridFilterOperator.EndsWith] = ApiGridFilterOperator.EndsWith,
            [OmniDataGridFilterOperator.GreaterThan] = ApiGridFilterOperator.GreaterThan,
            [OmniDataGridFilterOperator.GreaterThanOrEquals] = ApiGridFilterOperator.GreaterThanOrEqual,
            [OmniDataGridFilterOperator.LessThan] = ApiGridFilterOperator.LessThan,
            [OmniDataGridFilterOperator.LessThanOrEquals] = ApiGridFilterOperator.LessThanOrEqual,
            [OmniDataGridFilterOperator.IsNull] = ApiGridFilterOperator.IsNull,
            [OmniDataGridFilterOperator.IsNotNull] = ApiGridFilterOperator.IsNotNull,
            [OmniDataGridFilterOperator.IsEmpty] = ApiGridFilterOperator.IsEmpty,
            [OmniDataGridFilterOperator.IsNotEmpty] = ApiGridFilterOperator.IsNotEmpty,
            [OmniDataGridFilterOperator.In] = ApiGridFilterOperator.In,
            [OmniDataGridFilterOperator.NotIn] = ApiGridFilterOperator.NotIn
        };

    private static ApiGridFilterOperator Map(OmniDataGridFilterOperator candidate) =>
        Operators.TryGetValue(candidate, out var mapped)
            ? mapped
            : throw new ArgumentOutOfRangeException(nameof(candidate), candidate, "Unmapped grid filter operator.");
}
