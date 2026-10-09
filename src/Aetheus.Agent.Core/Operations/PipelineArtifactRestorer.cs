// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Downloads and safely restores a verified pipeline artifact into the agent workspace.
/// </summary>
internal static class PipelineArtifactRestorer
{
    private const long SafetyFloorBytes = 512L * 1024 * 1024;

    public static async Task<ExecutorResult> RestoreAsync(
        IServerApiClient apiClient, ILogger logger, IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct,
        Func<string, long>? availableSpaceProvider = null)
    {
        if (!TryReadRequest(envVars, out var request))
        {
            await onOutput(
                "Missing or invalid AETHEUS_RESTORE_ARTIFACT_ID, AETHEUS_RESTORE_RUN_ID, " +
                "AETHEUS_RESTORE_ARTIFACT_SHA256 or AETHEUS_WORKING_DIR",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        try
        {
            return await RestoreVerifiedAsync(
                apiClient, request, onOutput, ct, availableSpaceProvider).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or ArgumentException or NotSupportedException or JsonException)
        {
            logger.LogError(ex, "Failed to restore artifact {ArtifactId} for run {RunId}", request.ArtifactId, request.RunId);
            await onOutput($"Artifact restore failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
    }

    private static bool TryReadRequest(
        IReadOnlyDictionary<string, string> envVars,
        out ArtifactRestoreRequest request)
    {
        envVars.TryGetValue("AETHEUS_RESTORE_ARTIFACT_ID", out var artifactIdText);
        envVars.TryGetValue("AETHEUS_RESTORE_RUN_ID", out var runIdText);
        envVars.TryGetValue("AETHEUS_RESTORE_ARTIFACT_SHA256", out var expectedSha256);
        envVars.TryGetValue("AETHEUS_RESTORE_ARTIFACT_SIZE_BYTES", out var expectedSizeText);
        envVars.TryGetValue("AETHEUS_WORKING_DIR", out var workingDir);
        envVars.TryGetValue("AETHEUS_RESTORE_TARGET_DIR", out var targetDirectory);
        envVars.TryGetValue("AETHEUS_RESTORE_RELEASE_SELECTOR", out var releaseSelector);
        envVars.TryGetValue("AETHEUS_RESTORE_EXPECTED_SOURCE_COMMIT", out var expectedSourceCommit);
        var artifactIdValid = int.TryParse(artifactIdText, out var artifactId) && artifactId > 0;
        var runIdValid = int.TryParse(runIdText, out var runId) && runId > 0;
        var valid = artifactIdValid
                    && runIdValid
                    && !string.IsNullOrWhiteSpace(workingDir)
                    && expectedSha256 is { Length: 64 }
                    && expectedSha256.All(Uri.IsHexDigit);
        var expectedSize = long.TryParse(expectedSizeText, out var parsedSize) && parsedSize >= 0
            ? parsedSize
            : 0;
        request = new ArtifactRestoreRequest(
            artifactId, runId, expectedSha256 ?? string.Empty, expectedSize,
            workingDir ?? string.Empty, targetDirectory, releaseSelector, expectedSourceCommit);
        return valid;
    }

    private static async Task<ExecutorResult> RestoreVerifiedAsync(
        IServerApiClient apiClient,
        ArtifactRestoreRequest request,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct,
        Func<string, long>? availableSpaceProvider)
    {
        await using var transferLease = await ArtifactTransferBudget.Shared.AcquireAsync(request.ExpectedSize, ct)
            .ConfigureAwait(false);
        var restoreDirectory = ResolveRestoreDirectory(request.WorkingDirectory, request.TargetDirectory);
        Directory.CreateDirectory(restoreDirectory);
        await onOutput($"Downloading verified artifact {request.ArtifactId} into {restoreDirectory}…", TaskLogLevel.Info).ConfigureAwait(false);
        var download = Stopwatch.StartNew();
        await using var zip = await apiClient.DownloadArtifactAsync(request.ArtifactId, request.RunId, ct).ConfigureAwait(false);
        download.Stop();
        if (zip is null)
        {
            await onOutput("Artifact download failed (not found / forbidden)", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var hash = await ReadHashAsync(zip, ct).ConfigureAwait(false);
        if (!hash.Sha256.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            await onOutput(
                $"Artifact SHA-256 mismatch: expected {request.ExpectedSha256}, received {hash.Sha256.ToLowerInvariant()}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var requiredBytes = DeployLayout.ValidateArchive(archive, restoreDirectory);
        var root = Path.GetPathRoot(Path.GetFullPath(request.WorkingDirectory));
        var availableBytes = availableSpaceProvider?.Invoke(root!) ?? new DriveInfo(root!).AvailableFreeSpace;
        if (!HasEnoughSpace(requiredBytes, availableBytes))
        {
            await onOutput(
                $"Artifact restore refused: {requiredBytes} bytes to unpack with only {availableBytes} bytes free (512 MiB safety floor).",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        // Provenance is read from this archive's own entries, before anything is extracted: markers
        // already present in the workspace (an earlier restore, a checkout) prove nothing about it.
        if (VerifyProvenance(archive, request.ExpectedSourceCommit) is { } provenanceFailure)
        {
            await onOutput(provenanceFailure, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var extraction = Stopwatch.StartNew();
        DeployLayout.ExtractSafely(archive, restoreDirectory);
        extraction.Stop();

        var downloadDuration = ArtifactDownloadTelemetry.TryGet(zip, out var measured)
            ? measured.DownloadDuration
            : download.Elapsed;
        await EmitRestoreMetricsAsync(
            onOutput, downloadDuration, hash, extraction.Elapsed, requiredBytes).ConfigureAwait(false);
        await onOutput($"Restored {archive.Entries.Count} artifact entries into {restoreDirectory}", TaskLogLevel.Info).ConfigureAwait(false);
        if (IsDeployedReleaseSelector(request.ReleaseSelector))
            await ExportDeployedBaselineAsync(
                restoreDirectory, request.ReleaseSelector!, onOutput, ct).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    /// <summary>
    /// D-04: proves the restored artifact was built from the revision this run was launched on.
    ///
    /// The archive's SHA-256 above proves it arrived intact; it says nothing about WHICH build it is.
    /// Without this, a consumer restores a previous run's artifacts and then scans, grades or ships a
    /// revision it never looked at, which is exactly the class of defect the sealed delivery contract
    /// exists to prevent. Every consumer used to rewrite the comparison in shell afterwards, so a
    /// consumer that forgot the stage got no warning at all.
    ///
    /// Returns null when there is nothing to check (a release selector deliberately restores another
    /// commit) or when the artifact checks out; otherwise the reason to refuse it.
    /// </summary>
    private static string? VerifyProvenance(ZipArchive archive, string? expectedSourceCommit)
    {
        if (string.IsNullOrWhiteSpace(expectedSourceCommit)) return null;

        var commitEntry = FindProvenanceEntry(archive, "source-commit");
        if (commitEntry is null)
            return "Artifact provenance refused: the restored artifact carries no "
                + ".pipeline-artifacts/source-commit, so the revision it was built from cannot be proved.";

        // Bounded on purpose: this file holds one commit id, and anything else is not evidence.
        if (commitEntry.Length is <= 0 or > 256)
            return "Artifact provenance refused: .pipeline-artifacts/source-commit is empty or "
                + "implausibly large.";

        string actual;
        using (var reader = new StreamReader(commitEntry.Open()))
            actual = reader.ReadToEnd().Trim();
        if (!string.Equals(actual, expectedSourceCommit.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"Artifact provenance refused: built from {actual}, but this run was launched on "
                + $"{expectedSourceCommit}.";

        foreach (var required in RequiredProvenanceFiles)
        {
            if (FindProvenanceEntry(archive, required) is not { Length: > 0 })
                return $"Artifact provenance refused: .pipeline-artifacts/{required} is missing or empty.";
        }

        return null;
    }

    // The last entry of a name is the one extraction leaves on disk.
    private static ZipArchiveEntry? FindProvenanceEntry(ZipArchive archive, string name) =>
        archive.Entries.LastOrDefault(entry => string.Equals(
            entry.FullName.Replace('\\', '/'), $".pipeline-artifacts/{name}", StringComparison.Ordinal));

    /// <summary>The delivery contract and the provenance manifest travel with every artifact built by
    /// the pipelines; an artifact missing either is not one a consumer can attest anything about.</summary>
    private static readonly string[] RequiredProvenanceFiles =
        ["delivery-contract.json", "artifact-provenance.json"];

    private static bool IsDeployedReleaseSelector(string? selector) =>
        string.Equals(selector, "previous-deployed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(selector, "current-deployed", StringComparison.OrdinalIgnoreCase);

    private static async Task<ArtifactHashResult> ReadHashAsync(Stream zip, CancellationToken ct)
    {
        if (ArtifactDownloadTelemetry.TryGet(zip, out var transfer))
            return new ArtifactHashResult(transfer.Sha256, transfer.HashDuration, transfer.Bytes);
        var timer = Stopwatch.StartNew();
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(zip, ct).ConfigureAwait(false));
        timer.Stop();
        var bytes = zip.CanSeek ? zip.Length : 0;
        zip.Position = 0;
        return new ArtifactHashResult(sha256, timer.Elapsed, bytes);
    }

    private static async Task EmitRestoreMetricsAsync(
        Func<string, TaskLogLevel, Task> onOutput,
        TimeSpan downloadDuration,
        ArtifactHashResult hash,
        TimeSpan extractionDuration,
        long extractedBytes)
    {
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.restore.download", "Duration", "s", downloadDuration.TotalSeconds).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.restore.hash", "Duration", "s", hash.Duration.TotalSeconds).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.restore.extraction", "Duration", "s", extractionDuration.TotalSeconds).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.restore.bytes.downloaded", "Size", "bytes", hash.Bytes).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "artifact.restore.bytes.extracted", "Size", "bytes", extractedBytes).ConfigureAwait(false);
    }

    private sealed record ArtifactRestoreRequest(
        int ArtifactId,
        int RunId,
        string ExpectedSha256,
        long ExpectedSize,
        string WorkingDirectory,
        string? TargetDirectory,
        string? ReleaseSelector,
        string? ExpectedSourceCommit);

    private sealed record ArtifactHashResult(string Sha256, TimeSpan Duration, long Bytes);

    private static async Task ExportDeployedBaselineAsync(
        string restoreDirectory,
        string releaseSelector,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var contractPath = Path.Combine(restoreDirectory, ".pipeline-artifacts", "delivery-contract.json");
        if (!File.Exists(contractPath))
            throw new InvalidDataException(
                $"The {releaseSelector} artifact does not contain .pipeline-artifacts/delivery-contract.json.");

        var bytes = await File.ReadAllBytesAsync(contractPath, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("candidateVersion", out var candidateVersionElement)
            || candidateVersionElement.ValueKind != JsonValueKind.String
            || !document.RootElement.TryGetProperty("sourceSha", out var sourceShaElement)
            || sourceShaElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The {releaseSelector} delivery contract has no valid candidateVersion or sourceSha.");
        }

        var candidateVersion = candidateVersionElement.GetString()?.Trim();
        var sourceSha = sourceShaElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(candidateVersion)
            || candidateVersion.Length > 256
            || candidateVersion.IndexOfAny(['\r', '\n']) >= 0
            || sourceSha is not { Length: 40 }
            || sourceSha.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"The {releaseSelector} delivery contract has no valid candidateVersion or sourceSha.");
        }

        var contractSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await onOutput(
            "##aetheus[setvariable name=DELIVERY_BASELINE_BOOTSTRAP]false",
            TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput(
            $"##aetheus[setvariable name=DELIVERY_BASELINE_CANDIDATE_VERSION]{candidateVersion}",
            TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput(
            $"##aetheus[setvariable name=DELIVERY_BASELINE_SOURCE_SHA]{sourceSha.ToLowerInvariant()}",
            TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput(
            $"##aetheus[setvariable name=DELIVERY_BASELINE_CONTRACT_SHA256]{contractSha256}",
            TaskLogLevel.Info).ConfigureAwait(false);
    }

    internal static bool HasEnoughSpace(long requiredBytes, long availableBytes) =>
        availableBytes >= SafetyFloorBytes && requiredBytes <= availableBytes - SafetyFloorBytes;

    internal static string ResolveRestoreDirectory(string workspace, string? targetDirectory)
    {
        var root = Path.GetFullPath(workspace);
        if (string.IsNullOrWhiteSpace(targetDirectory)) return root;
        if (Path.IsPathRooted(targetDirectory))
            throw new InvalidDataException("Artifact target directory must be relative to the workspace.");

        var destination = Path.GetFullPath(Path.Combine(root, targetDirectory));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact target directory escapes the workspace.");
        return destination;
    }
}
