// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The JavaScript operations the ported Node programs apply to <see cref="JsJson"/> values, with
/// JavaScript's results: <c>String(value)</c>, truthiness, <c>===</c>, property reads (a read on
/// <c>null</c> or <c>undefined</c> throws <see cref="JsTypeError"/> unless optional), <c>??</c>,
/// <c>Object.entries</c> / <c>Object.keys</c>, <c>Number.isSafeInteger</c>, <c>String.prototype.trim</c>
/// and <c>Number.prototype.toString</c>.
///
/// A property read only sees own properties: the names the ported programs read are none of the
/// properties <c>Object.prototype</c>, <c>Array.prototype</c> or the primitive prototypes define, so the
/// prototype chain could not change their result.
/// </summary>
internal static partial class JsValue
{
    private const double MaxSafeInteger = 9007199254740991d;

    /// <summary><c>String(value)</c>, also what a template literal writes. An object converts through
    /// its <c>toString</c>: an own <c>toString</c> property, which JSON can only make a non-callable
    /// value, hides <c>Object.prototype.toString</c> and the conversion fails with V8's
    /// <see cref="JsTypeError"/>, as it does for an array holding such an object.</summary>
    public static string ToJsString(object? value) => value switch
    {
        null => "null",
        JsUndefined => "undefined",
        bool flag => flag ? "true" : "false",
        double number => FormatNumber(number),
        string text => text,
        // Array.prototype.toString is join(","), where null and undefined elements are empty.
        List<object?> array => string.Join(',', array.Select(element => IsNullish(element) ? string.Empty : ToJsString(element))),
        JsObject obj when obj.ContainsKey("toString") => throw new JsTypeError("Cannot convert object to primitive value"),
        JsObject => "[object Object]",
        _ => throw new ArgumentException($"{value.GetType().Name} is not a JavaScript value.", nameof(value))
    };

    /// <summary><c>Number(value)</c>, as a relational comparison converts its operands: an object or an
    /// array through <see cref="ToJsString"/> (neither has a callable <c>valueOf</c> of its own), a string
    /// by <c>StringToNumber</c>. A hexadecimal, octal or binary literal beyond 2^53 is approximated by
    /// <see cref="BigInteger"/> conversion; the ported comparisons only test such values against counts.</summary>
    public static double ToNumber(object? value) => value switch
    {
        null => 0,
        JsUndefined => double.NaN,
        bool flag => flag ? 1 : 0,
        double number => number,
        string text => StringToNumber(text),
        _ => StringToNumber(ToJsString(value))
    };

    private static double StringToNumber(string text)
    {
        var literal = Trim(text);
        if (literal.Length == 0)
            return 0;
        if (literal.Length > 2 && literal[0] == '0' && RadixOf(literal[1]) is var radix and > 0)
            return ParseRadix(literal[2..], radix);
        return literal switch
        {
            "Infinity" or "+Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            _ when DecimalLiteral().IsMatch(literal) => double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => double.NaN
        };
    }

    private static int RadixOf(char prefix) => prefix switch
    {
        'x' or 'X' => 16,
        'o' or 'O' => 8,
        'b' or 'B' => 2,
        _ => 0
    };

    private static double ParseRadix(string digits, int radix)
    {
        var value = BigInteger.Zero;
        foreach (var character in digits)
        {
            var digit = char.IsAsciiDigit(character) ? character - '0'
                : char.IsAsciiLetter(character) ? (character | 0x20) - 'a' + 10
                : radix;
            if (digit >= radix)
                return double.NaN;
            value = value * radix + digit;
        }
        return (double)value;
    }

