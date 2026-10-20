// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The JavaScript built-ins on text and time the ported Node programs rely on, with V8's results and
/// errors: <c>encodeURIComponent</c>, <c>Number.parseInt(text, 10)</c> and
/// <c>new Date(milliseconds).toISOString()</c>, computed without .NET culture or calendar data.
/// </summary>
internal static class JsText
{
    private const double MaxTime = 8.64e15;
    private const long MillisecondsPerDay = 86_400_000;
    private const string Unreserved = "-_.!~*'()";

    /// <summary><c>encodeURIComponent(text)</c>: ASCII letters, digits and <c>- _ . ! ~ * ' ( )</c> kept,
    /// every other code point percent-encoded as UTF-8 with uppercase hexadecimal digits; a lone
    /// surrogate throws <c>URIError: URI malformed</c>.</summary>
    public static string EncodeUriComponent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        Span<byte> bytes = stackalloc byte[4];
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsAsciiLetterOrDigit(character) || Unreserved.Contains(character, StringComparison.Ordinal))
            {
                builder.Append(character);
                continue;
            }
            var codePoint = CodePointAt(text, ref index);
            var length = new Rune(codePoint).EncodeToUtf8(bytes);
            foreach (var value in bytes[..length])
                builder.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static int CodePointAt(string text, ref int index)
    {
        var character = text[index];
        if (!char.IsSurrogate(character))
            return character;
        if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            return char.ConvertToUtf32(character, text[++index]);
        throw new JsError("URI malformed");
    }

    /// <summary><c>Number.parseInt(text, 10)</c>: leading JavaScript white space skipped, an optional
    /// sign, then the longest run of decimal digits, correctly rounded to a double (<c>-0</c> for
    /// <c>"-0"</c>, Infinity beyond the double range); NaN when no digit follows.</summary>
    public static double ParseInt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rest = JsValue.Trim(text);
        var negative = rest.StartsWith('-');
        if (negative || rest.StartsWith('+'))
            rest = rest[1..];
        var length = 0;
        while (length < rest.Length && char.IsAsciiDigit(rest[length]))
            length++;
        if (length == 0)
            return double.NaN;
        var value = double.Parse(rest.AsSpan(0, length), NumberStyles.None, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    /// <summary><c>new Date(time).toISOString()</c>: <c>TimeClip</c> (beyond ±8.64e15 ms or not finite
    /// throws <c>RangeError: Invalid time value</c>, a fraction is truncated), then the proleptic
    /// Gregorian UTC date, the year on four digits from 0 to 9999 and as a signed six-digit year
    /// outside.</summary>
    public static string ToIsoString(double time)
    {
        if (!double.IsFinite(time) || Math.Abs(time) > MaxTime)
            throw new JsError("Invalid time value");
        var milliseconds = (long)Math.Truncate(time);
        var days = Math.DivRem(milliseconds, MillisecondsPerDay, out var ofDay);
        if (ofDay < 0)
        {
            days--;
            ofDay += MillisecondsPerDay;
        }
        var (year, month, day) = CivilFromDays(days);
        var yearText = year is >= 0 and <= 9999
            ? year.ToString("D4", CultureInfo.InvariantCulture)
            : (year < 0 ? "-" : "+") + Math.Abs(year).ToString("D6", CultureInfo.InvariantCulture);
        var time24 = TimeSpan.FromMilliseconds(ofDay);
        return string.Create(CultureInfo.InvariantCulture,
            $"{yearText}-{month:D2}-{day:D2}T{time24.Hours:D2}:{time24.Minutes:D2}:{time24.Seconds:D2}.{time24.Milliseconds:D3}Z");
    }

    /// <summary>The proleptic Gregorian date of a day count from 1970-01-01 (Howard Hinnant's
    /// <c>civil_from_days</c>).</summary>
    private static (long Year, long Month, long Day) CivilFromDays(long days)
    {
        var shifted = days + 719_468;
        var era = (shifted >= 0 ? shifted : shifted - 146_096) / 146_097;
        var dayOfEra = shifted - era * 146_097;
        var yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36_524 - dayOfEra / 146_096) / 365;
        var dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
        var monthIndex = (5 * dayOfYear + 2) / 153;
        var day = dayOfYear - (153 * monthIndex + 2) / 5 + 1;
        var month = monthIndex < 10 ? monthIndex + 3 : monthIndex - 9;
        return (yearOfEra + era * 400 + (month <= 2 ? 1 : 0), month, day);
    }
}
