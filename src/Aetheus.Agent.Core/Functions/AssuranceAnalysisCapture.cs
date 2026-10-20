// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// One analysis grade of the candidate assurance contract, captured as
/// <c>deploy/scripts/generate-candidate-assurance-contract.mjs</c> captured it: the single summary file
/// of that name under the directory is copied into the evidence directory and graded; no summary, two,
/// or one that is not JSON gives an Unavailable grade F with a generated evidence file instead. A summary
/// whose run identity is invalid or whose commit is another one stops the generation.
/// </summary>
internal sealed partial class AssuranceAnalysisCapture(string sourceSha, string evidenceDirectory, string contractDirectory)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // readdirSync reports a symbolic link as neither a directory nor a file, so the search neither
    // follows nor matches one; hidden and system entries are listed like any other, and an unreadable
    // directory stops the generation instead of being skipped.
    private static readonly EnumerationOptions EveryEntry = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    [GeneratedRegex("^[A-F]\\z")]
    private static partial Regex GradeLetter();

    /// <summary><c>/^[A-F]$/.test(value ?? "")</c>, the value converted with <c>String()</c>.</summary>
    public static bool IsGradeLetter(object? value) => GradeLetter().IsMatch(JsValue.ToJsString(JsValue.Coalesce(value, string.Empty)));

    public async Task<JsObject> CaptureAsync(string label, string directory, string fileName, CancellationToken cancellationToken)
    {
        var files = FindFiles(directory, fileName);
        if (files.Count == 0)
            return await UnavailableAsync(label, "summary-missing", cancellationToken).ConfigureAwait(false);
        if (files.Count != 1)
            return await UnavailableAsync(label, "summary-ambiguous", cancellationToken).ConfigureAwait(false);

        var (bytes, summary) = await ReadSummaryAsync(files[0], cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return await UnavailableAsync(label, "summary-invalid-json", cancellationToken).ConfigureAwait(false);
        return Available(label, files[0], bytes, summary);
    }

    private static async Task<(byte[]? Bytes, object? Summary)> ReadSummaryAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return (bytes, JsJson.Parse(Utf8.GetString(bytes)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InsufficientExecutionStackException)
        {
            // The script's try covered the read and the parse alike.
            return (null, null);
        }
    }

    private JsObject Available(string label, string path, byte[] bytes, object? summary)
    {
        var grade = JsValue.GetOptional(JsValue.GetOptional(summary, "grade"), "overallGrade");
        var completeness = JsValue.GetOptional(JsValue.GetOptional(summary, "grade"), "completeness");
        var commitHash = JsValue.GetOptional(JsValue.GetOptional(summary, "grade"), "commitHash");
        var pipelineRunId = JsValue.Coalesce(
            JsValue.GetOptional(summary, "pipelineRunId"),
            JsValue.GetOptional(JsValue.GetOptional(summary, "grade"), "pipelineRunId"));
        // summary.status, not summary?.status: a summary that is JSON null stops the script.
        var gateStatus = JsValue.ToJsString(JsValue.Coalesce(JsValue.Get(summary, "status"), "Unknown"));
        if (!JsValue.IsPositiveSafeInteger(pipelineRunId))
            throw new AssuranceContractException($"{label} pipeline run identity is invalid.");
        if (commitHash is not string hash || !string.Equals(JsValue.AsciiLower(hash), JsValue.AsciiLower(sourceSha), StringComparison.Ordinal))
            throw new AssuranceContractException($"{label} summary belongs to another commit.");

        var destination = Destination(label);
        File.Copy(path, destination, overwrite: true);
        var gateUsable = gateStatus is "Passed" or "Warning";
        var gradeAvailable = IsGradeLetter(grade) && JsValue.StrictEquals(completeness, "Complete") && gateUsable;
        var analysis = new JsObject();
        analysis.Set("status", gradeAvailable ? "Available" : "Unavailable");
        analysis.Set("reason", Reason(gradeAvailable, gateUsable));
        analysis.Set("reportedGrade", IsGradeLetter(grade) ? grade : null);
        analysis.Set("effectiveGrade", gradeAvailable ? grade : "F");
        analysis.Set("completeness", completeness);
        analysis.Set("gateStatus", gateStatus);
        analysis.Set("pipelineRunId", pipelineRunId);
        analysis.Set("sourceSha", hash);
        analysis.Set("sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        analysis.Set("evidence", Evidence(destination));
        return analysis;
    }

    /// <summary>The reason of a captured grade; undefined (so not written) when it is available.</summary>
    private static object Reason(bool gradeAvailable, bool gateUsable) => gradeAvailable
        ? JsUndefined.Value
        : gateUsable ? "grade-incomplete-or-unavailable" : "gate-error-or-blocked";

    private async Task<JsObject> UnavailableAsync(string label, string reason, CancellationToken cancellationToken)
    {
        var destination = Destination(label);
        var lowerSha = JsValue.AsciiLower(sourceSha);
        var evidence = new JsObject();
        evidence.Set("schema", 1d);
        evidence.Set("status", "Unavailable");
        evidence.Set("reason", reason);
        evidence.Set("sourceSha", lowerSha);
        evidence.Set("generatedBy", "candidate-assurance");
        var bytes = Utf8.GetBytes(JsJson.StringifyIndented(evidence) + "\n");
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);

        var analysis = new JsObject();
        analysis.Set("status", "Unavailable");
        analysis.Set("reason", reason);
        analysis.Set("reportedGrade", null);
        analysis.Set("effectiveGrade", "F");
        analysis.Set("completeness", "Incomplete");
        analysis.Set("gateStatus", "Unavailable");
        analysis.Set("pipelineRunId", null);
        analysis.Set("sourceSha", lowerSha);
        analysis.Set("sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        analysis.Set("evidence", Evidence(destination));
        return analysis;
    }

    private string Destination(string label) => Path.Combine(evidenceDirectory, $"{label}-summary.json");

    /// <summary><c>relative(join(output, ".."), destination)</c> with forward slashes.</summary>
    private string Evidence(string destination)
    {
        var relative = Path.GetRelativePath(contractDirectory, destination);
        // path.relative answers "" where Path.GetRelativePath answers ".".
        return (relative == "." ? string.Empty : relative).Replace('\\', '/');
    }

    /// <summary>The files of that exact name under the directory, at any depth, in code-unit order;
    /// none when the directory does not exist.</summary>
    private static List<string> FindFiles(string root, string fileName)
    {
        if (!Directory.Exists(root))
            return [];
        var matches = new List<string>();
        Visit(new DirectoryInfo(root), fileName, matches);
        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    private static void Visit(DirectoryInfo directory, string fileName, List<string> matches)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", EveryEntry))
        {
            if (entry.LinkTarget is not null)
                continue;
            if (entry is DirectoryInfo child)
                Visit(child, fileName, matches);
            else if (string.Equals(entry.Name, fileName, StringComparison.Ordinal))
                matches.Add(entry.FullName);
        }
    }
}

/// <summary>A refusal the ported assurance scripts raised with <c>throw new Error(message)</c>; the
/// message is the script's, verbatim.</summary>
internal sealed class AssuranceContractException(string message) : Exception(message);
