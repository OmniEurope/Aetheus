// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>rebase-coverage-sources</c> function, port of
/// the former <c>rebase-quality-coverage-sources.mjs</c> script. The Cobertura reports restored from CI
/// name CI's own workspace in their <c>&lt;source&gt;</c> roots; every root must be an absolute path
/// below a <c>src</c> directory (Coverlet picks a different common root for single- and multi-project
/// suites), and is rebased onto this workspace's <c>src</c> in a copy under the output directory, the
/// restored reports left unchanged. The copies, sorted as the script sorted them and joined by
/// <c>;</c> for ReportGenerator, are the <c>COVERAGE_REPORTS</c> output the script printed.
/// </summary>
public sealed partial class RebaseCoverageSourcesFunction : IPipelineFunction
{
    private const string ReportName = "coverage.cobertura.xml";

    /// <summary>The script read and wrote UTF-8 without touching a byte order mark.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex("<source>([^<]*)</source>")]
    private static partial Regex SourceElement();

    [GeneratedRegex("^/|^[A-Za-z]:/")]
    private static partial Regex AbsolutePath();

    public string Name => PipelineFunctionCatalog.RebaseCoverageSources.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var sourceDirectory = Path.GetFullPath("src", context.Workspace);
        if (!Directory.Exists(sourceDirectory))
            return PipelineFunctionResult.Failure($"Source directory is unavailable: {sourceDirectory}");
        var coverageDirectory = Path.GetFullPath("coverage", context.Workspace);
        var reports = FindReports(coverageDirectory).Order(StringComparer.Ordinal).ToList();
        if (reports.Count == 0)
            return PipelineFunctionResult.Failure("No raw Cobertura report was found under coverage/.");

        var output = Path.GetFullPath(context.Parameters["output"], context.Workspace);
        var sourceRoot = sourceDirectory.Replace('\\', '/');
        var rebased = new List<string>();
        foreach (var report in reports)
        {
            var text = Utf8.GetString(await File.ReadAllBytesAsync(report, cancellationToken).ConfigureAwait(false));
            var rewrite = Rebase(text, sourceRoot, report);
            if (rewrite.Error is not null)
                return PipelineFunctionResult.Failure(rewrite.Error);
            var destination = Path.Combine(output, Path.GetRelativePath(coverageDirectory, report));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, Utf8.GetBytes(rewrite.Text!), cancellationToken).ConfigureAwait(false);
            rebased.Add(destination);
        }

        if (rebased.Any(path => path.Contains(';', StringComparison.Ordinal)))
            return PipelineFunctionResult.Failure("Report path contains a semicolon and cannot be passed to ReportGenerator.");
        await context.Log($"Rebased {rebased.Count} Cobertura report(s) onto {sourceRoot}.").ConfigureAwait(false);
        context.SetOutput("COVERAGE_REPORTS", string.Join(';', rebased));
        return PipelineFunctionResult.Success();
    }

    /// <summary>One report with every source root rebased, or the script's refusal.</summary>
    internal static (string? Text, string? Error) Rebase(string report, string sourceRoot, string reportPath)
    {
        string? error = null;
        var count = 0;
        var text = SourceElement().Replace(report, match =>
        {
            var source = match.Groups[1].Value;
            var normalized = source.Replace('\\', '/');
            if (!normalized.EndsWith('/'))
                normalized += "/";
            var srcIndex = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
            if (!AbsolutePath().IsMatch(normalized) || srcIndex < 0)
            {
                error ??= $"Unexpected Cobertura source root in {reportPath}: {source}";
                return match.Value;
            }
            count++;
            return $"<source>{XmlEscape(sourceRoot + normalized[(srcIndex + "/src".Length)..])}</source>";
        });
        if (error is not null)
            return (null, error);
        return count == 0 ? (null, $"Cobertura report has no source root: {reportPath}") : (text, null);
    }

    /// <summary>The script's recursive walk: real directories and regular files only.</summary>
    private static IEnumerable<string> FindReports(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
                continue;
            if (entry is DirectoryInfo)
                foreach (var report in FindReports(entry.FullName))
                    yield return report;
            else if (entry.Name == ReportName)
                yield return entry.FullName;
        }
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
