// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Tests.Helpers;

/// <summary>Every operator of the in-memory grid filter, on text, numbers and dates.</summary>
public sealed class GridClientFilterTests
{
    private static GridFilterDescriptor Filter(OmniDataGridFilterOperator op, object? value) => new("Column", value, op);

    [Theory]
    [InlineData(OmniDataGridFilterOperator.Equals, "alpha", true)]
    [InlineData(OmniDataGridFilterOperator.Equals, "ALPHA", true)]
    [InlineData(OmniDataGridFilterOperator.Equals, "beta", false)]
    [InlineData(OmniDataGridFilterOperator.NotEquals, "beta", true)]
    [InlineData(OmniDataGridFilterOperator.NotEquals, "alpha", false)]
    [InlineData(OmniDataGridFilterOperator.GreaterThan, "aaa", true)]
    [InlineData(OmniDataGridFilterOperator.GreaterThan, "alpha", false)]
    [InlineData(OmniDataGridFilterOperator.GreaterThanOrEquals, "alpha", true)]
    [InlineData(OmniDataGridFilterOperator.GreaterThanOrEquals, "zzz", false)]
    [InlineData(OmniDataGridFilterOperator.LessThan, "zzz", true)]
    [InlineData(OmniDataGridFilterOperator.LessThan, "alpha", false)]
    [InlineData(OmniDataGridFilterOperator.LessThanOrEquals, "alpha", true)]
    [InlineData(OmniDataGridFilterOperator.LessThanOrEquals, "aaa", false)]
    [InlineData(OmniDataGridFilterOperator.StartsWith, "AL", true)]
    [InlineData(OmniDataGridFilterOperator.StartsWith, "ph", false)]
    [InlineData(OmniDataGridFilterOperator.EndsWith, "HA", true)]
    [InlineData(OmniDataGridFilterOperator.EndsWith, "al", false)]
    [InlineData(OmniDataGridFilterOperator.Contains, "LPH", true)]
    [InlineData(OmniDataGridFilterOperator.Contains, "xyz", false)]
    [InlineData(OmniDataGridFilterOperator.DoesNotContain, "xyz", true)]
    [InlineData(OmniDataGridFilterOperator.DoesNotContain, "lph", false)]
    public void Text_IsComparedWithoutCase(OmniDataGridFilterOperator op, string expected, bool matches) =>
        Assert.Equal(matches, GridClientFilter.Matches("alpha", Filter(op, expected)));

    [Theory]
    [InlineData(OmniDataGridFilterOperator.Equals, "10", true)]
    [InlineData(OmniDataGridFilterOperator.GreaterThan, "9", true)]
    [InlineData(OmniDataGridFilterOperator.GreaterThan, "10", false)]
    [InlineData(OmniDataGridFilterOperator.LessThan, "100", true)]
    [InlineData(OmniDataGridFilterOperator.LessThanOrEquals, "10", true)]
    [InlineData(OmniDataGridFilterOperator.GreaterThanOrEquals, "11", false)]
    public void Numbers_AreComparedAsNumbersNotText(OmniDataGridFilterOperator op, string expected, bool matches) =>
        Assert.Equal(matches, GridClientFilter.Matches(10, Filter(op, expected)));

    [Fact]
    public void ANumberAgainstAFilterThatIsNotANumber_NeverMatches() =>
        Assert.False(GridClientFilter.Matches(10, Filter(OmniDataGridFilterOperator.Equals, "ten")));

    [Theory]
    [InlineData(OmniDataGridFilterOperator.GreaterThan, "2026-01-01", true)]
    [InlineData(OmniDataGridFilterOperator.LessThan, "2026-01-01", false)]
    [InlineData(OmniDataGridFilterOperator.Equals, "2026-03-05", true)]
    public void Dates_AreComparedAsDates(OmniDataGridFilterOperator op, string expected, bool matches)
    {
        var date = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(matches, GridClientFilter.Matches(date, Filter(op, expected)));
        Assert.False(GridClientFilter.Matches(date, Filter(op, "not a date")));
    }

