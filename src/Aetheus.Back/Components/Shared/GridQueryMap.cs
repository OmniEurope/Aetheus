// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Aetheus.Back.Components.Shared;

/// <summary>Allow only endpoint-defined grid keys and apply filters after organisation scope but
/// before counting. Lowercase text comparisons work in both Npgsql and the in-memory unit-test
/// provider.</summary>
public sealed class GridQueryMap<TEntity>
{
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo StringToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private readonly Dictionary<string, Column> _columns = new(StringComparer.OrdinalIgnoreCase);

    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(method => method.Name == nameof(Enumerable.Contains) && method.GetParameters().Length == 2);

    private enum Kind { Text, Number, Date, Enum, Boolean, Custom }

    private sealed record Column(
        string Key,
        LambdaExpression Selector,
        Kind Kind,
        Type ValueType,
        Func<GridFilter, Expression<Func<TEntity, bool>>>? Custom = null);

    public GridQueryMap<TEntity> Text(string key, Expression<Func<TEntity, string?>> selector) =>
        Add(key, selector, Kind.Text, typeof(string));

    public GridQueryMap<TEntity> Number<TNumber>(string key, Expression<Func<TEntity, TNumber>> selector) =>
        Add(key, selector, Kind.Number, typeof(TNumber));

    public GridQueryMap<TEntity> Date<TDate>(string key, Expression<Func<TEntity, TDate>> selector) =>
        Add(key, selector, Kind.Date, typeof(TDate));

    public GridQueryMap<TEntity> Enum<TEnum>(string key, Expression<Func<TEntity, TEnum>> selector) =>
        Add(key, selector, Kind.Enum, typeof(TEnum));

    public GridQueryMap<TEntity> Boolean<TBool>(string key, Expression<Func<TEntity, TBool>> selector) =>
        Add(key, selector, Kind.Boolean, typeof(TBool));

    /// <summary>
    /// A column whose filter is not a comparison of one stored value (tags kept as JSON, a state
    /// computed from several fields): the endpoint builds the whole predicate from the filter. It
    /// cannot be sorted on.
    /// </summary>
    public GridQueryMap<TEntity> Predicate(string key, Func<GridFilter, Expression<Func<TEntity, bool>>> predicate)
    {
        Expression<Func<TEntity, bool>> none = _ => true;
        if (!_columns.TryAdd(key, new Column(key, none, Kind.Custom, typeof(bool), predicate)))
            throw new InvalidOperationException($"Grid column '{key}' is declared twice.");
        return this;
    }

