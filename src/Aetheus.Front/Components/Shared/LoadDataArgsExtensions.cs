// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Helpers for converting grid <see cref="GridLoadArgs"/> into 1-based page / page-size
/// pairs expected by Aetheus paginated API endpoints.
/// </summary>
public static class GridLoadArgsExtensions
{
    public const int DefaultPageSize = PaginationDefaults.DefaultPageSize;

    /// <summary>Returns 1-based page number computed from <c>Skip</c>/<c>Top</c>.</summary>
    public static int GetPage(this GridLoadArgs args, int defaultPageSize = DefaultPageSize)
    {
        var top = args.GetPageSize(defaultPageSize);
        return ((args.Skip ?? 0) / top) + 1;
    }

    /// <summary>Returns the page size, falling back to the default.</summary>
    public static int GetPageSize(this GridLoadArgs args, int defaultPageSize = DefaultPageSize)
        => PaginationDefaults.Clamp(args.Top ?? PaginationDefaults.Clamp(defaultPageSize));

    /// <summary>Returns a <c>(page, pageSize)</c> pair.</summary>
    public static (int Page, int PageSize) ToPageRequest(this GridLoadArgs args, int defaultPageSize = DefaultPageSize)
        => (args.GetPage(defaultPageSize), args.GetPageSize(defaultPageSize));

    /// <summary>Apply sorting and column filters to the complete in-memory collection before
    /// slicing its page so header controls affect all rows.</summary>
    public static List<T> ToClientPage<T>(
        this GridLoadArgs args,
        IReadOnlyList<T> source,
        int defaultPageSize = DefaultPageSize)
    {
        var rows = ApplyClientSort(ApplyClientFilters(source, args), args);
        var skip = args.Skip ?? 0;
        return rows.Skip(skip).Take(args.GetPageSize(defaultPageSize)).ToList();
    }

    /// <summary>Number of rows left once the grid's column filters are applied. Callers use it for the
    /// pager count, so the pager measures what is reachable rather than the unfiltered collection.
    /// </summary>
    public static int ClientFilteredCount<T>(this GridLoadArgs args, IReadOnlyList<T> source)
        => ApplyClientFilters(source, args).Count();

    private static IEnumerable<T> ApplyClientFilters<T>(IReadOnlyList<T> source, GridLoadArgs args)
    {
        if (args.Filters is null) return source;

        IEnumerable<T> rows = source;
        foreach (var filter in args.Filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Property)) continue;
            var property = typeof(T).GetProperty(filter.Property);
            // A filter naming a property this row type does not have narrows nothing. Dropping the row
            // set instead would show an empty table for a column the user can see data in.
            if (property is null) continue;

