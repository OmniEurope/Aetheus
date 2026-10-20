// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of <c>deploy/scripts/list-failed-tests.mjs</c>, both halves: <c>record</c>, which the ported
/// suite runners call (<see cref="RecordAsync"/>), and <c>publish</c>, which the
/// <c>publish-failed-tests</c> function runs over TRX files and failed-tests lists
/// (<see cref="Collect"/>). The failed, errored, timed-out and aborted tests of a TRX, each once and in
/// file order, made safe to travel on one <c>##aetheus[setvariable]</c> line (no control character,
/// no comma, no <c>$</c>, backtick or backslash, at most 200 characters) and bounded so a run with a
/// thousand failures still publishes a readable value. Naming the tests is a diagnostic, never the
/// verdict.
/// </summary>
internal static partial class FailedTestNames
{
    internal const int MaxNames = 20;
    internal const int MaxValueLength = 1500;
    private const int MaxNameLength = 200;

    private static readonly HashSet<string> FailedOutcomes = new(StringComparer.Ordinal) { "Failed", "Error", "Timeout", "Aborted" };

    [GeneratedRegex("<UnitTestResult\\b([^>]*)>")]
    private static partial Regex UnitTestResult();

    [GeneratedRegex("([A-Za-z][A-Za-z0-9-]*)=\"([^\"]*)\"")]
    private static partial Regex Attribute();

    [GeneratedRegex("&#x([0-9a-fA-F]+);")]
    private static partial Regex HexEntity();

    [GeneratedRegex("&#([0-9]+);")]
    private static partial Regex DecimalEntity();

    /// <summary>JavaScript's <c>\s</c>, which .NET's differs from (NEL, BOM).</summary>
    [GeneratedRegex("[\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff]+")]
    private static partial Regex Whitespace();

    /// <summary>The failed test names of one TRX, in file order, each once.</summary>
    public static List<string> FromTrx(string trx)
    {
        var names = new List<string>();
        foreach (Match result in UnitTestResult().Matches(trx))
        {
            var attributes = Attributes(result.Groups[1].Value);
            if (!FailedOutcomes.Contains(attributes.GetValueOrDefault("outcome") ?? string.Empty)
                || attributes.GetValueOrDefault("testName") is not { Length: > 0 } testName)
                continue;
            AddOnce(names, Sanitize(DecodeXml(testName)));
        }
        return names;
    }

    /// <summary>Every source's names merged in order, each once. An absent source is skipped; a
    /// <c>.trx</c> file is read as TRX, anything else as one name per line.</summary>
    public static List<string> Collect(IEnumerable<string> sources)
    {
        var names = new List<string>();
        foreach (var source in sources.Where(File.Exists))
            foreach (var name in Read(source))
                AddOnce(names, name);
        return names;
    }

    private static IEnumerable<string> Read(string path)
    {
        var text = File.ReadAllText(path);
        if (path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            return FromTrx(text);
        return text.Split('\n').Select(line => Sanitize(line.TrimEnd('\r'))).Where(name => name.Length > 0);
    }

    private static void AddOnce(List<string> names, string name)
    {
        if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
            names.Add(name);
    }

    /// <summary>One display-safe name, as <c>sanitizeTestName</c> made it.</summary>
    public static string Sanitize(string name)
    {
        var cleaned = new StringBuilder(name.Length);
        foreach (var character in name)
            cleaned.Append(character switch
            {
                < ' ' or '\u007f' => ' ',
                ',' => ';',
                '$' or '`' or '\\' => '_',
                _ => character
            });
        var collapsed = Whitespace().Replace(cleaned.ToString(), " ").Trim(' ');
        return collapsed.Length > MaxNameLength ? collapsed[..(MaxNameLength - 3)] + "..." : collapsed;
    }

    /// <summary>The single-line value: the first names that fit, then <c>+N more</c>.</summary>
    public static string Format(IReadOnlyList<string> names)
    {
        var shown = new List<string>();
        var length = 0;
        foreach (var name in names)
        {
            var added = (shown.Count == 0 ? 0 : 2) + name.Length;
            // Room is kept for the "+N more" tail.
            if (shown.Count >= MaxNames || length + added > MaxValueLength - 16)
                break;
            shown.Add(name);
            length += added;
        }
        var rest = names.Count - shown.Count;
        if (rest > 0)
            shown.Add(string.Create(CultureInfo.InvariantCulture, $"+{rest} more"));
        return string.Join(", ", shown);
    }

    public static string LogLine(IReadOnlyList<string> names) =>
        string.Create(CultureInfo.InvariantCulture, $"Failed tests ({names.Count}): {Format(names)}");

    /// <summary>The <c>record</c> mode the suite runners ran when their status was not 0: the names
    /// written one per line to <paramref name="listPath"/> and logged, or, when the TRX cannot be
    /// read, said so in the log without changing the verdict.</summary>
    public static async Task RecordAsync(PipelineFunctionContext context, string trxPath, string listPath, CancellationToken cancellationToken)
    {
        try
        {
            var names = FromTrx(await File.ReadAllTextAsync(trxPath, cancellationToken).ConfigureAwait(false));
            await File.WriteAllTextAsync(listPath, string.Concat(names.Select(name => name + "\n")), cancellationToken).ConfigureAwait(false);
            if (names.Count > 0)
                await context.Log(LogLine(names)).ConfigureAwait(false);
        }
        // An entity naming no code point is the script's RangeError: the names are lost, not the verdict.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or OverflowException)
        {
            await context.Log($"The failed test names could not be read from {trxPath}.", TaskLogLevel.Error).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> Attributes(string fragment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Attribute().Matches(fragment))
            values[match.Groups[1].Value] = match.Groups[2].Value;
        return values;
    }

    private static string DecodeXml(string text)
    {
        var decoded = text
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal);
        decoded = HexEntity().Replace(decoded, match => CodePoint(int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        decoded = DecimalEntity().Replace(decoded, match => CodePoint(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
        return decoded.Replace("&amp;", "&", StringComparison.Ordinal);
    }

    private static string CodePoint(int value) => char.ConvertFromUtf32(value);
}
