// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Names the files an OpenGrep scan skipped for their size. OpenGrep only reports a count on its stderr
/// ("Scan skipped: 1 files larger than 1.0 MB") and lists the files solely under <c>--verbose</c>, which
/// also floods the log, so the agent derives them: it reads the count and the limit from that line and
/// walks the scanned source for the files above the limit that the scan arguments do not exclude.
/// The result is a list of candidates (a binary file above the limit that OpenGrep never reads would be
/// listed too), reported next to the count OpenGrep gave.
/// </summary>
internal static partial class OpenGrepSkippedFiles
{
    internal const string ExcludePrefix = "--exclude=";
    private const int MaxListed = 20;

    [GeneratedRegex(
        @"Scan skipped:\s*(?<count>\d+)\s+files?\s+larger than\s+(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMG]?B)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SizeSkipLine();

    /// <summary>The count and the size limit (in bytes, decimal units) of the first "Scan skipped" line
    /// about file size, or null when OpenGrep skipped nothing for its size.</summary>
    internal static (int Count, long LimitBytes)? ParseSizeSkip(IEnumerable<string> stderrLines)
    {
        foreach (var line in stderrLines)
        {
            var match = SizeSkipLine().Match(line);
            if (!match.Success) continue;
            var size = double.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups["unit"].Value switch { "KB" => 1_000d, "MB" => 1_000_000d, "GB" => 1_000_000_000d, _ => 1d };
            return (int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture), (long)(size * unit));
        }
        return null;
    }

    /// <summary>The <c>--exclude=</c> values of the scanner arguments.</summary>
    internal static IReadOnlyList<string> ExcludePatterns(IEnumerable<string> arguments) =>
        [.. arguments
            .Where(argument => argument.StartsWith(ExcludePrefix, StringComparison.Ordinal))
            .Select(argument => argument[ExcludePrefix.Length..])];

    /// <summary>The "Scan skipped" report: what OpenGrep counted, then the candidate files of the source
    /// above the limit (path relative to the source, size in bytes).</summary>
    internal static string Describe(
        (int Count, long LimitBytes) skip, string sourceDirectory, IReadOnlyList<string> excludePatterns)
    {
        var found = FindLargerThan(sourceDirectory, skip.LimitBytes, excludePatterns);
        var header = $"OpenGrep skipped {skip.Count} file(s) larger than {skip.LimitBytes} bytes and did not scan them.";
        if (found.Count == 0)
            return header + " No such file was found in the scanned source outside the excluded paths.";
        var listed = string.Join(", ", found.Take(MaxListed).Select(file => $"{file.Path} ({file.Bytes} bytes)"));
        var more = found.Count > MaxListed ? $", and {found.Count - MaxListed} more" : string.Empty;
        return $"{header} Candidates in the scanned source: {listed}{more}.";
    }

    /// <summary>Every file of <paramref name="sourceDirectory"/> strictly above <paramref name="limitBytes"/>
    /// that no exclude pattern covers, biggest first.</summary>
    internal static IReadOnlyList<(string Path, long Bytes)> FindLargerThan(
        string sourceDirectory, long limitBytes, IReadOnlyList<string> excludePatterns)
    {
        var excludes = excludePatterns.Select(ToExcludeRegex).ToList();
        var found = new List<(string Path, long Bytes)>();
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Entries(Path.Combine(sourceDirectory, directory)))
            {
                var relative = directory.Length == 0 ? entry.Name : directory + "/" + entry.Name;
                if (excludes.Exists(exclude => exclude.IsMatch(relative))) continue;
                if (entry is DirectoryInfo { Attributes: var attributes } && !attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(relative);
                else if (entry is FileInfo file && SizeOf(file) is var length && length > limitBytes)
                    found.Add((relative, length));
            }
        }
        return [.. found.OrderByDescending(file => file.Bytes).ThenBy(file => file.Path, StringComparer.Ordinal)];
    }

    /// <summary>The size of a file, or -1 when it cannot be read (a dangling link, a race with a delete).</summary>
    private static long SizeOf(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static IEnumerable<FileSystemInfo> Entries(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>An OpenGrep <c>--exclude</c> pattern as a regular expression over a relative path with
    /// <c>/</c> separators: a pattern with a slash is anchored at the source root and covers what is
    /// below a match, a pattern without one matches any path segment; <c>*</c> and <c>?</c> stay within a
    /// segment.</summary>
    internal static Regex ToExcludeRegex(string pattern)
    {
        var glob = Regex.Escape(pattern.TrimStart('/')).Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal);
        var expression = pattern.Contains('/', StringComparison.Ordinal)
            ? "^" + glob + "(/.*)?$"
            : "(^|/)" + glob + "(/.*)?$";
        return new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    }
}
