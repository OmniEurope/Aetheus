// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Aetheus.Agent.Core.Operations;

internal static class PipelineCoveragePublisher
{
    public static async Task<ExecutorResult> PublishAsync(
        IServerApiClient apiClient, ILogger logger, TimeProvider timeProvider, string target,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var context = await PipelinePublicationContext.CreateAsync(envVars, timeProvider, onOutput).ConfigureAwait(false);
        if (context is null) return new ExecutorResult(-1, false);
        var (runId, stageName, baseDir, startedAt) = context;
        var patterns = ParsePatterns(target);
        await onOutput(
            $"Coverage workspace: '{baseDir}' (exists={Directory.Exists(baseDir)}); patterns: {string.Join(", ", patterns)}",
            TaskLogLevel.Info).ConfigureAwait(false);
        var file = await FindCoverageFileAsync(baseDir, patterns, onOutput).ConfigureAwait(false);
        if (file is null) return new ExecutorResult(1, false);
        if (!await MatchesExpectedCommitAsync(baseDir, envVars, ct).ConfigureAwait(false))
        {
            await onOutput("Coverage source SHA does not match the checked-out workspace.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var rawXml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        if (!TryNormalizeCoverage(rawXml, out var xml, out var covered, out var valid))
        {
            await onOutput("Coverage report is empty, malformed or has invalid line totals.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var linePercent = 100.0 * covered / valid;
        var relativePath = Path.GetRelativePath(baseDir, file);
        var toolName = Value(envVars, "AETHEUS_COVERAGE_TOOL") ?? "Coverlet";
        var language = Value(envVars, "AETHEUS_COVERAGE_LANGUAGE") ?? "multi";
        var toolVersion = Value(envVars, "AETHEUS_COVERAGE_VERSION") ?? "project-lock";
        var scannerKey = BuildScannerKey(toolName);
        await onOutput($"Publishing merged coverage from {relativePath}: {linePercent:0.##}% ({covered}/{valid})", TaskLogLevel.Info).ConfigureAwait(false);
        var publication = await PublishCoverageReportAsync(
            apiClient, logger, timeProvider, runId, xml, stageName, relativePath, file,
            scannerKey, toolName, toolVersion, language, covered, valid, linePercent, startedAt,
            onOutput, ct).ConfigureAwait(false);
        if (publication is not null) return publication;
        if (!await MeetsMinimumAsync(envVars, linePercent, onOutput).ConfigureAwait(false))
            return new ExecutorResult(1, false);

        await onOutput("Coverage published successfully", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    internal static async Task<string?> FindCoverageFileAsync(
        string baseDir,
        IReadOnlyCollection<string> patterns,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        var files = patterns
            .SelectMany(pattern => WorkspaceFileMatcher.GetMatchingFiles(baseDir, pattern))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 1) return files[0];
        if (files.Count > 1)
        {
            await onOutput(
                $"Coverage publish requires one merged Cobertura report; found {files.Count} raw reports.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)) + Path.DirectorySeparatorChar;
        foreach (var pattern in patterns.Where(pattern => !pattern.Contains('*') && !pattern.Contains('?')))
        {
            // targetFiles comes from the pipeline YAML. An absolute or ../ pattern makes Path.Combine
            // drop the workspace, and the diagnostic below would tell the run log whether any path of
            // the host exists: only a candidate inside the workspace is probed.
            var candidate = Path.GetFullPath(Path.Combine(baseDir, pattern));
            if (!candidate.StartsWith(workspace, StringComparison.Ordinal))
            {
                await onOutput($"Coverage pattern '{pattern}' points outside the workspace; not probed.", TaskLogLevel.Error)
                    .ConfigureAwait(false);
                continue;
            }
            await onOutput(
                $"Coverage candidate missing: '{candidate}' (file={File.Exists(candidate)}, directory={Directory.Exists(Path.GetDirectoryName(candidate))})",
                TaskLogLevel.Error).ConfigureAwait(false);
        }
        await onOutput(
            "No coverage files found matching patterns - failing (coverage step published nothing).",
            TaskLogLevel.Error).ConfigureAwait(false);
        return null;
    }

    private static string BuildScannerKey(string toolName)
    {
        var key = "coverage-" + string.Concat(toolName.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')).Trim('-');
        return key.Length > 100 ? key[..100] : key;
    }

    private static async Task<ExecutorResult?> PublishCoverageReportAsync(
        IServerApiClient apiClient,
        ILogger logger,
        TimeProvider timeProvider,
        int runId,
        string xml,
        string? stageName,
        string relativePath,
        string file,
        string scannerKey,
        string toolName,
        string toolVersion,
        string language,
        long covered,
        long valid,
        double linePercent,
        DateTime startedAt,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        try
        {
            await apiClient.PublishCoverageAsync(runId, xml, stageName, relativePath, ct).ConfigureAwait(false);
            var artifactId = await AnalysisArtifactUploader.UploadFileAsync(
                apiClient, runId, $"analysis-{scannerKey}", stageName, relativePath, file, ct).ConfigureAwait(false);
            var analysis = await apiClient.PublishAnalysisReportAsync(
                runId,
                BuildAnalysisRequest(
                    scannerKey, toolName, toolVersion, language, covered, valid, linePercent,
                    relativePath, artifactId, stageName, startedAt, timeProvider.GetUtcNow().UtcDateTime),
                ct).ConfigureAwait(false);
            if (analysis is null)
            {
                await onOutput("Coverage analysis publication returned no result.", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(1, false);
            }
            if (analysis.GateStatus is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error)
                await onOutput(
                    $"Coverage verdict deferred to the common gate: {analysis.GateStatus}.",
                    TaskLogLevel.Warning).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish coverage for run {RunId}", runId);
            await onOutput($"Coverage publish failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
    }

    private static PublishAnalysisReportRequest BuildAnalysisRequest(
        string scannerKey,
        string toolName,
        string toolVersion,
        string language,
        long covered,
        long valid,
        double linePercent,
        string relativePath,
        int? artifactId,
        string? stageName,
        DateTime startedAt,
        DateTime completedAt) => new()
        {
            ScannerKey = scannerKey,
            ScannerName = toolName,
            ScannerVersion = toolVersion,
            Category = AnalysisCategory.Coverage,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ReportContent = JsonSerializer.Serialize(new
            {
                metrics = new[]
            {
                new { key = "coverage.line.percent", value = linePercent, unit = "percent", scope = "project", language, toolName, direction = "HigherIsBetter" },
                new { key = "coverage.lines.covered", value = (double)covered, unit = "lines", scope = "project", language, toolName, direction = "HigherIsBetter" },
                new { key = "coverage.lines.valid", value = (double)valid, unit = "lines", scope = "project", language, toolName, direction = "Informational" }
            }
            }),
            ReportPath = relativePath,
            PipelineArtifactId = artifactId,
            StageName = stageName,
            StepName = relativePath,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };

    private static async Task<bool> MeetsMinimumAsync(
        IReadOnlyDictionary<string, string> envVars,
        double linePercent,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (!envVars.TryGetValue("AETHEUS_MIN_COVERAGE", out var minimumText)
            || !double.TryParse(minimumText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum)
            || linePercent >= minimum) return true;
        await onOutput(
            $"Coverage below threshold: {linePercent:0.##}% < {minimum:0.##}%",
            TaskLogLevel.Error).ConfigureAwait(false);
        return false;
    }

    private static List<string> ParsePatterns(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return ["**/coverage.cobertura.xml"];
        try { return JsonSerializer.Deserialize<List<string>>(target) ?? ["**/coverage.cobertura.xml"]; }
        catch (JsonException) { return [target]; }
    }

    private static async Task<bool> MatchesExpectedCommitAsync(
        string baseDir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        var expected = envVars.GetValueOrDefault("BUILD_SOURCEVERSION");
        if (string.IsNullOrWhiteSpace(expected)) expected = envVars.GetValueOrDefault("AETHEUS_SOURCE_COMMIT");
        if (string.IsNullOrWhiteSpace(expected) || !Directory.Exists(Path.Combine(baseDir, ".git"))) return true;

        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = baseDir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");
        GitRepositoryEnvironment.Neutralize(startInfo.Environment);
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return false;
        var actual = (await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryNormalizeCoverage(string rawXml, out string xml, out long covered, out long valid)
    {
        xml = rawXml;
        covered = 0;
        valid = 0;
        XElement? root;
        try
        {
            root = LoadCoverageDocument(rawXml).Root;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
        if (root?.Name.LocalName == "coverage")
            return TryReadCoberturaTotals(root, out covered, out valid);
        if (root?.Name.LocalName != "report"
            || !TryReadJacocoLineTotals(root, out covered, out valid)) return false;
        xml = ConvertJacocoToCobertura(root, covered, valid);
        return true;
    }

    private static XDocument LoadCoverageDocument(string rawXml)
    {
        using var text = new StringReader(rawXml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            // Cobertura (including NYC/Istanbul) commonly emits the standard external SYSTEM
            // doctype. Ignore the declaration while keeping resolution disabled. Internal
            // entity references consequently remain undefined and are rejected below.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 100 * 1024 * 1024
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool TryReadCoberturaTotals(XElement root, out long covered, out long valid)
    {
        valid = 0;
        return long.TryParse(root.Attribute("lines-covered")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out covered)
            && long.TryParse(root.Attribute("lines-valid")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out valid)
            && valid > 0 && covered >= 0 && covered <= valid;
    }

    private static bool TryReadJacocoLineTotals(XElement root, out long covered, out long valid)
    {
        covered = 0;
        valid = 0;
        var lineCounter = root.Elements().FirstOrDefault(element => element.Name.LocalName == "counter"
            && string.Equals(element.Attribute("type")?.Value, "LINE", StringComparison.OrdinalIgnoreCase));
        if (lineCounter is null
            || !long.TryParse(lineCounter.Attribute("covered")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out covered)
            || !long.TryParse(lineCounter.Attribute("missed")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var missed)
            || covered < 0 || missed < 0 || covered + missed <= 0) return false;
        valid = covered + missed;
        return true;
    }

    private static string ConvertJacocoToCobertura(XElement root, long covered, long valid)
    {
        var packages = new XElement("packages", root.Elements().Where(element => element.Name.LocalName == "package")
            .Select(ToCoberturaPackage));
        var normalized = new XDocument(new XElement("coverage",
            new XAttribute("lines-covered", covered),
            new XAttribute("lines-valid", valid),
            new XAttribute("line-rate", ((double)covered / valid).ToString("0.########", CultureInfo.InvariantCulture)),
            packages));
        return normalized.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement ToCoberturaPackage(XElement package) =>
        new("package",
            new XAttribute("name", package.Attribute("name")?.Value ?? string.Empty),
            new XElement("classes", package.Elements().Where(element => element.Name.LocalName == "sourcefile")
                .Select(source => ToCoberturaClass(package, source))));

    private static XElement ToCoberturaClass(XElement package, XElement source) =>
        new("class",
            new XAttribute("name", source.Attribute("name")?.Value ?? string.Empty),
            new XAttribute("filename", JoinPath(package.Attribute("name")?.Value, source.Attribute("name")?.Value)),
            new XElement("lines", source.Elements().Where(element => element.Name.LocalName == "line")
                .Select(line => new XElement("line",
                    new XAttribute("number", line.Attribute("nr")?.Value ?? "0"),
                    new XAttribute("hits", HasCoveredInstructions(line) ? "1" : "0")))));

    private static bool HasCoveredInstructions(XElement line) =>
        int.TryParse(line.Attribute("ci")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var covered)
        && covered > 0;

    private static string JoinPath(string? package, string? source) =>
        string.IsNullOrWhiteSpace(package) ? source ?? string.Empty : $"{package}/{source}";

    private static string? Value(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
