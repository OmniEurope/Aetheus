// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// <see cref="JsJson"/> and <see cref="JsValue"/> against what JavaScript (V8, node v24.7.0) answers.
/// The expected strings below were printed by node for the same input; the number table
/// <c>TestData/js-number-format.json</c> holds 3000 doubles (edge values, random bit patterns, random
/// decimal literals, random dyadic fractions) as their IEEE bits with <c>String(value)</c> as node
/// wrote it.
/// </summary>
public sealed class JsJsonTests
{
    private static string RoundTrip(string json) => JsJson.Stringify(JsJson.Parse(json))!;

    [Theory]
    [InlineData("""{"b":1,"a":2,"b":3}""", """{"b":3,"a":2}""")]
    [InlineData("""{"b":1,"4294967295":2,"4294967294":3,"1":4,"01":5,"0":6,"-0":7}""",
        """{"0":6,"1":4,"4294967294":3,"b":1,"4294967295":2,"01":5,"-0":7}""")]
    [InlineData("""{"a":1,"2":2,"a":3,"1":4,"2":5}""", """{"1":4,"2":5,"a":3}""")]
    [InlineData("""{"__proto__":{"x":1},"a":1}""", """{"__proto__":{"x":1},"a":1}""")]
    [InlineData(" \t\r\n[ 1 , {} , [] , null , true , false ] \n", "[1,{},[],null,true,false]")]
    public void Parse_KeepsJavaScriptPropertyOrder(string json, string expected) =>
        Assert.Equal(expected, RoundTrip(json));

    [Fact]
    public void Stringify_EscapesExactlyAsJavaScript()
    {
        var value = new JsObject();
        value.Set("a", "\u2028\u0007\ud800x\udc00\ud83d\ude00/<\u007f\u0085\"\\\b\f\n\r\t\u0000\u001f\u00e9");

        Assert.Equal(
            "{\"a\":\"\u2028\\u0007\\ud800x\\udc00\ud83d\ude00/<\u007f\u0085\\\"\\\\\\b\\f\\n\\r\\t\\u0000\\u001f\u00e9\"}",
            JsJson.Stringify(value));
    }

    [Fact]
    public void Parse_DecodesEscapesIncludingLoneSurrogates()
    {
        var text = (string)JsJson.Parse("\"\\u0041\\/\\ud800\\uDC00\\u00E9\\b\"")!;

        Assert.Equal("A/\ud800\udc00\u00e9\b", text);
    }

    [Fact]
    public void StringifyIndented_UsesTwoSpacesAndCompactEmptyContainers()
    {
        var value = JsJson.Parse("""{"b":[],"c":{},"d":[null,{"e":1}],"f":"x"}""");
        ((JsObject)value!).Set("u", JsUndefined.Value);
        ((List<object?>)((JsObject)value)["d"]!).Add(JsUndefined.Value);

        Assert.Equal(
            "{\n  \"b\": [],\n  \"c\": {},\n  \"d\": [\n    null,\n    {\n      \"e\": 1\n    },\n    null\n  ],\n  \"f\": \"x\"\n}",
            JsJson.StringifyIndented(value));
    }

    [Fact]
    public void Stringify_OmitsUndefinedPropertiesAndReturnsNullForUndefined()
    {
        var value = new JsObject();
        value.Set("a", JsUndefined.Value);

        Assert.Equal("{}", JsJson.Stringify(value));
        Assert.Null(JsJson.Stringify(JsUndefined.Value));
    }

    [Theory]
    [InlineData("[1e21,1.5e-7,-0,0.000001,123456789012345680000,1e-7,1e400,-1e400,5e-324,1.7976931348623157e308,0.30000000000000004]",
        "[1e+21,1.5e-7,0,0.000001,123456789012345680000,1e-7,null,null,5e-324,1.7976931348623157e+308,0.30000000000000004]")]
    [InlineData("[2.0e0,1.1E1,-0.0,100e-2,12345678901234567890123]", "[2,11,0,1,1.2345678901234568e+22]")]
    [InlineData("[1e99999999999999999999,-1e-99999999999999999999,0.0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001e400]",
        "[null,0,1e+90]")]
    public void Stringify_WritesNumbersAsJavaScript(string json, string expected) =>
        Assert.Equal(expected, RoundTrip(json));

    [Fact]
    public void FormatNumber_MatchesNodeOnTheReferenceTable()
    {
        var table = JsonSerializer.Deserialize<string[][]>(
            File.ReadAllText(Path.Combine(RepositoryScan.Root, "tests", "Aetheus.Agent.Core.Tests", "TestData", "js-number-format.json")))!;

        Assert.Equal(3000, table.Length);
        var mismatches = table
            .Select(entry => (Bits: entry[0], Expected: entry[1],
                Value: BitConverter.Int64BitsToDouble(long.Parse(entry[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture))))
            .Where(entry => JsValue.FormatNumber(entry.Value) != entry.Expected
                // The text node wrote reads back as the same double through JSON.parse.
                || BitConverter.DoubleToInt64Bits((double)JsJson.Parse(entry.Expected)!) != BitConverter.DoubleToInt64Bits(Math.Abs(entry.Value) == 0 ? 0 : entry.Value))
            .Select(entry => $"{entry.Bits}: {JsValue.FormatNumber(entry.Value)} != {entry.Expected}")
            .ToList();
        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{,}")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1e")]
    [InlineData("-")]
    [InlineData("NaN")]
    [InlineData("tru")]
    [InlineData("'a'")]
    [InlineData("\"\\x41\"")]
    [InlineData("\"\\u12G4\"")]
    [InlineData("\"tab\there\"")]
    [InlineData("{\"a\" 1}")]
    [InlineData("[1] [2]")]
    [InlineData("\ufeff{}")]
    [InlineData("\u00a0{}")]
    [InlineData("{}\u2028")]
    public void Parse_RefusesWhatJavaScriptRefuses(string json) =>
        Assert.Throws<FormatException>(() => JsJson.Parse(json));

