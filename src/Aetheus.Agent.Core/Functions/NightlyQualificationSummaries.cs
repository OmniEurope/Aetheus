// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The reading half of the former <c>record-nightly-qualification.mjs</c> script, with the script's
/// JavaScript semantics on the parsed values (<see cref="JsJson"/>, <see cref="JsValue"/>):
/// <c>findSummary</c>, <c>severityCount</c> and <c>describe</c>.
/// </summary>
internal static partial class NightlyQualificationSummaries
{
    private const string NotIterable = " is not iterable (cannot read property Symbol(Symbol.iterator))";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex("^[A-F]\\z")]
    public static partial Regex Grade();

    /// <summary>
    /// <c>findSummary(root, fileName)</c>: null without a directory, else the parsed content of the only
    /// file of that name at any depth, null when there is none, more than one, or it cannot be read or
    /// parsed. Like <c>readdirSync</c>'s entry types, a symbolic link is neither visited nor counted; a
    /// directory that cannot be listed fails, as the script's uncaught error did.
    /// </summary>
    public static object? Find(string workspace, string? root, string fileName)
    {
        if (string.IsNullOrEmpty(root))
            return null;
        var directory = Path.GetFullPath(Path.Combine(workspace, root));
        if (!Directory.Exists(directory))
            return null;
        var matches = new List<string>();
        Visit(new DirectoryInfo(directory), fileName, matches);
        if (matches.Count != 1)
            return null;
        try
        {
            return JsJson.Parse(Utf8.GetString(File.ReadAllBytes(matches[0])));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            // JSON.parse or readFileSync threw inside the script's try: the summary is unavailable.
            return null;
        }
    }

    private static void Visit(DirectoryInfo directory, string fileName, List<string> matches)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            if (entry.LinkTarget is not null)
                continue;
            if (entry is DirectoryInfo child)
                Visit(child, fileName, matches);
            else if (string.Equals(entry.Name, fileName, StringComparison.Ordinal))
                matches.Add(entry.FullName);
        }
    }

    /// <summary><c>severityCount(summary, severity)</c>: the sum of the observed numeric values of the
    /// measures of that severity (compared lowercase, untrimmed), or null when none was observed or the
    /// domains are not an array.</summary>
    public static double? SeverityCount(object? summary, string severity)
    {
        if (JsValue.GetOptional(JsValue.GetOptional(summary, "grade"), "domains") is not List<object?> domains)
            return null;
        double? total = null;
        foreach (var domain in domains)
        {
            foreach (var measure in Iterate(JsValue.Coalesce(JsValue.GetOptional(domain, "measures"), new List<object?>())))
            {
                var name = JsValue.ToJsString(JsValue.Coalesce(JsValue.GetOptional(measure, "severity"), ""));
                if (!string.Equals(JsValue.AsciiLower(name), severity, StringComparison.Ordinal))
                    continue;
                if (!JsValue.StrictEquals(JsValue.GetOptional(measure, "observed"), true) || JsValue.GetOptional(measure, "observedValue") is not double value)
                    continue;
                total = (total ?? 0) + value;
            }
        }
        return total;
    }

    /// <summary>
    /// <c>for (const measure of value)</c>: the elements of an array; nothing for a string, whose
    /// characters have none of the properties read (the comparison reads them as an empty severity); V8's
    /// TypeError for any other value.
    /// </summary>
    private static IEnumerable<object?> Iterate(object? value) => value switch
    {
        List<object?> array => array,
        string => [],
        bool flag => throw new JsTypeError($"boolean {(flag ? "true" : "false")}{NotIterable}"),
        double number => throw new JsTypeError($"number {JsValue.FormatNumber(number)}{NotIterable}"),
        _ => throw new JsTypeError("object" + NotIterable)
    };

    /// <summary><c>describe(summary)</c>: status, grade (one letter A to F, else null), completeness,
    /// safe-integer run id and commit; only the first four, unavailable, for a falsy summary.</summary>
    public static JsObject Describe(object? summary)
    {
        var description = new JsObject();
        if (!JsValue.Truthy(summary))
        {
            description.Set("status", "Unavailable");
            description.Set("grade", null);
            description.Set("completeness", null);
            description.Set("pipelineRunId", null);
            return description;
        }
        var grade = JsValue.GetOptional(summary, "grade");
        var overallGrade = JsValue.Coalesce(JsValue.GetOptional(grade, "overallGrade"), "");
        var pipelineRunId = JsValue.GetOptional(summary, "pipelineRunId");
        description.Set("status", JsValue.ToJsString(JsValue.Coalesce(JsValue.Get(summary, "status"), "Unknown")));
        description.Set("grade", Grade().IsMatch(JsValue.ToJsString(overallGrade)) ? overallGrade : null);
        description.Set("completeness", JsValue.Coalesce(JsValue.GetOptional(grade, "completeness"), null));
        description.Set("pipelineRunId", JsValue.IsSafeInteger(pipelineRunId) ? pipelineRunId : null);
        description.Set("commitHash", JsValue.Coalesce(JsValue.GetOptional(grade, "commitHash"), null));
        return description;
    }

    /// <summary><c>{ ...describe(summary), criticalFindings, highFindings }</c>.</summary>
    public static JsObject DescribeWithCounts(object? summary)
    {
        var description = Describe(summary);
        description.Set("criticalFindings", SeverityCount(summary, "critical"));
        description.Set("highFindings", SeverityCount(summary, "high"));
        return description;
    }

    /// <summary>Whether the summary names a commit, as a string, other than the source commit.</summary>
    public static bool BelongsToAnotherCommit(object? summary, string lowerSourceSha) =>
        JsValue.Truthy(summary)
        && JsValue.GetOptional(JsValue.GetOptional(summary, "grade"), "commitHash") is string commit
        && !string.Equals(JsValue.AsciiLower(commit), lowerSourceSha, StringComparison.Ordinal);
}
