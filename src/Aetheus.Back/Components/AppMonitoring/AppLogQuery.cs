// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary> The columns of an app's log grid. The grid reads the log page by page from the API, so
/// its header filters and its sort are applied here, to every stored record of the app, and not to
/// the rows already on screen. A request only names keys; the column behind each key is decided
/// here. </summary>
internal static class AppLogQuery
{
    /// <summary>
    /// OTLP severity classes and the severity numbers each one covers (the OTLP data model defines four
    /// numbers per class, 1..24). The severity column filters on these classes rather than on raw numbers.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (int Min, int Max)> SeverityClasses =
        new Dictionary<string, (int Min, int Max)>(StringComparer.OrdinalIgnoreCase)
        {
            ["TRACE"] = (1, 4),
            ["DEBUG"] = (5, 8),
            ["INFO"] = (9, 12),
            ["WARN"] = (13, 16),
            ["ERROR"] = (17, 20),
            ["FATAL"] = (21, 24)
        };

    /// <summary>The header filters: time range, severity class, message text, attributes text.</summary>
    internal static readonly GridQueryMap<AppLogEntry> Filters = new GridQueryMap<AppLogEntry>()
        .Date("timestamp", l => l.Timestamp)
        .Predicate("severity", SeverityFilter)
        .Text("body", l => l.Body)
        .Text("attributesJson", l => l.AttributesJson);

    /// <summary>
    /// The sortable columns. The severity key sorts on the stored severity number, which a filter
    /// column declared as a predicate cannot do, hence a map of its own.
    /// </summary>
    internal static readonly GridQueryMap<AppLogEntry> Sorts = new GridQueryMap<AppLogEntry>()
        .Date("timestamp", l => l.Timestamp)
        .Number("severity", l => l.SeverityNumber)
        .Text("body", l => l.Body);

    /// <summary>The single sort a request carries, or none when it names no column.</summary>
    internal static IReadOnlyList<GridSort>? SortsOf(string? sortBy, bool sortDescending) =>
        string.IsNullOrWhiteSpace(sortBy)
            ? null
            : [new GridSort { Field = sortBy.Trim(), Descending = sortDescending }];

    /// <summary>
    /// A record matches when its severity number falls in one of the named classes (<c>In</c>, from the
    /// column's checkable list, or <c>Equals</c>), or in none of them (<c>NotIn</c>, <c>NotEquals</c>).
    /// An unknown class name is a <see cref="BadRequestException"/>.
    /// </summary>
    private static Expression<Func<AppLogEntry, bool>> SeverityFilter(GridFilter filter)
    {
        IReadOnlyList<string> names = filter.Operator switch
        {
            GridFilterOperator.In or GridFilterOperator.NotIn => GridQueryMap<AppLogEntry>.SplitList(filter.Value),
            GridFilterOperator.Equals or GridFilterOperator.NotEquals when !string.IsNullOrWhiteSpace(filter.Value) => [filter.Value!],
            _ => throw new BadRequestException($"The severity column does not support '{filter.Operator}'.")
        };
        if (names.Count == 0)
            throw new BadRequestException("The filter on 'severity' needs a value.");

        var entry = Expression.Parameter(typeof(AppLogEntry), "log");
        var number = Expression.Property(entry, nameof(AppLogEntry.SeverityNumber));
        Expression? any = null;
        foreach (var name in names)
        {
            if (!SeverityClasses.TryGetValue(name.Trim(), out var range))
                throw new BadRequestException($"'{name}' is not a log severity.");
            var inRange = Expression.AndAlso(
                Expression.GreaterThanOrEqual(number, Expression.Constant(range.Min)),
                Expression.LessThanOrEqual(number, Expression.Constant(range.Max)));
            any = any is null ? inRange : Expression.OrElse(any, inRange);
        }

        var body = filter.Operator is GridFilterOperator.NotIn or GridFilterOperator.NotEquals
            ? Expression.Not(any!)
            : any!;
        return Expression.Lambda<Func<AppLogEntry, bool>>(body, entry);
    }
}
