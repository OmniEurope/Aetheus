// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// <c>JSON.parse</c> and <c>JSON.stringify</c> exactly as JavaScript (V8) performs them, for the ported
/// Node programs whose output is hashed or compared byte for byte (a seal is
/// <c>sha256(JSON.stringify(value))</c>, recomputed later over the parsed value).
///
/// Values are modelled as JavaScript sees them: <c>null</c>, <see cref="JsUndefined.Value"/>,
/// <see cref="bool"/>, <see cref="double"/> (every JSON number), <see cref="string"/>,
/// <see cref="JsObject"/> and <see cref="List{T}"/> of values for an array. Parsing keeps the property
/// order JavaScript keeps (array-index keys first, ascending, then the other keys in insertion order; a
/// duplicate key keeps the position of its first occurrence and the value of its last). Writing escapes
/// strings like <c>QuoteJSONString</c>, formats numbers like <c>Number.prototype.toString</c>, writes
/// non-finite numbers as <c>null</c> and omits object properties whose value is undefined.
///
/// One difference, deliberate: nesting deeper than the .NET stack allows is refused with
/// <see cref="InsufficientExecutionStackException"/> instead of crashing the process; V8 parses any
/// depth and fails only when stringifying beyond its own stack.
/// </summary>
internal static class JsJson
{
    /// <summary><c>JSON.parse(text)</c>. Throws <see cref="FormatException"/> on text JavaScript refuses.</summary>
    public static object? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new JsJsonParser(text).ParseDocument();
    }

    /// <summary><c>JSON.stringify(value)</c>: compact. Null when JavaScript returns undefined.</summary>
    public static string? Stringify(object? value) => Write(value, indent: null);

    /// <summary><c>JSON.stringify(value, null, 2)</c>, without the trailing newline the callers add.</summary>
    public static string? StringifyIndented(object? value) => Write(value, indent: "  ");

    private static string? Write(object? value, string? indent)
    {
        if (value is JsUndefined)
            return null;
        var builder = new StringBuilder();
        WriteValue(builder, value, indent, string.Empty);
        return builder.ToString();
    }

    private static void WriteValue(StringBuilder builder, object? value, string? indent, string current)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case double number:
                builder.Append(double.IsFinite(number) ? JsValue.FormatNumber(number) : "null");
                break;
            case string text:
                Quote(builder, text);
                break;
            case JsObject obj:
                WriteObject(builder, obj, indent, current);
                break;
            case List<object?> array:
                WriteArray(builder, array, indent, current);
                break;
            default:
                throw new ArgumentException($"{value.GetType().Name} is not a JavaScript value.", nameof(value));
        }
    }

    private static void WriteObject(StringBuilder builder, JsObject obj, string? indent, string current)
    {
        var members = obj.Properties.Where(property => property.Value is not JsUndefined).ToList();
        if (members.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        var inner = current + indent;
        builder.Append('{');
        for (var index = 0; index < members.Count; index++)
        {
            Separate(builder, index, indent, inner);
            Quote(builder, members[index].Key);
            builder.Append(indent is null ? ":" : ": ");
            WriteValue(builder, members[index].Value, indent, inner);
        }
        Close(builder, indent, current, '}');
    }

    private static void WriteArray(StringBuilder builder, List<object?> array, string? indent, string current)
    {
        if (array.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        var inner = current + indent;
        builder.Append('[');
        for (var index = 0; index < array.Count; index++)
        {
            Separate(builder, index, indent, inner);
            // An undefined element is written as null, unlike an undefined property, which is omitted.
            WriteValue(builder, array[index] is JsUndefined ? null : array[index], indent, inner);
        }
        Close(builder, indent, current, ']');
    }

    private static void Separate(StringBuilder builder, int index, string? indent, string inner)
    {
        if (index > 0)
            builder.Append(',');
        if (indent is not null)
            builder.Append('\n').Append(inner);
    }

    private static void Close(StringBuilder builder, string? indent, string current, char bracket)
    {
        if (indent is not null)
            builder.Append('\n').Append(current);
        builder.Append(bracket);
    }

    /// <summary><c>QuoteJSONString</c>: the six short escapes, other control characters and lone
    /// surrogates as lowercase <c>\uXXXX</c>, everything else as is.</summary>
    private static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[++index]);
                continue;
            }
            builder.Append(Escape(character) ?? character.ToString());
        }
        builder.Append('"');
    }

    private static string? Escape(char character) => character switch
    {
        '"' => "\\\"",
        '\\' => "\\\\",
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ when character < ' ' || char.IsSurrogate(character) =>
            "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
        _ => null
    };
}

/// <summary>JavaScript's <c>undefined</c>, distinct from <c>null</c>: a property holding it is omitted
/// by <c>JSON.stringify</c>, and a missing property reads as it.</summary>
internal sealed class JsUndefined
{
    public static readonly JsUndefined Value = new();

    private JsUndefined()
    {
    }

    public override string ToString() => "undefined";
}

/// <summary>
/// An ordinary JavaScript object: its own properties in the order JavaScript enumerates them, the
/// array-index keys (canonical decimal integers below 2^32 - 1) first in ascending order, then the other
/// keys in the order they were first set. Setting an existing key keeps its position.
/// </summary>
internal sealed class JsObject
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly SortedSet<uint> _indices = [];
    private readonly List<string> _names = [];

    /// <summary>The own keys in JavaScript order.</summary>
    public IEnumerable<string> Keys =>
        _indices.Select(index => index.ToString(CultureInfo.InvariantCulture)).Concat(_names);

    /// <summary>The own properties in JavaScript order.</summary>
    public IEnumerable<KeyValuePair<string, object?>> Properties =>
        Keys.Select(key => new KeyValuePair<string, object?>(key, _values[key]));

    /// <summary>The value of an own property, or undefined.</summary>
    public object? this[string key]
    {
        get => _values.TryGetValue(key, out var value) ? value : JsUndefined.Value;
        set => Set(key, value);
    }

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <summary><c>obj[key] = value</c> on a data property.</summary>
    public void Set(string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_values.ContainsKey(key))
        {
            if (TryArrayIndex(key, out var index))
                _indices.Add(index);
            else
                _names.Add(key);
        }
        _values[key] = value;
    }

    /// <summary><c>delete obj[key]</c>.</summary>
    public bool Remove(string key)
    {
        if (!_values.Remove(key))
            return false;
        if (TryArrayIndex(key, out var index))
            _indices.Remove(index);
        else
            _names.Remove(key);
        return true;
    }

    /// <summary><c>{ ...obj }</c>: a new object with the same own properties in the same order.</summary>
    public JsObject Clone()
    {
        var copy = new JsObject();
        foreach (var (key, value) in Properties)
            copy.Set(key, value);
        return copy;
    }

    /// <summary>An array index: <c>0</c> or a decimal integer without leading zero, below 2^32 - 1.</summary>
    internal static bool TryArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length is 0 or > 10 || (key[0] == '0' && key.Length > 1) || !key.All(char.IsAsciiDigit))
            return false;
        var value = ulong.Parse(key, NumberStyles.None, CultureInfo.InvariantCulture);
        if (value >= uint.MaxValue)
            return false;
        index = (uint)value;
        return true;
    }
}
