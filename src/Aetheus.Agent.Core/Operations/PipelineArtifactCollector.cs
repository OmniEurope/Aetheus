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
            var (matchingFiles, patternSizes) = DiscoverFiles(baseDirectory, patterns);
            discovery.Stop();
            var incomplete = DescribeIncompleteCollection(baseDirectory, patterns, matchingFiles, patternSizes);
            if (incomplete is not null)
            {
                await onOutput(incomplete, TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(-1, false);
            }

            var sourceBytes = SumFileLengths(matchingFiles);
            await using var transferLease = await ArtifactTransferBudget.Shared.AcquireAsync(sourceBytes, ct)
                .ConfigureAwait(false);
            _ = ResolveCompressionLevel(compressionText);
            var compression = Stopwatch.StartNew();
            var compressionLevels = CreateZip(temporaryZip, baseDirectory, matchingFiles, compressionText);
            compression.Stop();

            var fileInfo = new FileInfo(temporaryZip);
            var hash = Stopwatch.StartNew();
            var zipHash = await HashFileAsync(temporaryZip, ct).ConfigureAwait(false);
            hash.Stop();
            await onOutput(
                $"Artifact zip created: {fileInfo.Length / 1024} Ko from {sourceBytes} bytes " +
                $"({matchingFiles.Count} files, compression={DescribeCompression(compressionText, compressionLevels)}).",
                TaskLogLevel.Info).ConfigureAwait(false);
            await EmitCollectionMetricsAsync(
                onOutput,
                new CollectionTimings(discovery.Elapsed, compression.Elapsed, hash.Elapsed),
                sourceBytes,
                fileInfo.Length,
                patternSizes).ConfigureAwait(false);
            await using var stream = new FileStream(temporaryZip, FileMode.Open, FileAccess.Read);
            var upload = Stopwatch.StartNew();
            var artifact = await apiClient.UploadArtifactAsync(runId, artifactName, stageName, stream, ct)
                .ConfigureAwait(false);
            upload.Stop();
            var zipSha256 = Convert.ToHexStringLower(zipHash);
            EnsureUploadMatches(artifact, fileInfo.Length, zipSha256);
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

    private readonly record struct PatternSize(int Index, string Pattern, long Bytes, int Files);

    private readonly record struct CollectionTimings(TimeSpan Discovery, TimeSpan Compression, TimeSpan Hash);

    /// <summary>The files to archive (the whole workspace when no pattern is set) and, for each
    /// pattern, what it matched on its own.</summary>
    private static (List<string> Files, List<PatternSize> PatternSizes) DiscoverFiles(
        string baseDirectory, IReadOnlyList<string>? patterns)
    {
        var patternSizes = new List<PatternSize>();
        if (patterns is null or { Count: 0 })
        {
            var workspaceFiles = ExistingDistinct(Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories));
            patternSizes.Add(new PatternSize(0, "<workspace>", SumFileLengths(workspaceFiles), workspaceFiles.Count));
            return (workspaceFiles, patternSizes);
        }

        for (var index = 0; index < patterns.Count; index++)
        {
            var patternFiles = ExistingDistinct(WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, patterns[index]));
            patternSizes.Add(new PatternSize(index, patterns[index], SumFileLengths(patternFiles), patternFiles.Count));
        }
        var matchingFiles = ExistingDistinct(patterns
            .SelectMany(pattern => WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, pattern)));
        return (matchingFiles, patternSizes);
    }

    /// <summary>Why the collected files are not the artifact the step declared, or <c>null</c> when they
    /// are. An empty archive is not an artifact: with or without patterns, nothing collected is a
    /// failure. And every declared path is part of the artifact: one that matches nothing makes the
    /// publication incomplete, so it fails instead of uploading the rest as a green step (mirror CI
    /// run 1272 published ApplicationPayload-artifacts without its three image archives).</summary>
    private static string? DescribeIncompleteCollection(
        string baseDirectory, IReadOnlyList<string>? patterns, List<string> matchingFiles,
        List<PatternSize> patternSizes)
    {
        if (patterns is null or { Count: 0 })
            return matchingFiles.Count == 0
                ? $"Artifact collection failed: the workspace {baseDirectory} holds no file to collect"
                : null;
        if (matchingFiles.Count == 0)
            return "Artifact collection failed: no files matched the configured patterns";
        var unmatched = patternSizes.Where(item => item.Files == 0).Select(item => item.Pattern).ToList();
        return unmatched.Count == 0
            ? null
            : $"Artifact collection failed: {unmatched.Count} declared path(s) matched no file in {baseDirectory}: " +
              string.Join(", ", unmatched);
    }

    private static List<string> ExistingDistinct(IEnumerable<string> files) =>
        files.Where(File.Exists).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Writes the archive and counts the entries stored at each compression level.</summary>
    private static Dictionary<CompressionLevel, int> CreateZip(
        string zipPath, string baseDirectory, List<string> files, string? compressionText)
    {
        var compressionLevels = new Dictionary<CompressionLevel, int>();
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entryName = Path.GetRelativePath(baseDirectory, file);
            var compressionLevel = ResolveCompressionLevel(compressionText, file);
            archive.CreateEntryFromFile(file, entryName, compressionLevel);
            compressionLevels[compressionLevel] = compressionLevels.GetValueOrDefault(compressionLevel) + 1;
        }
        return compressionLevels;
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var hashStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return await SHA256.HashDataAsync(hashStream, ct).ConfigureAwait(false);
    }

    private static string DescribeCompression(string? compressionText, Dictionary<CompressionLevel, int> compressionLevels) =>
        string.Equals(compressionText?.Trim(), "adaptive", StringComparison.OrdinalIgnoreCase)
            ? "Adaptive [" + string.Join(", ", compressionLevels
                .OrderBy(item => item.Key)
                .Select(item => $"{item.Key}={item.Value}")) + "]"
            : ResolveCompressionLevel(compressionText).ToString();

    private static async Task EmitCollectionMetricsAsync(
        Func<string, TaskLogLevel, Task> onOutput,
        CollectionTimings timings,
        long sourceBytes,
        long compressedBytes,
        List<PatternSize> patternSizes)
    {
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.collect.discovery", "Duration", "s", timings.Discovery.TotalSeconds).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.collect.compression", "Duration", "s", timings.Compression.TotalSeconds).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.collect.hash", "Duration", "s", timings.Hash.TotalSeconds).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.bytes.source", "Size", "bytes", sourceBytes).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.bytes.compressed", "Size", "bytes", compressedBytes).ConfigureAwait(false);
        foreach (var item in patternSizes)
        {
            await onOutput(
                $"Artifact pattern #{item.Index}: files={item.Files}, bytes={item.Bytes}, pattern={item.Pattern}",
                TaskLogLevel.Info).ConfigureAwait(false);
            await PipelineMetricEmitter.EmitAsync(
                onOutput, $"artifact.pattern.{item.Index}.bytes", "Size", "bytes", item.Bytes).ConfigureAwait(false);
        }
    }

    private static void EnsureUploadMatches(PipelineArtifactDto? artifact, long zipLength, string zipSha256)
    {
        if (artifact is null
            || artifact.SizeBytes != zipLength
            || !string.Equals(artifact.Sha256, zipSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The uploaded artifact size or SHA-256 does not match the collected zip.");
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
