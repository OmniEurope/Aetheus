// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Where the scalar value of a <c>key: value</c> line sits in that line: <c>[Start, End)</c> covers the
/// value alone, without the spaces around it or an end-of-line comment. <see cref="Quote"/> is the
/// quote it is written in, <c>'\0'</c> for a plain or an empty value.
/// </summary>
internal sealed record ValueSpan(int Start, int End, char Quote)
{
    public bool IsEmpty => Start == End;

    /// <summary>The value in the style it is already written in; an empty value, or a plain one that
    /// would not stay a plain scalar, is double quoted.</summary>
    public string Render(string value) => Quote switch
    {
        '\'' => $"'{value}'",
        '"' => $"\"{value}\"",
        _ => !IsEmpty && IsSafePlain(value) ? value : $"\"{value}\""
    };

    /// <summary>Finds the value after the colon at <paramref name="afterColon"/>; null with the reason
    /// (a block, flow, anchored, aliased or tagged value, or a quoted one running over several lines).</summary>
    public static ValueSpan? Locate(string text, int afterColon, out string? error)
    {
        error = null;
        var start = afterColon;
        while (start < text.Length && text[start] is ' ' or '\t')
        {
            start++;
        }

        if (start == text.Length || text[start] == '#')
        {
            return new ValueSpan(start, start, '\0');
        }

        if (text[start] is '"' or '\'')
        {
            return LocateQuoted(text, start, out error);
        }

        if (text[start] is '|' or '>' or '[' or '{' or '&' or '*' or '!' or '%' or '@' or '`')
        {
            error = "is written as a block, a flow, an anchor, an alias or a tag";
            return null;
        }

        return new ValueSpan(start, PlainEnd(text, start), '\0');
    }

    private static ValueSpan? LocateQuoted(string text, int start, out string? error)
    {
        error = null;
        var quote = text[start];
        var close = start + 1;
        while (close < text.Length)
        {
            if (quote == '"' && text[close] == '\\')
            {
                close += 2;
            }
            else if (text[close] == quote && quote == '\'' && close + 1 < text.Length && text[close + 1] == '\'')
            {
                close += 2;
            }
            else if (text[close] == quote)
            {
                break;
            }
            else
            {
                close++;
            }
        }

        if (close >= text.Length)
        {
            error = "is a quoted scalar running over several lines";
            return null;
        }

        // Anything but a comment after the closing quote is a syntax error the parser has already refused.
        return new ValueSpan(start, close + 1, quote);
    }

    private static int PlainEnd(string text, int start)
    {
        var end = text.Length;
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '#' && text[i - 1] is ' ' or '\t')
            {
                end = i - 1;
                break;
            }
        }

        while (end > start && text[end - 1] is ' ' or '\t')
        {
            end--;
        }

        return end;
    }

    private static bool IsSafePlain(string value) =>
        value.Length > 0
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '*' or '/' or ',' or '-' or '?');
}
