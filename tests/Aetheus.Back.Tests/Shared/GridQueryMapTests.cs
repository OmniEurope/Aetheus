// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests;

public sealed class GridQueryMapTests
{
    private enum Phase { Queued, Running, Done }

    private sealed record Row(int Id, string? Name, DateTime? At, Phase Phase, bool Enabled, decimal Amount);

    private static readonly GridQueryMap<Row> Map = new GridQueryMap<Row>()
        .Number("id", row => row.Id)
        .Number("amount", row => row.Amount)
        .Text("name", row => row.Name)
        .Date("at", row => row.At)
        .Enum("phase", row => row.Phase)
        .Boolean("enabled", row => row.Enabled);

    private static readonly Row[] Rows =
    [
        new(1, "Alpha build", new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), Phase.Done, true, 10.5m),
        new(2, "beta deploy", new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc), Phase.Running, false, 20m),
        new(3, null, null, Phase.Queued, true, 0m),
        new(4, "", new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc), Phase.Done, false, 99.99m)
    ];

    private static int[] Ids(params GridFilter[] filters) =>
        Map.ApplyFilters(Rows.AsQueryable(), filters).Select(row => row.Id).OrderBy(id => id).ToArray();

    private static GridFilter F(string field, GridFilterOperator op, string? value = null) =>
        new() { Field = field, Operator = op, Value = value };

    [Theory]
    [InlineData(GridFilterOperator.Contains, "BUILD", new[] { 1 })]
    [InlineData(GridFilterOperator.DoesNotContain, "build", new[] { 2, 3, 4 })]
    [InlineData(GridFilterOperator.StartsWith, "be", new[] { 2 })]
    [InlineData(GridFilterOperator.EndsWith, "DEPLOY", new[] { 2 })]
    [InlineData(GridFilterOperator.Equals, "alpha build", new[] { 1 })]
    [InlineData(GridFilterOperator.NotEquals, "alpha build", new[] { 2, 3, 4 })]
    [InlineData(GridFilterOperator.IsNull, null, new[] { 3 })]
    [InlineData(GridFilterOperator.IsNotNull, null, new[] { 1, 2, 4 })]
    [InlineData(GridFilterOperator.IsEmpty, null, new[] { 3, 4 })]
    [InlineData(GridFilterOperator.IsNotEmpty, null, new[] { 1, 2 })]
    public void TextOperators_CompareWithoutCase(GridFilterOperator op, string? value, int[] expected) =>
        Assert.Equal(expected, Ids(F("name", op, value)));

    [Theory]
    [InlineData(GridFilterOperator.Equals, "20", new[] { 2 })]
    [InlineData(GridFilterOperator.NotEquals, "20", new[] { 1, 3, 4 })]
    [InlineData(GridFilterOperator.GreaterThan, "10.5", new[] { 2, 4 })]
    [InlineData(GridFilterOperator.GreaterThanOrEqual, "10.5", new[] { 1, 2, 4 })]
    [InlineData(GridFilterOperator.LessThan, "10.5", new[] { 3 })]
    [InlineData(GridFilterOperator.LessThanOrEqual, "10.5", new[] { 1, 3 })]
    public void NumberOperators_ReadTheInvariantCulture(GridFilterOperator op, string value, int[] expected) =>
        Assert.Equal(expected, Ids(F("amount", op, value)));

    [Fact]
    public void DateOperators_ReadIso8601InUtc_AndNullsNeverMatchARange()
    {
        Assert.Equal([2, 4], Ids(F("at", GridFilterOperator.GreaterThanOrEqual, "2026-09-02T00:00:00Z")));
        Assert.Equal([1], Ids(F("at", GridFilterOperator.LessThan, "2026-09-02T00:00:00Z")));
        Assert.Equal([3], Ids(F("at", GridFilterOperator.IsNull)));
    }

    [Fact]
    public void EnumAndBooleanColumns_MatchByNameOrValue()
    {
        Assert.Equal([1, 4], Ids(F("phase", GridFilterOperator.Equals, "done")));
        Assert.Equal([2, 3], Ids(F("phase", GridFilterOperator.NotEquals, "Done")));
        Assert.Equal([1, 3], Ids(F("enabled", GridFilterOperator.Equals, "true")));
    }

    [Fact]
    public void ASecondCondition_JoinsTheFirstWithAndOrOr()
    {
        var or = new GridFilter { Field = "id", Operator = GridFilterOperator.Equals, Value = "1", SecondOperator = GridFilterOperator.Equals, SecondValue = "3", Logic = GridFilterLogic.Or };
        var and = new GridFilter { Field = "amount", Operator = GridFilterOperator.GreaterThan, Value = "5", SecondOperator = GridFilterOperator.LessThan, SecondValue = "50", Logic = GridFilterLogic.And };

        Assert.Equal([1, 3], Ids(or));
        Assert.Equal([1, 2], Ids(and));
    }

    /// <summary>A checkable list keeps the rows equal to any ticked value, text without case, enums
    /// and numbers parsed as an Equals value would be; NotIn keeps the others.</summary>
    [Fact]
    public void AListFilter_MatchesAnyOfItsValues()
    {
        Assert.Equal([1, 4], Ids(F("phase", GridFilterOperator.In, "Done")));
        Assert.Equal([1, 2, 4], Ids(F("phase", GridFilterOperator.In, "done\u001FRunning")));
        Assert.Equal([3], Ids(F("phase", GridFilterOperator.NotIn, "Done\u001FRunning")));
        Assert.Equal([1, 2], Ids(F("name", GridFilterOperator.In, "ALPHA BUILD\u001Fbeta deploy")));
        Assert.Equal([2, 3], Ids(F("id", GridFilterOperator.In, "2\u001F3")));
    }

    [Fact]
    public void AListFilter_IsBounded_AndRefusesAValueItsColumnCannotParse()
    {
        var tooMany = string.Join(GridFilter.ListSeparator, Enumerable.Range(0, GridFilter.MaximumListCount + 1));
        Assert.Throws<BadRequestException>(() => Ids(F("id", GridFilterOperator.In, tooMany)));
        Assert.Throws<BadRequestException>(() => Ids(F("phase", GridFilterOperator.In, "Done\u001FSleeping")));
        Assert.Throws<BadRequestException>(() => Ids(F("phase", GridFilterOperator.In, string.Empty)));
    }

    [Fact]
    public void APredicateColumn_BuildsItsOwnCondition_AndCannotBeSorted()
    {
        var map = new GridQueryMap<Row>().Predicate("odd", filter => row => row.Id % 2 == 1);

        Assert.Equal([1, 3], map.ApplyFilters(Rows.AsQueryable(), [F("odd", GridFilterOperator.Equals, "x")]).Select(row => row.Id).ToArray());
        Assert.Throws<BadRequestException>(() => map.ApplySorts(Rows.AsQueryable(), [new GridSort { Field = "odd" }]));
    }

    [Fact]
    public void SeveralFilters_MustAllMatch() =>
        Assert.Equal([4], Ids(F("phase", GridFilterOperator.Equals, "Done"), F("enabled", GridFilterOperator.Equals, "false")));

    [Theory]
    [InlineData("password", GridFilterOperator.Equals, "x")]
    [InlineData("phase", GridFilterOperator.Contains, "Do")]
    [InlineData("amount", GridFilterOperator.StartsWith, "1")]
    [InlineData("amount", GridFilterOperator.Equals, "ten")]
    [InlineData("phase", GridFilterOperator.Equals, "Sleeping")]
    [InlineData("at", GridFilterOperator.GreaterThan, "yesterday")]
    [InlineData("amount", GridFilterOperator.Equals, null)]
    public void AnUnknownKey_AnUnsupportedOperator_OrABadValue_IsABadRequest(string field, GridFilterOperator op, string? value) =>
        Assert.Throws<BadRequestException>(() => Ids(F(field, op, value)));

    [Fact]
    public void TheLimits_AreEnforcedEvenWithoutModelValidation()
    {
        var eleven = Enumerable.Range(0, PaginationRequest.MaxFilters + 1).Select(_ => F("id", GridFilterOperator.IsNotNull)).ToArray();

        Assert.Throws<BadRequestException>(() => Ids(eleven));
        Assert.Throws<BadRequestException>(() => Ids(F("name", GridFilterOperator.Contains, new string('a', GridFilter.MaximumValueLength + 1))));
    }

    [Fact]
    public void Sorts_OrderByTheRequestedKeys_AndNoneLeavesTheCallerDefault()
    {
        var sorted = Map.ApplySorts(Rows.AsQueryable(), [new GridSort { Field = "enabled", Descending = true }, new GridSort { Field = "amount" }]);

        Assert.NotNull(sorted);
        Assert.Equal([3, 1, 2, 4], sorted!.Select(row => row.Id).ToArray());
        Assert.Null(Map.ApplySorts(Rows.AsQueryable(), null));
        Assert.Throws<BadRequestException>(() => Map.ApplySorts(Rows.AsQueryable(), [new GridSort { Field = "secret" }]));
    }

    [Fact]
    public void AKeyDeclaredTwice_IsAProgrammingError() =>
        Assert.Throws<InvalidOperationException>(() => new GridQueryMap<Row>().Number("id", row => row.Id).Number("ID", row => row.Id));
}