    [GeneratedRegex(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?\z")]
    private static partial Regex DecimalLiteral();

    /// <summary><c>left &gt;= right</c> for a number on the right: false when either side is NaN.</summary>
    public static bool GreaterOrEqual(object? left, double right) => ToNumber(left) >= right;

    /// <summary>Whether <c>if (value)</c> takes the branch.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null or JsUndefined => false,
        bool flag => flag,
        double number => number != 0 && !double.IsNaN(number),
        string text => text.Length > 0,
        _ => true
    };

    public static bool IsNullish(object? value) => value is null or JsUndefined;

    /// <summary><c>a === b</c>: primitives by value (NaN equals nothing, 0 equals -0), objects and
    /// arrays by identity.</summary>
    public static bool StrictEquals(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (JsUndefined, JsUndefined) => true,
        (bool a, bool b) => a == b,
        (double a, double b) => a == b,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (JsObject or List<object?>, _) => ReferenceEquals(left, right),
        _ => false
    };

    /// <summary><c>value ?? fallback</c>.</summary>
    public static object? Coalesce(object? value, object? fallback) => IsNullish(value) ? fallback : value;

    /// <summary><c>target.key</c>: throws like JavaScript on null or undefined.</summary>
    public static object? Get(object? target, string key) => IsNullish(target)
        ? throw new JsTypeError($"Cannot read properties of {ToJsString(target)} (reading '{key}')")
        : GetOptional(target, key);

    /// <summary><c>target?.key</c>. The only property of a primitive or an array the ported programs
    /// read is <c>length</c>.</summary>
    public static object? GetOptional(object? target, string key) => (target, key) switch
    {
        (JsObject obj, _) => obj[key],
        (string text, "length") => (double)text.Length,
        (List<object?> array, "length") => (double)array.Count,
        _ => JsUndefined.Value
    };

    /// <summary><c>Object.entries(value)</c>: own properties of an object, indices of an array or of a
    /// string, nothing for another primitive.</summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> Entries(object? value) => value switch
    {
        null or JsUndefined => throw new JsTypeError("Cannot convert undefined or null to object"),
        JsObject obj => obj.Properties.ToList(),
        List<object?> array => array.Select((element, index) => Entry(index, element)).ToList(),
        string text => text.Select((character, index) => Entry(index, character.ToString())).ToList(),
        _ => []
    };

    /// <summary><c>Object.keys(value)</c>.</summary>
    public static IReadOnlyList<string> Keys(object? value) => Entries(value).Select(entry => entry.Key).ToList();

    /// <summary><c>Number.isSafeInteger(value)</c>.</summary>
    public static bool IsSafeInteger(object? value) =>
        value is double number && double.IsFinite(number) && Math.Floor(number) == number && Math.Abs(number) <= MaxSafeInteger;

    /// <summary><c>Number.isSafeInteger(value) &amp;&amp; value &gt; 0</c>.</summary>
    public static bool IsPositiveSafeInteger(object? value) => IsSafeInteger(value) && (double)value! > 0;

    /// <summary><c>String.prototype.trim</c>: removes JavaScript white space and line terminators,
    /// which include U+FEFF but not U+0085, unlike <see cref="string.Trim()"/>.</summary>
    public static string Trim(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        var end = text.Length;
        while (start < end && IsWhiteSpace(text[start]))
            start++;
        while (end > start && IsWhiteSpace(text[end - 1]))
            end--;
        return text[start..end];
    }

    private static bool IsWhiteSpace(char character) =>
        character is '\t' or '\n' or '\v' or '\f' or '\r'
        || character == LineSeparator || character == ParagraphSeparator || character == ByteOrderMark
        || char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;

    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;
    private const char ByteOrderMark = (char)0xFEFF;

    /// <summary>
    /// <c>value.toLowerCase() === lowerHex</c> for a lowercase hexadecimal <paramref name="lowerHex"/>, as
    /// <c>value?.toLowerCase() !== lowerHex</c> reads it: null and undefined differ, a value that is not a
    /// string throws (<c>toLowerCase is not a function</c>). Lowering ASCII letters alone gives
    /// JavaScript's answer: no other character lowercases to a hexadecimal digit (U+0130 becomes
    /// <c>i</c> plus a combining dot, U+212A becomes <c>k</c>).
    /// </summary>
    public static bool LowerCaseEquals(object? value, string lowerHex, string expression) => value switch
    {
        null or JsUndefined => false,
        string text => string.Equals(AsciiLower(text), lowerHex, StringComparison.Ordinal),
        _ => throw new JsTypeError($"{expression}.toLowerCase is not a function")
    };

    /// <summary>The text with its ASCII capitals lowered, every other character kept.</summary>
    public static string AsciiLower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var index = 0; index < source.Length; index++)
            span[index] = char.IsAsciiLetterUpper(source[index]) ? (char)(source[index] | 0x20) : source[index];
    });

    /// <summary><c>Number.prototype.toString()</c>: the shortest digits that read back as the same
    /// double, positional from 1e-7 (exclusive) to 1e21 (exclusive), exponent form <c>1e+21</c> /
    /// <c>1.5e-7</c> outside, <c>-0</c> as <c>0</c>.</summary>
    public static string FormatNumber(double number)
    {
        if (double.IsNaN(number))
            return "NaN";
        if (double.IsInfinity(number))
            return number > 0 ? "Infinity" : "-Infinity";
        if (number == 0)
            return "0";
        if (number < 0)
            return "-" + FormatNumber(-number);
        var (digits, point) = ShortestDigits(number);
        return Layout(digits, point);
    }

    /// <summary>The shortest round-trip digits of a positive finite double, without leading or trailing
    /// zeros, and the position n of the decimal point: the value is 0.d1d2...dk x 10^n.</summary>
    private static (string Digits, int Point) ShortestDigits(double number)
    {
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOf('E', StringComparison.Ordinal);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(text.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var point = (dot < 0 ? mantissa.Length : dot) + exponent;
        var significant = digits.TrimStart('0');
        point -= digits.Length - significant.Length;
        return (significant.TrimEnd('0'), point);
    }

    private static string Layout(string digits, int point)
    {
        var count = digits.Length;
        if (count <= point && point <= 21)
            return digits + new string('0', point - count);
        if (0 < point && point <= 21)
            return digits[..point] + "." + digits[point..];
        if (-6 < point && point <= 0)
            return "0." + new string('0', -point) + digits;
        var exponent = point - 1;
        var mantissa = count == 1 ? digits : digits[..1] + "." + digits[1..];
        return mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
    }

    private static KeyValuePair<string, object?> Entry(int index, object? value) =>
        new(index.ToString(CultureInfo.InvariantCulture), value);
}

/// <summary>A JavaScript <c>TypeError</c>: where the Node program would have crashed, the port fails
/// with V8's message for that error.</summary>
internal sealed class JsTypeError(string message) : Exception(message);

/// <summary>An <c>Error</c> (or <c>RangeError</c>, <c>URIError</c>) a ported Node program throws: the
/// step fails with its message, verbatim.</summary>
internal sealed class JsError(string message) : Exception(message);
