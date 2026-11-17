// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>
/// Turns the simple <c>OnCalendar=</c> forms of a systemd timer into the five-field expression the planner reads:
/// <c>hourly</c>, <c>daily</c>, <c>weekly</c>, <c>monthly</c>, <c>quarterly</c>, <c>semiannually</c>, <c>yearly</c>, and
/// <c>[Mon,Wed|Mon..Fri] [*-*-*|*-*-15] HH:MM[:00] [zone]</c> (the hour and the minute may be <c>*</c>, a list or <c>*/n</c>).
/// Everything else (a date with a month, seconds other than zero, a range that wraps) returns false: nothing is guessed.
/// </summary>
internal static partial class OnCalendarConverter
{
    private static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    [GeneratedRegex(@"^(\*|\d{1,2}(,\d{1,2})*|\*/\d{1,2})$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^(?<h>[\d,*/]+):(?<m>[\d,*/]+)(:00?)?$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^\*-\*-(\*|\d{1,2})$")]
    private static partial Regex DatePattern();

    [GeneratedRegex("^(mon|tue|wed|thu|fri|sat|sun)[a-z]*$", RegexOptions.IgnoreCase)]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"^[A-Za-z_]+(/[A-Za-z0-9_+\-]+)*$")]
    private static partial Regex ZonePattern();

    public static bool TryConvert(string onCalendar, out string cron, out string? zoneId)
    {
        cron = string.Empty;
        zoneId = null;
        var words = onCalendar.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return false;

        zoneId = TakeZone(words);

        if (words.Count == 1 && Shorthand(words[0]) is { } shorthand)
        {
            cron = shorthand;
            return true;
        }

        if (!TryTakeDays(words, out var dayOfWeek) || !TryTakeDayOfMonth(words, out var dayOfMonth)) return false;
        if (words.Count != 1 || TimePattern().Match(words[0]) is not { Success: true } time) return false;
        var hour = time.Groups["h"].Value;
        var minute = time.Groups["m"].Value;
        if (!FieldPattern().IsMatch(hour) || !FieldPattern().IsMatch(minute)) return false;
        cron = $"{WithoutLeadingZeros(minute)} {WithoutLeadingZeros(hour)} {WithoutLeadingZeros(dayOfMonth)} * {dayOfWeek}";
        return true;
    }

    // A zone ends the expression: UTC, or an Area/City identifier.
    private static string? TakeZone(List<string> words)
    {
        if (words.Count <= 1) return null;
        var last = words[^1];
        if (char.IsDigit(last[0]) || last[0] == '*' || !ZonePattern().IsMatch(last) || IsDayList(last)) return null;
        words.RemoveAt(words.Count - 1);
        return last;
    }

    private static bool TryTakeDays(List<string> words, out string dayOfWeek)
    {
        dayOfWeek = "*";
        if (words.Count == 0 || !IsDayList(words[0])) return true;
        if (!TryDays(words[0], out dayOfWeek)) return false;
        words.RemoveAt(0);
        return true;
    }

    private static bool TryTakeDayOfMonth(List<string> words, out string dayOfMonth)
    {
        dayOfMonth = "*";
        if (words.Count != 2) return true;
        if (DatePattern().Match(words[0]) is not { Success: true } date) return false;
        dayOfMonth = date.Groups[1].Value;
        words.RemoveAt(0);
        return true;
    }

    // systemd writes 03:05, cron reads 5 3: the numbers of a list lose their leading zero, "*" and "*/n" stay.
    private static string WithoutLeadingZeros(string field) => string.Join(
        ',', field.Split(',').Select(item => item.All(char.IsAsciiDigit) ? int.Parse(item, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture) : item));

    private static string? Shorthand(string word) => word.ToLowerInvariant() switch
    {
        "hourly" => "0 * * * *",
        "daily" => "0 0 * * *",
        "weekly" => "0 0 * * 1",
        "monthly" => "0 0 1 * *",
        "quarterly" => "0 0 1 1,4,7,10 *",
        "semiannually" => "0 0 1 1,7 *",
        "yearly" or "annually" => "0 0 1 1 *",
        _ => null
    };

    private static bool IsDayList(string word) =>
        word.Split(',').All(part => part.Split("..").All(name => DayPattern().IsMatch(name)));

    /// <summary>The days of a systemd list (<c>Mon,Wed</c>, <c>Mon..Fri</c>) as cron day numbers (Sunday is 0).</summary>
    private static bool TryDays(string word, out string days)
    {
        List<string> parts = [];
        foreach (var part in word.Split(','))
        {
            var ends = part.Split("..").Select(name => Array.IndexOf(Days, name[..3].ToLowerInvariant())).ToArray();
            if (ends.Length == 1) parts.Add(ends[0].ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (ends.Length == 2 && ends[0] <= ends[1]) parts.Add($"{ends[0]}-{ends[1]}");
            else
            {
                days = string.Empty;
                return false;
            }
        }

        days = string.Join(',', parts);
        return true;
    }
}
