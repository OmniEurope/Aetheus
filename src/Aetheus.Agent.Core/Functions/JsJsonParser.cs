// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The JSON grammar <c>JSON.parse</c> accepts (ECMA-404): whitespace is only space, tab, line feed
/// and carriage return; strings refuse raw control characters and accept lone surrogates written as
/// <c>\u</c> escapes; numbers have no leading zero, no leading <c>+</c> and no bare dot, and are read as
/// the nearest double (an exponent beyond the double range reads as an infinity, as in JavaScript).
/// Objects are built as <see cref="JsObject"/>, arrays as lists.
/// </summary>
internal sealed class JsJsonParser(string text)
{
    private int _position;

    public object? ParseDocument()
    {
        SkipWhitespace();
        var value = ParseValue();
        SkipWhitespace();
        if (_position != text.Length)
            throw Unexpected();
        return value;
    }

    private object? ParseValue()
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return Peek() switch
        {
            '{' => ParseObject(),
            '[' => ParseArray(),
            '"' => ParseString(),
            't' => ParseLiteral("true", true),
            'f' => ParseLiteral("false", false),
            'n' => ParseLiteral("null", null),
            _ => ParseNumber()
        };
    }

    private JsObject ParseObject()
    {
        _position++;
        var obj = new JsObject();
        SkipWhitespace();
        if (TryConsume('}'))
            return obj;
        do
        {
            SkipWhitespace();
            if (Peek() != '"')
                throw Unexpected();
            var key = ParseString();
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();
            // A duplicate key keeps its first position and takes the last value.
            obj.Set(key, ParseValue());
            SkipWhitespace();
        }
        while (TryConsume(','));
        Expect('}');
        return obj;
    }

    private List<object?> ParseArray()
    {
        _position++;
        var array = new List<object?>();
        SkipWhitespace();
        if (TryConsume(']'))
            return array;
        do
        {
            SkipWhitespace();
            array.Add(ParseValue());
            SkipWhitespace();
        }
        while (TryConsume(','));
        Expect(']');
        return array;
    }

    private string ParseString()
    {
        _position++;
        var builder = new StringBuilder();
        while (true)
        {
            if (_position >= text.Length)
                throw Unexpected();
            var character = text[_position++];
            if (character == '"')
                return builder.ToString();
            if (character < ' ')
                throw Unexpected(_position - 1);
            if (character == '\\')
                builder.Append(ParseEscape());
            else
                builder.Append(character);
        }
    }

    private char ParseEscape()
    {
        if (_position >= text.Length)
            throw Unexpected();
        var escape = text[_position++];
        return escape switch
        {
            '"' => '"',
            '\\' => '\\',
            '/' => '/',
            'b' => '\b',
            'f' => '\f',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'u' => ParseUnicodeEscape(),
            _ => throw Unexpected(_position - 1)
        };
    }

    private char ParseUnicodeEscape()
    {
        if (_position + 4 > text.Length || text.AsSpan(_position, 4).ContainsAnyExcept(HexDigits))
            throw Unexpected();
        var code = ushort.Parse(text.AsSpan(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        _position += 4;
        return (char)code;
    }

    private static readonly System.Buffers.SearchValues<char> HexDigits =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    private double ParseNumber()
    {
        var start = _position;
        TryConsume('-');
        if (!TryConsume('0'))
            RequireDigits();
        if (TryConsume('.'))
            RequireDigits();
        if (TryConsume('e') || TryConsume('E'))
        {
            if (!TryConsume('+'))
                TryConsume('-');
            RequireDigits();
        }
        return double.Parse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private void RequireDigits()
    {
        var start = _position;
        while (_position < text.Length && char.IsAsciiDigit(text[_position]))
            _position++;
        if (_position == start)
            throw Unexpected();
    }

    private object? ParseLiteral(string literal, object? value)
    {
        if (string.CompareOrdinal(text, _position, literal, 0, literal.Length) != 0)
            throw Unexpected();
        _position += literal.Length;
        return value;
    }

    private void SkipWhitespace()
    {
        while (_position < text.Length && text[_position] is ' ' or '\t' or '\n' or '\r')
            _position++;
    }

    private int Peek() => _position < text.Length ? text[_position] : -1;

    private bool TryConsume(char expected)
    {
        if (Peek() != expected)
            return false;
        _position++;
        return true;
    }

    private void Expect(char expected)
    {
        if (!TryConsume(expected))
            throw Unexpected();
    }

    private FormatException Unexpected() => Unexpected(_position);

    private FormatException Unexpected(int position) => position >= text.Length
        ? new FormatException("Unexpected end of JSON input")
        : new FormatException($"Unexpected character '{text[position]}' in JSON at position {position}");
}