            rows = rows.Where(row => GridClientFilter.Matches(property.GetValue(row), filter));
        }

        return rows;
    }

    private static bool TryParseBound(string? text, out DateTime value) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static IEnumerable<T> ApplyClientSort<T>(IEnumerable<T> rows, GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return rows;

        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var property = typeof(T).GetProperty(parts[0]);
        if (property is null) return rows;

        var descending = parts.Length > 1 && string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);
        return descending
            ? rows.OrderByDescending(row => property.GetValue(row))
            : rows.OrderBy(row => property.GetValue(row));
    }

    /// <summary>Returns the first sort column and direction, or the supplied fallback.</summary>
    public static (string SortBy, bool Descending) ToSortRequest(
        this GridLoadArgs args,
        string fallback,
        bool fallbackDescending = false)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return (fallback, fallbackDescending);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1
            && string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the value the user typed into one column's header filter, or null when that
    /// column is unfiltered. Property names are matched case-insensitively so a caller can pass the
    /// DTO property name regardless of how the column declared it.</summary>
    public static string? ColumnFilter(this GridLoadArgs args, string property)
    {
        var value = args.Filters?
            .FirstOrDefault(f => string.Equals(f.Property, property, StringComparison.OrdinalIgnoreCase))?
            .FilterValue?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Every value ticked in one column's checkable header filter (a single value for any
    /// other filter shape), empty when the column is unfiltered.</summary>
    public static IReadOnlyList<string> ColumnFilterValues(this GridLoadArgs args, string property)
    {
        var filter = Find(args, property);
        var raw = filter?.FilterValue?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return OmniDataGridFilterValues.IsMultiValued(filter!.Operator) ? OmniDataGridFilterValues.Split(raw) : [raw];
    }

    /// <summary>Same as <see cref="ColumnFilterValues(GridLoadArgs, string)"/> for a column bound to an
    /// enum. A value that names no member is skipped, so a stale filter narrows by what still exists.</summary>
    public static IReadOnlyList<TEnum> ColumnFilterValues<TEnum>(this GridLoadArgs args, string property) where TEnum : struct, Enum =>
        [.. args.ColumnFilterValues(property)
            .Select(value => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : (TEnum?)null)
            .OfType<TEnum>()
            .Distinct()];

    /// <summary>The bounds of one column's date range header filter, an inclusive start and an
    /// exclusive end, either null when that side is open. The grid resolves the whole-day rule; the
    /// bounds are the dates the column shows, which are UTC, so they come back as UTC.</summary>
    public static (DateTime? From, DateTime? ToExclusive) ColumnDateRange(this GridLoadArgs args, string property)
    {
        var filter = Find(args, property);
        if (filter is null) return (null, null);
        DateTime? from = null;
        DateTime? to = null;
        Take(filter.Operator, filter.FilterValue?.ToString());
        if (filter.SecondOperator is { } second) Take(second, filter.SecondValue);
        return (from, to);

        void Take(OmniDataGridFilterOperator bound, string? text)
        {
            if (!TryParseBound(text, out var value)) return;
            var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
            if (bound == OmniDataGridFilterOperator.GreaterThanOrEquals) from = utc;
            else if (bound == OmniDataGridFilterOperator.LessThan) to = utc;
        }
    }

    /// <summary>
    /// One column's number header filter on whole numbers (an identifier), as inclusive bounds and one
    /// value left out: equals gives both bounds, greater/less (or equal) one each, not-equal the
    /// excluded value; a second condition narrows further. All null when the column is unfiltered or
    /// its value is not a number. A fractional value is rounded the way the comparison needs (more
    /// than 4.5 is from 5); an "equals 4.5" matches no identifier, so it gives an empty range.
    /// </summary>
    public static (int? From, int? To, int? Not) ColumnWholeNumberRange(this GridLoadArgs args, string property)
    {
        var filter = Find(args, property);
        if (filter is null) return (null, null, null);
        long? from = null;
        long? to = null;
        int? not = null;
        Take(filter.Operator, filter.FilterValue?.ToString());
        if (filter.SecondOperator is { } second) Take(second, filter.SecondValue);
        return (Clamp(from), Clamp(to), not);

        void Take(OmniDataGridFilterOperator comparison, string? text)
        {
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return;
            var floor = (long)Math.Floor(value);
            var ceiling = (long)Math.Ceiling(value);
            switch (comparison)
            {
                case OmniDataGridFilterOperator.Equals when floor == ceiling:
                    Lower(floor);
                    Upper(floor);
                    break;
                case OmniDataGridFilterOperator.Equals:
                    Lower(ceiling);
                    Upper(floor);
                    break;
                case OmniDataGridFilterOperator.NotEquals when floor == ceiling && floor is >= int.MinValue and <= int.MaxValue:
                    not = (int)floor;
                    break;
                case OmniDataGridFilterOperator.GreaterThan:
                    Lower(floor + 1);
                    break;
                case OmniDataGridFilterOperator.GreaterThanOrEquals:
                    Lower(ceiling);
                    break;
                case OmniDataGridFilterOperator.LessThan:
                    Upper(ceiling - 1);
                    break;
                case OmniDataGridFilterOperator.LessThanOrEquals:
                    Upper(floor);
                    break;
            }
        }

        void Lower(long bound) => from = from is { } current ? Math.Max(current, bound) : bound;
        void Upper(long bound) => to = to is { } current ? Math.Min(current, bound) : bound;
        static int? Clamp(long? bound) => bound is { } value ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : null;
    }

    private static GridFilterDescriptor? Find(GridLoadArgs args, string property) =>
        args.Filters?.FirstOrDefault(f => string.Equals(f.Property, property, StringComparison.OrdinalIgnoreCase));

    /// <summary>Same as <see cref="ColumnFilter"/> for a column bound to an enum. Returns null when the
    /// column is unfiltered or when the value does not name a member, so an unparsable filter narrows
    /// nothing instead of silently returning an empty page.</summary>
    public static TEnum? ColumnFilter<TEnum>(this GridLoadArgs args, string property) where TEnum : struct, Enum
    {
        var raw = args.Filters?
            .FirstOrDefault(f => string.Equals(f.Property, property, StringComparison.OrdinalIgnoreCase))?
            .FilterValue;
        return raw switch
        {
            null => null,
            TEnum typed => typed,
            _ => Enum.TryParse<TEnum>(raw.ToString(), ignoreCase: true, out var parsed) ? parsed : null
        };
    }
}