    [Fact]
    public void DatesWithAnOffset_AreComparedAsInstants()
    {
        var instant = new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.True(GridClientFilter.Matches(instant, Filter(OmniDataGridFilterOperator.GreaterThan, "2026-03-05T11:00:00+00:00")));
        Assert.False(GridClientFilter.Matches(instant, Filter(OmniDataGridFilterOperator.LessThan, "2026-03-05T11:00:00+00:00")));
        Assert.False(GridClientFilter.Matches(instant, Filter(OmniDataGridFilterOperator.LessThan, "not a date")));
    }

    [Fact]
    public void NullAndEmptyOperators_LookAtTheValueNotTheFilterText()
    {
        Assert.True(GridClientFilter.Matches(null, Filter(OmniDataGridFilterOperator.IsNull, null)));
        Assert.False(GridClientFilter.Matches("x", Filter(OmniDataGridFilterOperator.IsNull, null)));
        Assert.True(GridClientFilter.Matches("x", Filter(OmniDataGridFilterOperator.IsNotNull, null)));
        Assert.False(GridClientFilter.Matches(null, Filter(OmniDataGridFilterOperator.IsNotNull, null)));
        Assert.True(GridClientFilter.Matches(string.Empty, Filter(OmniDataGridFilterOperator.IsEmpty, null)));
        Assert.True(GridClientFilter.Matches(null, Filter(OmniDataGridFilterOperator.IsEmpty, null)));
        Assert.False(GridClientFilter.Matches("x", Filter(OmniDataGridFilterOperator.IsEmpty, null)));
        Assert.True(GridClientFilter.Matches("x", Filter(OmniDataGridFilterOperator.IsNotEmpty, null)));
        Assert.False(GridClientFilter.Matches(string.Empty, Filter(OmniDataGridFilterOperator.IsNotEmpty, null)));
    }

    [Fact]
    public void ABlankFilterText_KeepsEveryRow_ButANullValueFailsAComparison()
    {
        Assert.True(GridClientFilter.Matches("x", Filter(OmniDataGridFilterOperator.Contains, string.Empty)));
        Assert.True(GridClientFilter.Matches(null, Filter(OmniDataGridFilterOperator.Contains, null)));
        Assert.False(GridClientFilter.Matches(null, Filter(OmniDataGridFilterOperator.Contains, "x")));
    }

    [Fact]
    public void MultiValuedOperators_MatchAnyOfTheListedValues()
    {
        var values = $"alpha{Aetheus.Shared.Components.Shared.GridFilter.ListSeparator}beta";

        Assert.True(GridClientFilter.Matches("ALPHA", Filter(OmniDataGridFilterOperator.In, values)));
        Assert.False(GridClientFilter.Matches("gamma", Filter(OmniDataGridFilterOperator.In, values)));
        Assert.True(GridClientFilter.Matches("gamma", Filter(OmniDataGridFilterOperator.NotIn, values)));
        Assert.False(GridClientFilter.Matches("beta", Filter(OmniDataGridFilterOperator.NotIn, values)));
    }

    [Fact]
    public void ASecondCondition_NarrowsTheFirst()
    {
        var range = new GridFilterDescriptor(
            "Column", "5", OmniDataGridFilterOperator.GreaterThanOrEquals,
            OmniDataGridFilterOperator.LessThanOrEquals, "9");

        Assert.True(GridClientFilter.Matches(7, range));
        Assert.False(GridClientFilter.Matches(4, range));
        Assert.False(GridClientFilter.Matches(10, range));
    }

    [Fact]
    public void AnUnknownOperator_IsRefusedRatherThanIgnored()
    {
        var unknown = (OmniDataGridFilterOperator)int.MaxValue;

        Assert.Throws<ArgumentOutOfRangeException>(() => GridClientFilter.Matches("x", Filter(unknown, "x")));
    }

    [Fact]
    public void Numbers_UseTheInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            Assert.True(GridClientFilter.Matches(1.5m, Filter(OmniDataGridFilterOperator.Equals, "1.5")));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