    /// <summary>The candidates of an <see cref="GridFilterOperator.In"/> value, bounded.</summary>
    public static IReadOnlyList<string> SplitList(string? value)
    {
        var items = (value ?? string.Empty).Split(GridFilter.ListSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (items.Length > GridFilter.MaximumListCount)
            throw new BadRequestException($"A list filter carries at most {GridFilter.MaximumListCount} values.");
        if (items.Any(item => item.Length > GridFilter.MaximumValueLength))
            throw new BadRequestException($"A filter value is limited to {GridFilter.MaximumValueLength} characters.");
        return items;
    }

    /// <summary>The keys a grid may use, for the guard that proves every filterable column has one.</summary>
    public IReadOnlyCollection<string> Keys => _columns.Keys;

    /// <summary>
    /// Applies every filter, all of them required to match. An unknown key, an operator the column's
    /// type does not support or a value that does not parse is a <see cref="BadRequestException"/>.
    /// </summary>
    public IQueryable<TEntity> ApplyFilters(IQueryable<TEntity> query, IReadOnlyList<GridFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
            return query;
        if (filters.Count > PaginationRequest.MaxFilters)
            throw new BadRequestException($"At most {PaginationRequest.MaxFilters} column filters are accepted.");

        foreach (var filter in filters)
            query = query.Where(Predicate(filter));
        return query;
    }

    /// <summary>
    /// Orders by the requested keys, first key first, or returns null when none was requested so the
    /// caller keeps its own default order. An unknown key is a <see cref="BadRequestException"/>.
    /// </summary>
    public IOrderedQueryable<TEntity>? ApplySorts(IQueryable<TEntity> query, IReadOnlyList<GridSort>? sorts)
    {
        if (sorts is null || sorts.Count == 0)
            return null;
        if (sorts.Count > PaginationRequest.MaxSorts)
            throw new BadRequestException($"At most {PaginationRequest.MaxSorts} sort keys are accepted.");

        IOrderedQueryable<TEntity>? ordered = null;
        foreach (var sort in sorts)
        {
            var column = Find(sort.Field);
            if (column.Kind == Kind.Custom)
                throw new BadRequestException($"'{sort.Field}' cannot be sorted on.");
            var method = (ordered, sort.Descending) switch
            {
                (null, false) => nameof(Queryable.OrderBy),
                (null, true) => nameof(Queryable.OrderByDescending),
                (_, false) => nameof(Queryable.ThenBy),
                (_, true) => nameof(Queryable.ThenByDescending)
            };
            var call = Expression.Call(
                typeof(Queryable), method, [typeof(TEntity), column.ValueType],
                (ordered ?? query).Expression, Expression.Quote(column.Selector));
            ordered = (IOrderedQueryable<TEntity>)query.Provider.CreateQuery<TEntity>(call);
        }

        return ordered;
    }

    private GridQueryMap<TEntity> Add(string key, LambdaExpression selector, Kind kind, Type valueType)
    {
        if (!_columns.TryAdd(key, new Column(key, selector, kind, valueType)))
            throw new InvalidOperationException($"Grid column '{key}' is declared twice.");
        return this;
    }

    private Column Find(string key) =>
        _columns.TryGetValue(key ?? string.Empty, out var column)
            ? column
            : throw new BadRequestException($"'{key}' is not a column this grid can filter or sort on.");

    private Expression<Func<TEntity, bool>> Predicate(GridFilter filter)
    {
        var column = Find(filter.Field);
        if (column.Custom is { } custom)
            return custom(filter);
        var parameter = column.Selector.Parameters[0];
        var first = Condition(column, filter.Operator, filter.Value);
        var body = filter.SecondOperator is { } second
            ? filter.Logic == GridFilterLogic.Or
                ? Expression.OrElse(first, Condition(column, second, filter.SecondValue))
                : Expression.AndAlso(first, Condition(column, second, filter.SecondValue))
            : first;
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private static Expression Condition(Column column, GridFilterOperator op, string? value)
    {
        var member = column.Selector.Body;
        if (op is GridFilterOperator.In or GridFilterOperator.NotIn)
        {
            var any = ListCondition(column, member, SplitList(value));
            return op == GridFilterOperator.In ? any : Expression.Not(any);
        }

        if (value is { Length: > GridFilter.MaximumValueLength })
            throw new BadRequestException($"A filter value is limited to {GridFilter.MaximumValueLength} characters.");

        var nullable = !column.ValueType.IsValueType || Nullable.GetUnderlyingType(column.ValueType) is not null;
        switch (op)
        {
            case GridFilterOperator.IsNull when nullable:
                return Expression.Equal(member, Expression.Constant(null, column.ValueType));
            case GridFilterOperator.IsNotNull when nullable:
                return Expression.NotEqual(member, Expression.Constant(null, column.ValueType));
            case GridFilterOperator.IsNull:
                return Expression.Constant(false);
            case GridFilterOperator.IsNotNull:
                return Expression.Constant(true);
        }

        return column.Kind == Kind.Text ? TextCondition(member, op, value) : ValueCondition(column, member, op, value);
    }

    /// <summary> The column equals one of the candidates. Text compares case-insensitively, the
    /// other kinds parse each candidate as a single Equals value would be parsed. The list reaches
    /// SQL as one parameter (<c>= ANY</c> on Npgsql). </summary>
    private static Expression ListCondition(Column column, Expression member, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            throw new BadRequestException($"The filter on '{column.Key}' needs a value.");

        if (column.Kind == Kind.Text)
        {
            var lowered = values.Select(candidate => candidate.ToLowerInvariant()).Distinct().ToList();
            var isNotNull = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));
            var contains = Expression.Call(
                EnumerableContains.MakeGenericMethod(typeof(string)),
                Expression.Constant(lowered),
                Expression.Call(member, StringToLower));
            return Expression.AndAlso(isNotNull, contains);
        }

        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(column.ValueType))!;
        foreach (var candidate in values)
            list.Add(Parse(column, candidate));
        return Expression.Call(
            EnumerableContains.MakeGenericMethod(column.ValueType),
            Expression.Constant(list),
            member);
    }

    private static Expression TextCondition(Expression member, GridFilterOperator op, string? value)
    {
        var isNull = Expression.Equal(member, Expression.Constant(null, typeof(string)));
        var isEmpty = Expression.OrElse(isNull, Expression.Equal(member, Expression.Constant(string.Empty)));
        if (op == GridFilterOperator.IsEmpty) return isEmpty;
        if (op == GridFilterOperator.IsNotEmpty) return Expression.Not(isEmpty);

        var needle = Expression.Constant((value ?? string.Empty).ToLowerInvariant());
        var lowered = Expression.Call(member, StringToLower);
        var notNull = Expression.Not(isNull);
        return op switch
        {
            GridFilterOperator.Contains => Expression.AndAlso(notNull, Expression.Call(lowered, StringContains, needle)),
            GridFilterOperator.DoesNotContain => Expression.OrElse(isNull, Expression.Not(Expression.Call(lowered, StringContains, needle))),
            GridFilterOperator.StartsWith => Expression.AndAlso(notNull, Expression.Call(lowered, StringStartsWith, needle)),
            GridFilterOperator.EndsWith => Expression.AndAlso(notNull, Expression.Call(lowered, StringEndsWith, needle)),
            GridFilterOperator.Equals => Expression.AndAlso(notNull, Expression.Equal(lowered, needle)),
            GridFilterOperator.NotEquals => Expression.OrElse(isNull, Expression.NotEqual(lowered, needle)),
            _ => throw new BadRequestException($"A text column does not support '{op}'.")
        };
    }

    private static Expression ValueCondition(Column column, Expression member, GridFilterOperator op, string? value)
    {
        var ordered = column.Kind is Kind.Number or Kind.Date;
        var supported = op is GridFilterOperator.Equals or GridFilterOperator.NotEquals
            || ordered && op is GridFilterOperator.GreaterThan or GridFilterOperator.GreaterThanOrEqual
                or GridFilterOperator.LessThan or GridFilterOperator.LessThanOrEqual;
        if (!supported)
            throw new BadRequestException($"A {column.Kind.ToString().ToLowerInvariant()} column does not support '{op}'.");

        var constant = Expression.Constant(Parse(column, value), column.ValueType);
        return op switch
        {
            GridFilterOperator.Equals => Expression.Equal(member, constant),
            GridFilterOperator.NotEquals => Expression.NotEqual(member, constant),
            GridFilterOperator.GreaterThan => Expression.GreaterThan(member, constant),
            GridFilterOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(member, constant),
            GridFilterOperator.LessThan => Expression.LessThan(member, constant),
            _ => Expression.LessThanOrEqual(member, constant)
        };
    }

    private static object Parse(Column column, string? value)
    {
        var type = Nullable.GetUnderlyingType(column.ValueType) ?? column.ValueType;
        if (string.IsNullOrWhiteSpace(value))
            throw new BadRequestException($"The filter on '{column.Key}' needs a value.");

        try
        {
            return column.Kind switch
            {
                Kind.Enum when System.Enum.TryParse(type, value, ignoreCase: true, out var parsed)
                               && System.Enum.IsDefined(type, parsed!) => parsed!,
                Kind.Boolean => bool.Parse(value),
                Kind.Date when type == typeof(DateTimeOffset) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                Kind.Date => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                Kind.Number => Convert.ChangeType(value, type, CultureInfo.InvariantCulture),
                _ => throw new FormatException()
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new BadRequestException($"'{value}' is not a valid value for '{column.Key}'.");
        }
    }
}