    [Fact]
    public void ToJsString_ConvertsLikeString()
    {
        var array = (List<object?>)JsJson.Parse("""[1,[2,null],{},null,"a",true,-0,1e21]""")!;
        array.Add(JsUndefined.Value);

        Assert.Equal("1,2,,[object Object],,a,true,0,1e+21,", JsValue.ToJsString(array));
        Assert.Equal("undefined", JsValue.ToJsString(JsUndefined.Value));
        Assert.Equal("null", JsValue.ToJsString(null));
        Assert.Equal("[object Object]", JsValue.ToJsString(new JsObject()));
    }

    [Fact]
    public void StrictEquals_ComparesPrimitivesByValueAndObjectsByIdentity()
    {
        var obj = new JsObject();

        Assert.True(JsValue.StrictEquals(0d, -0d));
        Assert.False(JsValue.StrictEquals(double.NaN, double.NaN));
        Assert.False(JsValue.StrictEquals(null, JsUndefined.Value));
        Assert.False(JsValue.StrictEquals("1", 1d));
        Assert.True(JsValue.StrictEquals(obj, obj));
        Assert.False(JsValue.StrictEquals(obj, new JsObject()));
        Assert.False(JsValue.StrictEquals(new List<object?> { "A" }, "A"));
    }

    [Fact]
    public void Truthy_FollowsJavaScript()
    {
        Assert.All(new object?[] { null, JsUndefined.Value, false, 0d, -0d, double.NaN, "" }, value => Assert.False(JsValue.Truthy(value)));
        Assert.All(new object?[] { true, 1d, "0", new JsObject(), new List<object?>() }, value => Assert.True(JsValue.Truthy(value)));
    }

    [Fact]
    public void Entries_FollowObjectEntries()
    {
        Assert.Equal(["0", "1"], JsValue.Keys("ab"));
        Assert.Equal(["0", "1"], JsValue.Keys(new List<object?> { 1d, 2d }));
        Assert.Empty(JsValue.Keys(5d));
        Assert.Empty(JsValue.Keys(true));
        Assert.Equal(["2", "10", "b", "a"], JsValue.Keys(JsJson.Parse("""{"b":1,"10":2,"a":3,"2":4}""")));
        Assert.Throws<JsTypeError>(() => JsValue.Entries(null));
    }

    [Fact]
    public void Get_ThrowsTheTypeErrorOfJavaScriptOnNullAndUndefined()
    {
        Assert.Equal("Cannot read properties of null (reading 'status')",
            Assert.Throws<JsTypeError>(() => JsValue.Get(null, "status")).Message);
        Assert.Equal("Cannot read properties of undefined (reading 'x')",
            Assert.Throws<JsTypeError>(() => JsValue.Get(JsUndefined.Value, "x")).Message);
        Assert.Same(JsUndefined.Value, JsValue.Get("text", "status"));
        Assert.Same(JsUndefined.Value, JsValue.GetOptional(null, "status"));
    }

    [Fact]
    public void Trim_RemovesJavaScriptWhiteSpaceOnly()
    {
        Assert.Equal("a", JsValue.Trim("\ufeff\u00a0\u3000\u2028\t a \r\n\v\f\u2029\u202f"));
        Assert.Equal("\u0085a\u0085", JsValue.Trim(" \u0085a\u0085 "));
    }

    [Fact]
    public void IsSafeInteger_FollowsNumberIsSafeInteger()
    {
        Assert.True(JsValue.IsSafeInteger(9007199254740991d));
        Assert.True(JsValue.IsSafeInteger(-0d));
        Assert.False(JsValue.IsSafeInteger(9007199254740992d));
        Assert.False(JsValue.IsSafeInteger(1.5));
        Assert.False(JsValue.IsSafeInteger("1"));
        Assert.False(JsValue.IsSafeInteger(double.PositiveInfinity));
    }

    [Fact]
    public void DeepNesting_IsRefusedInsteadOfOverflowingTheStack()
    {
        var json = new string('[', 1_000_000) + new string(']', 1_000_000);

        Assert.Throws<InsufficientExecutionStackException>(() => JsJson.Parse(json));
    }

    [Fact]
    public void Clone_KeepsOrderAndRemoveForgetsTheKey()
    {
        var obj = (JsObject)JsJson.Parse("""{"seal":1,"b":2,"3":3}""")!;
        var copy = obj.Clone();
        copy.Remove("seal");
        copy.Set("seal", 4d);

        Assert.Equal("""{"3":3,"b":2,"seal":4}""", JsJson.Stringify(copy));
        Assert.Equal("""{"3":3,"seal":1,"b":2}""", JsJson.Stringify(obj));
    }
}
