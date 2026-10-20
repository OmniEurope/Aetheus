// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// <see cref="JsLocaleCompare"/> and <see cref="JsText"/> against node v24.7.0
/// (<c>TestData/js-locale-compare.json</c>, <see cref="ScriptReferenceData"/>; the literal expectations
/// below were printed by node for the same input).
/// </summary>
public sealed class JsLocaleCompareTests
{
    private const string Table = "js-locale-compare.json";

    [Fact]
    public void EveryRecordedPair_ComparesAsNodeDid()
    {
        var pairs = ScriptReferenceData.Root(Table).GetProperty("pairs").EnumerateArray()
            .Select(pair => (Left: pair[0].GetString()!, Right: pair[1].GetString()!, Expected: pair[2].GetInt32()))
            .ToList();

        var mismatches = pairs.Where(pair => JsLocaleCompare.Compare(pair.Left, pair.Right) != pair.Expected).ToList();

        Assert.Equal(3681, pairs.Count);
        Assert.Empty(mismatches);
        // The table is meaningful: hundreds of its pairs order differently from an ordinal comparison.
        Assert.True(pairs.Count(pair => Math.Sign(string.CompareOrdinal(pair.Left, pair.Right)) != pair.Expected) > 500);
    }

    [Fact]
    public void EveryPairOfAsciiCharacters_ComparesAsTheirNodeRanks()
    {
        var root = ScriptReferenceData.Root(Table);
        var singles = root.GetProperty("singles").GetString()!;
        var ranks = root.GetProperty("ranks").EnumerateArray().Select(rank => rank.GetInt32()).ToArray();
        Assert.Equal(128, singles.Length);

        for (var i = 0; i < singles.Length; i++)
        {
            for (var j = 0; j < singles.Length; j++)
                Assert.Equal(Math.Sign(ranks[i] - ranks[j]), JsLocaleCompare.Compare(singles[i].ToString(), singles[j].ToString()));
        }
    }

    [Theory]
    [InlineData("é")]
    [InlineData("a ")]
    [InlineData("😀")]
    public void TextOutsideAscii_IsRefusedRatherThanGuessed(string text)
    {
        var error = Assert.Throws<NotSupportedException>(() => JsLocaleCompare.Compare("a", text));

        Assert.StartsWith("localeCompare is only reproduced for ASCII text", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sort_IsStableAndChecksEveryKeyBeforeSorting()
    {
        var sorted = JsLocaleCompare.Sort(["b", "a\u0001", "A", "a", "\u0001a"], key => key);

        // "a\u0001", "a" and "\u0001a" compare as 0: they keep their order, as Array.prototype.sort keeps it.
        Assert.Equal(["a\u0001", "a", "\u0001a", "A", "b"], sorted);
        Assert.Equal(["é"], JsLocaleCompare.Sort(["é"], key => key));
        Assert.Throws<NotSupportedException>(() => JsLocaleCompare.Sort(["a", "b", "é"], key => key));
    }

    [Theory]
    [InlineData("@aetheus/web-analytics", "%40aetheus%2Fweb-analytics")]
    [InlineData("AZaz09-_.!~*'()", "AZaz09-_.!~*'()")]
    [InlineData("[1.0.0, )+ é😀\u007f\u0000", "%5B1.0.0%2C%20)%2B%20%C3%A9%F0%9F%98%80%7F%00")]
    public void EncodeUriComponent_EncodesAsJavaScript(string text, string expected) =>
        Assert.Equal(expected, JsText.EncodeUriComponent(text));

    // A lone surrogate does not survive the serialization of theory data, so the cases are built here.
    [Fact]
    public void EncodeUriComponent_RefusesALoneSurrogate()
    {
        foreach (var text in (string[])["\ud800", "a\udc00", "\ud800\ud800", "\udc00\ud800"])
            Assert.Equal("URI malformed", Assert.Throws<JsError>(() => JsText.EncodeUriComponent(text)).Message);
    }

    [Theory]
    [InlineData(" \t\n1784793600xyz", 1784793600d)]
    [InlineData("+5", 5d)]
    [InlineData("1.9", 1d)]
    [InlineData("1e3", 1d)]
    [InlineData("-12", -12d)]
    public void ParseInt_ReadsTheLeadingDecimalDigits(string text, double expected) =>
        Assert.Equal(expected, JsText.ParseInt(text));

    [Fact]
    public void ParseInt_GivesNaNWithoutDigitsAndMinusZero()
    {
        Assert.True(double.IsNaN(JsText.ParseInt("")));
        Assert.True(double.IsNaN(JsText.ParseInt("-x1")));
        Assert.True(double.IsNegative(JsText.ParseInt("-0")));
        Assert.True(double.IsPositiveInfinity(JsText.ParseInt(new string('9', 400))));
    }

    [Theory]
    [InlineData(1784793600000d, "2026-07-23T08:00:00.000Z")]
    [InlineData(-1000d, "1969-12-31T23:59:59.000Z")]
    [InlineData(-62198755200000d, "-000001-01-01T00:00:00.000Z")]
    [InlineData(-62167219200000d, "0000-01-01T00:00:00.000Z")]
    [InlineData(253402300800000d, "+010000-01-01T00:00:00.000Z")]
    [InlineData(8.64e15, "+275760-09-13T00:00:00.000Z")]
    [InlineData(-8.64e15, "-271821-04-20T00:00:00.000Z")]
    [InlineData(1.9, "1970-01-01T00:00:00.001Z")]
    public void ToIsoString_WritesTheProlepticGregorianUtcDate(double time, string expected) =>
        Assert.Equal(expected, JsText.ToIsoString(time));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(8.64e15 + 1)]
    public void ToIsoString_RefusesAnInvalidTime(double time) =>
        Assert.Equal("Invalid time value", Assert.Throws<JsError>(() => JsText.ToIsoString(time)).Message);
}
