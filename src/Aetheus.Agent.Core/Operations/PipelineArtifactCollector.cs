// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Aetheus.Agent.Core.Operations;

internal static class PipelineArtifactCollector
{
    private static readonly HashSet<string> AlreadyCompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avif", ".br", ".bz2", ".gif", ".gz", ".jpeg", ".jpg", ".mp3", ".mp4",
        ".nupkg", ".pdf", ".png", ".snupkg", ".tgz", ".webp", ".woff", ".woff2",
        ".xz", ".zip"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".css", ".csv", ".html", ".js", ".json", ".md", ".mjs", ".props", ".razor",
        ".sarif", ".targets", ".tsv", ".txt", ".xml", ".yaml", ".yml"
    };

    public static async Task<ExecutorResult> CollectAsync(
        IServerApiClient apiClient, ILogger logger, string target,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        envVars.TryGetValue("AETHEUS_ARTIFACT_NAME", out var artifactName);
        envVars.TryGetValue("AETHEUS_RUN_ID", out var runIdText);
        envVars.TryGetValue("AETHEUS_STAGE_NAME", out var stageName);
        envVars.TryGetValue("AETHEUS_WORKING_DIR", out var workingDirectory);
        envVars.TryGetValue("AETHEUS_ARTIFACT_COMPRESSION", out var compressionText);
        if (!int.TryParse(runIdText, out var runId))
        {
            await onOutput("Missing AETHEUS_RUN_ID", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        artifactName ??= "build-artifact";
        var baseDirectory = !string.IsNullOrEmpty(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory();
        var patterns = PipelineTargetPatterns.ParseOptional(target);

        await onOutput($"Collecting artifacts from {baseDirectory}...", TaskLogLevel.Info).ConfigureAwait(false);
        var temporaryZip = Path.Combine(Path.GetTempPath(), $"aetheus-artifact-{Guid.NewGuid():N}.zip");
        try
        {
            var discovery = Stopwatch.StartNew();
            List<string> matchingFiles;
            var patternSizes = new List<(int Index, string Pattern, long Bytes, int Files)>();
            if (patterns is null or { Count: 0 })
            {
                matchingFiles = Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
                    .Where(File.Exists)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                patternSizes.Add((0, "<workspace>", SumFileLengths(matchingFiles), matchingFiles.Count));
            }
            else
            {
                for (var index = 0; index < patterns.Count; index++)
                {
                    var patternFiles = WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, patterns[index])
                        .Where(File.Exists)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    patternSizes.Add((index, patterns[index], SumFileLengths(patternFiles), patternFiles.Count));
                }
                matchingFiles = patterns
                    .SelectMany(pattern => WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, pattern))
                    .Where(File.Exists)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            discovery.Stop();
            // An empty archive is not an artifact: with or without patterns, nothing collected is a
            // failure, never a green step that uploaded an empty zip.
            if (matchingFiles.Count == 0)
            {
                await onOutput(
                    patterns is null or { Count: 0 }
                        ? $"Artifact collection failed: the workspace {baseDirectory} holds no file to collect"
                        : "Artifact collection failed: no files matched the configured patterns",
                    TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(-1, false);
            }

            var sourceBytes = SumFileLengths(matchingFiles);
            await using var transferLease = await ArtifactTransferBudget.Shared.AcquireAsync(sourceBytes, ct)
                .ConfigureAwait(false);
            _ = ResolveCompressionLevel(compressionText);
            var compressionLevels = new Dictionary<CompressionLevel, int>();
            var compression = Stopwatch.StartNew();
            using (var archive = ZipFile.Open(temporaryZip, ZipArchiveMode.Create))
            {
                foreach (var file in matchingFiles)
                {
                    var entryName = Path.GetRelativePath(baseDirectory, file);
                    var compressionLevel = ResolveCompressionLevel(compressionText, file);
                    archive.CreateEntryFromFile(file, entryName, compressionLevel);
                    compressionLevels[compressionLevel] = compressionLevels.GetValueOrDefault(compressionLevel) + 1;
                }
            }
            compression.Stop();

            var fileInfo = new FileInfo(temporaryZip);
            var hash = Stopwatch.StartNew();
            byte[] zipHash;
            await using (var hashStream = new FileStream(
                             temporaryZip, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                zipHash = await SHA256.HashDataAsync(hashStream, ct).ConfigureAwait(false);
            hash.Stop();
            var compressionSummary = string.Equals(compressionText?.Trim(), "adaptive", StringComparison.OrdinalIgnoreCase)
                ? "Adaptive [" + string.Join(", ", compressionLevels
                    .OrderBy(item => item.Key)
                    .Select(item => $"{item.Key}={item.Value}")) + "]"
                : ResolveCompressionLevel(compressionText).ToString();
            await onOutput(
                $"Artifact zip created: {fileInfo.Length / 1024}KB from {sourceBytes} bytes " +
                $"({matchingFiles.Count} files, compression={compressionSummary}).",
                TaskLogLevel.Info).ConfigureAwait(false);
            await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.collect.discovery", "Duration", "s", discovery.Elapsed.TotalSeconds).ConfigureAwait(false);
            await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.collect.compression", "Duration", "s", compression.Elapsed.TotalSeconds).ConfigureAwait(false);
            await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.collect.hash", "Duration", "s", hash.Elapsed.TotalSeconds).ConfigureAwait(false);
            await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.bytes.source", "Size", "bytes", sourceBytes).ConfigureAwait(false);
            await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.bytes.compressed", "Size", "bytes", fileInfo.Length).ConfigureAwait(false);
            foreach (var item in patternSizes)
            {
                await onOutput(
                    $"Artifact pattern #{item.Index}: files={item.Files}, bytes={item.Bytes}, pattern={item.Pattern}",
                    TaskLogLevel.Info).ConfigureAwait(false);
                await PipelineMetricEmitter.EmitAsync(
                    onOutput, $"artifact.pattern.{item.Index}.bytes", "Size", "bytes", item.Bytes).ConfigureAwait(false);
            }
            await using var stream = new FileStream(temporaryZip, FileMode.Open, FileAccess.Read);
            var upload = Stopwatch.StartNew();
            var artifact = await apiClient.UploadArtifactAsync(runId, artifactName, stageName, stream, ct)
                .ConfigureAwait(false);
            upload.Stop();
            var zipSha256 = Convert.ToHexStringLower(zipHash);
            if (artifact is null
                || artifact.SizeBytes != fileInfo.Length
                || !string.Equals(artifact.Sha256, zipSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The uploaded artifact size or SHA-256 does not match the collected zip.");
            await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.collect.upload", "Duration", "s", upload.Elapsed.TotalSeconds).ConfigureAwait(false);
            await onOutput(
                $"Artifact uploaded successfully: {GetUploadPartCount(fileInfo.Length)} part(s), " +
                $"sha256 {zipSha256}.",
                TaskLogLevel.Info).ConfigureAwait(false);
            await TryAutoPublishCoverageAsync(apiClient, logger, runId, stageName, baseDirectory, onOutput, ct).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to collect/upload artifacts for run {RunId}", runId);
            await onOutput($"Artifact collection failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
        }
    }

    internal static CompressionLevel ResolveCompressionLevel(string? value, string? filePath = null) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "fastest" => CompressionLevel.Fastest,
            "optimal" => CompressionLevel.Optimal,
            "none" or "nocompression" => CompressionLevel.NoCompression,
            "adaptive" => ResolveAdaptiveCompressionLevel(filePath),
            _ => throw new InvalidDataException(
                "AETHEUS_ARTIFACT_COMPRESSION must be Adaptive, Fastest, Optimal or NoCompression.")
        };

    internal static long GetUploadPartCount(long length) =>
        length > ArtifactChunkPlan.ChunkThresholdBytes
            ? 1 + (length - 1) / ArtifactChunkPlan.ChunkSizeBytes
            : 1;

    private static CompressionLevel ResolveAdaptiveCompressionLevel(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return CompressionLevel.Fastest;
        var extension = Path.GetExtension(filePath);
        if (AlreadyCompressedExtensions.Contains(extension)) return CompressionLevel.NoCompression;
        return TextExtensions.Contains(extension) ? CompressionLevel.Optimal : CompressionLevel.Fastest;
    }

    private static long SumFileLengths(IEnumerable<string> files) =>
        files.Aggregate(0L, (total, file) => checked(total + new FileInfo(file).Length));

    private static async Task TryAutoPublishCoverageAsync(
        IServerApiClient apiClient, ILogger logger, int runId, string? stageName, string baseDirectory,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        try
        {
            var mergedFiles = WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, "**/Cobertura.xml")
                .Distinct(StringComparer.Ordinal).ToList();
            if (mergedFiles.Count > 1)
            {
                await onOutput("Coverage auto-publish skipped: multiple merged Cobertura reports found.", TaskLogLevel.Warning).ConfigureAwait(false);
                return;
            }

            var coverageFile = mergedFiles.SingleOrDefault();
            if (coverageFile is null)
            {
                var rawFiles = WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, "**/*.cobertura.xml")
                    .Concat(WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, "**/coverage.xml"))
                    .Distinct(StringComparer.Ordinal).ToList();
                if (rawFiles.Count > 1)
                {
                    await onOutput("Coverage auto-publish skipped: multiple raw reports require an explicit merged report.", TaskLogLevel.Warning).ConfigureAwait(false);
                    return;
                }
                coverageFile = rawFiles.SingleOrDefault();
            }
            if (coverageFile is null || !File.Exists(coverageFile)) return;

            var xml = await File.ReadAllTextAsync(coverageFile, ct).ConfigureAwait(false);
            var relativePath = Path.GetRelativePath(baseDirectory, coverageFile);
            await apiClient.PublishCoverageAsync(runId, xml, stageName, relativePath, ct).ConfigureAwait(false);
            await onOutput($"Coverage auto-detected and published from {relativePath}", TaskLogLevel.Info).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto coverage publish skipped for run {RunId}", runId);
        }
    }
}
