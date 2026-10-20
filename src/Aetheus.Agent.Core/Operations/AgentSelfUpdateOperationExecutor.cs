// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// F-32 typed operation: <see cref="OperationKind.AgentSelfUpdate"/>.
///
/// The agent cannot overwrite its own running binaries, so the update is a two-phase handoff:
///  1. Download the latest agent archive from the backend's anonymous <c>/downloads</c> endpoint
///     (URL derived from the agent's own configured <c>ServerUrl</c>) and unpack it to a staging
///     directory inside the work dir.
///  2. Write a small platform updater script and launch it <em>detached</em> so it outlives this
///     process. After the task result reaches the backend, the polling service exits with a
///     failure code. The updater waits for the agent process to exit, swaps
///     the binaries in the install directory (preserving <c>appsettings.json</c> and the work dir),
///     and the service manager's auto-restart policy (systemd <c>Restart=always</c> /
///     Windows SCM failure-restart) brings the new build back up. The freshly started agent
///     re-enrolls from its persisted credentials and reports the new version on its next heartbeat.
///
/// No privilege escalation is required: the agent already has write access to its install
/// directory (Linux systemd unit grants <c>ReadWritePaths=$INSTALL_DIR</c>; the Windows service
/// account owns its program directory) and the updater never calls into <c>systemctl</c>/<c>sc</c>.
/// </summary>
public sealed class AgentSelfUpdateOperationExecutor(
    IHttpClientFactory httpClientFactory,
    IOptions<AetheusAgentOptions> options,
    IServerApiClient serverApi,
    AgentState agentState,
    ILogger<AgentSelfUpdateOperationExecutor> logger) : IOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;
    private readonly AgentReleaseArchiveResolver _releaseResolver =
        new(httpClientFactory, options.Value);

    private const string LinuxArchiveName = "aetheus-agent-linux-x64.tar.gz";
    private const string WindowsArchiveName = "aetheus-agent-win-x64.zip";
    internal const long MaxArchiveBytes = 256L * 1024 * 1024;
    internal const long MaxExtractedPayloadBytes = 1024L * 1024 * 1024;
    internal const int MaxArchiveEntries = 4096;
    internal const int ConfirmationTimeoutSeconds = 330;
    // Posture 3: the supervisor understands the sixth request field (capabilities to enable). This agent
    // refuses to hand a request to a supervisor of another version, so a six-field line never reaches a
    // supervisor that would reject it.
    internal const string LinuxIntegrationPostureVersion = "3";
    internal const string LinuxIntegrationPostureVersionPath = "/etc/aetheus-agent-posture-version";
    internal bool? IsWindowsOverride { get; set; }
    internal string? InstallDirectoryOverride { get; set; }
    internal string? LinuxPostureVersionPathOverride { get; set; }
    internal string? LinuxUpdateRequestPathOverride { get; set; }
    internal Action<string, IReadOnlyList<string>>? DetachedLaunchOverride { get; set; }

    public bool CanHandle(OperationKind kind) => kind is OperationKind.AgentSelfUpdate;

    /// <summary>
    /// Fire-and-forget phase emitter. Reports to the backend so UI clients on the server group
    /// can render the progress bar (item #3 of the plan). Failures are swallowed inside
    /// <see cref="IServerApiClient.ReportUpdateProgressAsync"/> - a self-update must never abort
    /// because a telemetry beat couldn't go through.
    /// </summary>
    private async Task ReportPhaseAsync(AgentUpdatePhase phase, int percent, string? message, CancellationToken ct)
    {
        if (agentState.ServerId is not { } serverId) return; // not enrolled yet (defensive)
        await serverApi.ReportUpdateProgressAsync(
            serverId,
            new AgentUpdateProgressReport { Phase = phase, Percent = percent, Message = message },
            ct).ConfigureAwait(false);
    }

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(kind, target, null, timeoutSeconds, onOutput, cancellationToken);

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(kind, target, envVars, timeoutSeconds, onOutput, cancellationToken);

    private async Task<ExecutorResult> ExecuteCoreAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string>? envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (kind != OperationKind.AgentSelfUpdate)
            return new ExecutorResult(-1, false);

        var isWindows = IsWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var installDir = (InstallDirectoryOverride ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stagingRoot = Path.Combine(_options.WorkDirectory, ".agent-update");
        try
        {
            if (AgentSelfUpdateFileSystem.IsSameOrChildPath(_options.WorkDirectory, installDir))
            {
                await onOutput(
                    "Self-update refused: WorkDirectory must be outside the agent installation directory.",
                    TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(-1, false);
            }

            await ReportPhaseAsync(AgentUpdatePhase.PickedUp, 10, null, cancellationToken).ConfigureAwait(false);
            await onOutput($"Starting agent self-update (install dir: {installDir})", TaskLogLevel.Info).ConfigureAwait(false);
            // Refuse a capability request before anything is downloaded or touched.
            var capabilities = await ReadCapabilityRequestAsync(envVars, isWindows, onOutput, cancellationToken)
                .ConfigureAwait(false);
            if (capabilities is null) return new ExecutorResult(-1, false);
            var preparation = await PrepareUpdateAsync(
                stagingRoot, envVars, isWindows, onOutput, cancellationToken).ConfigureAwait(false);
            if (preparation.Context is null) return preparation.Failure!;
            return await HandoffUpdateAsync(
                preparation.Context, stagingRoot, installDir, isWindows, capabilities, onOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AgentSelfUpdateLauncher.SafeOutputAsync(
                onOutput, "Self-update cancelled.", TaskLogLevel.Warning).ConfigureAwait(false);
            return new ExecutorResult(-1, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent self-update failed");
            // Best-effort failure beat. The CancellationToken may already be tripped if the
            // exception came from a downstream operation; pass CancellationToken.None so the
            // beat can still go through during teardown.
            await SafeReportAsync(AgentUpdatePhase.Failed, 0, ex.Message).ConfigureAwait(false);
            await AgentSelfUpdateLauncher.SafeOutputAsync(
                onOutput, $"Self-update failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
    }

    private async Task<SelfUpdatePreparationResult> PrepareUpdateAsync(
        string stagingRoot,
        IReadOnlyDictionary<string, string>? envVars,
        bool isWindows,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        AgentSelfUpdateFileSystem.PrepareCleanDirectory(stagingRoot);
        var release = await _releaseResolver.ResolveAsync(envVars, isWindows, onOutput, ct).ConfigureAwait(false);
        if (release is null && envVars?.ContainsKey("AETHEUS_AGENT_TARGET_VERSION") == true)
        {
            await ReportPhaseAsync(AgentUpdatePhase.Failed, 20, "release manifest invalid", ct).ConfigureAwait(false);
            return SelfUpdatePreparationResult.Failed(new ExecutorResult(-1, false));
        }
        if (!isWindows && release is null)
        {
            await ReportPhaseAsync(AgentUpdatePhase.Failed, 20, "qualified release manifest required", ct).ConfigureAwait(false);
            await onOutput("Linux self-update requires a versioned release manifest.", TaskLogLevel.Error).ConfigureAwait(false);
            return SelfUpdatePreparationResult.Failed(new ExecutorResult(-1, false));
        }

        var archiveName = release?.Archive.FileName ?? (isWindows ? WindowsArchiveName : LinuxArchiveName);
        var archivePath = Path.Combine(stagingRoot, archiveName);
        var extractDir = Path.Combine(stagingRoot, "new");
        Directory.CreateDirectory(extractDir);
        var downloadUrl = BuildDownloadUrl(release, archiveName);
        await ReportPhaseAsync(AgentUpdatePhase.Downloading, 20, downloadUrl, ct).ConfigureAwait(false);
        await onOutput($"Downloading latest agent from {downloadUrl}", TaskLogLevel.Info).ConfigureAwait(false);
        if (!await DownloadAsync(
                downloadUrl, archivePath, release?.Archive.Sha256, release?.Archive.SizeBytes, onOutput, ct)
                .ConfigureAwait(false))
        {
            await ReportPhaseAsync(AgentUpdatePhase.Failed, 20, "download failed", ct).ConfigureAwait(false);
            return SelfUpdatePreparationResult.Failed(new ExecutorResult(-1, false));
        }
        await ReportPhaseAsync(AgentUpdatePhase.Downloaded, 50, null, ct).ConfigureAwait(false);
        await ReportPhaseAsync(AgentUpdatePhase.Extracting, 60, null, ct).ConfigureAwait(false);
        await onOutput("Extracting update package…", TaskLogLevel.Info).ConfigureAwait(false);
        if (!await ExtractAsync(archivePath, extractDir, isWindows, onOutput, ct).ConfigureAwait(false))
        {
            await ReportPhaseAsync(AgentUpdatePhase.Failed, 60, "extraction failed", ct).ConfigureAwait(false);
            return SelfUpdatePreparationResult.Failed(new ExecutorResult(-1, false));
        }
        if (!StagedPayloadLooksValid(extractDir, isWindows))
        {
            await ReportPhaseAsync(AgentUpdatePhase.Failed, 70, "staged payload invalid", ct).ConfigureAwait(false);
            await onOutput("Update package does not contain the expected agent binaries - aborting.", TaskLogLevel.Error).ConfigureAwait(false);
            return SelfUpdatePreparationResult.Failed(new ExecutorResult(-1, false));
        }
        return SelfUpdatePreparationResult.Prepared(new SelfUpdateContext(release, extractDir));
    }

    private string BuildDownloadUrl(ResolvedAgentReleaseArchive? release, string archiveName) =>
        release is null
            ? $"{_options.ServerUrl.TrimEnd('/')}/downloads/{archiveName}"
            : $"{_options.ServerUrl.TrimEnd('/')}/downloads/releases/"
              + $"{Uri.EscapeDataString(release.SoftwareVersion)}/"
              + Uri.EscapeDataString(archiveName);

    private async Task<ExecutorResult> HandoffUpdateAsync(
        SelfUpdateContext context,
        string stagingRoot,
        string installDir,
        bool isWindows,
        IReadOnlyList<string> enableCapabilities,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        await ReportPhaseAsync(AgentUpdatePhase.LaunchingUpdater, 80, null, ct).ConfigureAwait(false);
        if (isWindows)
        {
            await onOutput("Launching detached updater...", TaskLogLevel.Info).ConfigureAwait(false);
            LaunchWindowsUpdater(stagingRoot, context.ExtractDirectory, installDir);
            await onOutput("Update handed off to the detached updater. The swap outcome is confirmed by the agent version on the next heartbeat.", TaskLogLevel.Warning).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }

        var requested = await LinuxAgentPostureUpgradeRequester.TryRequestAsync(
            _options.WorkDirectory,
            LinuxPostureVersionPathOverride ?? LinuxIntegrationPostureVersionPath,
            LinuxUpdateRequestPathOverride ?? Path.Combine(_options.WorkDirectory, ".agent-posture-upgrade-request"),
            LinuxIntegrationPostureVersion,
            LinuxAgentPostureUpgradeRequester.BuildQualifiedRequest(context.Release!, enableCapabilities),
            onOutput,
            ct).ConfigureAwait(false);
        if (requested) return new ExecutorResult(0, false);
        const string diagnostic = "Linux integration posture upgrade supervisor is missing or obsolete.";
        await SafeReportAsync(AgentUpdatePhase.Failed, 80, diagnostic).ConfigureAwait(false);
        return new ExecutorResult(-1, false,
            Aetheus.Shared.Components.Shared.TaskFailureCodes.InfrastructureMismatch, diagnostic);
    }

    /// <summary>
    /// The capabilities this update must also grant (R3-58, ADR-051), or null after reporting why the
    /// request is refused. The backend already validated them; the agent checks again against the same
    /// closed whitelist because the task environment is not a trust boundary of its own. Only the Linux
    /// installer has the flags the tokens map to, so a Windows agent refuses the request.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ReadCapabilityRequestAsync(
        IReadOnlyDictionary<string, string>? envVars,
        bool isWindows,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        string? raw = null;
        envVars?.TryGetValue(AgentEnableableCapabilities.EnvironmentVariable, out raw);
        if (!AgentEnableableCapabilities.TryParse(raw, out var capabilities, out var rejected))
        {
            await ReportPhaseAsync(AgentUpdatePhase.Failed, 10, "capability request refused", ct).ConfigureAwait(false);
            await onOutput(
                $"Self-update refused: capability '{rejected}' cannot be enabled by an agent update.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }
        if (capabilities.Count > 0 && isWindows)
        {
            await ReportPhaseAsync(AgentUpdatePhase.Failed, 10, "capability request refused", ct).ConfigureAwait(false);
            await onOutput(
                "Self-update refused: capabilities can only be enabled on a Linux agent.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }
        return capabilities;
    }

    private void LaunchWindowsUpdater(string stagingRoot, string extractDir, string installDir)
    {
        var updaterPath = WriteWindowsUpdater(stagingRoot);
        IReadOnlyList<string> updaterArguments =
        [
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            extractDir,
            installDir,
            ConfirmationTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ];
        if (DetachedLaunchOverride is null)
            AgentSelfUpdateLauncher.LaunchDetached(updaterPath, true, logger, updaterArguments);
        else
            DetachedLaunchOverride(updaterPath, updaterArguments);
    }

    private sealed record SelfUpdateContext(ResolvedAgentReleaseArchive? Release, string ExtractDirectory);

    private sealed record SelfUpdatePreparationResult(SelfUpdateContext? Context, ExecutorResult? Failure)
    {
        public static SelfUpdatePreparationResult Prepared(SelfUpdateContext context) => new(context, null);
        public static SelfUpdatePreparationResult Failed(ExecutorResult failure) => new(null, failure);
    }

    private async Task SafeReportAsync(AgentUpdatePhase phase, int percent, string? message)
    {
        try { await ReportPhaseAsync(phase, percent, message, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* logging best-effort during teardown */ }
    }

    private async Task<bool> DownloadAsync(
        string url,
        string destination,
        string? manifestSha256,
        long? manifestSizeBytes,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        // The /downloads endpoint is anonymous; a plain client (no bearer handler) is enough.
        // Threat model: the X-Content-SHA256 header below is emitted by the same origin that
        // serves the archive, so integrity reduces to "trust TLS + trust the backend". The
        // Linux archive is re-packed per request (server-URL marker), so its hash cannot be
        // pre-registered out-of-band. Against a MITM holding a trusted cert, configure
        // Aetheus:PinnedServerCertThumbprint - the transfer client's TLS handler enforces
        // the pin for this download. A compromised backend remains out of scope (it could
        // sign whatever it serves).
        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var validation = await ValidateDownloadResponseAsync(
            response, manifestSha256, manifestSizeBytes, onOutput).ConfigureAwait(false);
        if (!validation.IsValid) return false;
        var download = await SaveArchiveAsync(response, destination, ct).ConfigureAwait(false);
        return await ValidateSavedArchiveAsync(
            destination, download, validation.ExpectedSha256, manifestSizeBytes, onOutput)
            .ConfigureAwait(false);
    }

    private async Task<DownloadResponseValidation> ValidateDownloadResponseAsync(
        HttpResponseMessage response,
        string? manifestSha256,
        long? manifestSizeBytes,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (!response.IsSuccessStatusCode)
        {
            await onOutput($"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}", TaskLogLevel.Error).ConfigureAwait(false);
            return DownloadResponseValidation.Invalid;
        }
        var headerSha256 = response.Headers.TryGetValues("X-Content-SHA256", out var checksumValues)
            ? checksumValues.FirstOrDefault()
            : null;
        if (manifestSha256 is not null
            && !string.Equals(headerSha256, manifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            await onOutput(
                "Update archive checksum header does not match the immutable release manifest.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return DownloadResponseValidation.Invalid;
        }
        var expected = manifestSha256 ?? headerSha256;
        if (string.IsNullOrEmpty(expected) && !await AllowMissingChecksumAsync(onOutput).ConfigureAwait(false))
            return DownloadResponseValidation.Invalid;
        if (response.Content.Headers.ContentLength is > MaxArchiveBytes)
        {
            await onOutput(ArchiveTooLargeMessage, TaskLogLevel.Error).ConfigureAwait(false);
            return DownloadResponseValidation.Invalid;
        }
        if (manifestSizeBytes is { } expectedSize
            && response.Content.Headers.ContentLength is { } contentLength
            && contentLength != expectedSize)
        {
            await onOutput(
                $"Update archive size differs from manifest: expected {expectedSize}, got {contentLength}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return DownloadResponseValidation.Invalid;
        }
        return new DownloadResponseValidation(true, expected);
    }

    private async Task<bool> AllowMissingChecksumAsync(Func<string, TaskLogLevel, Task> onOutput)
    {
        if (!_options.AllowInsecureCerts)
        {
            logger.LogError("Agent self-update rejected: missing X-Content-SHA256 header");
            await onOutput("update rejected: missing X-Content-SHA256", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        logger.LogWarning("Update archive served without X-Content-SHA256 - accepted only because AllowInsecureCerts=true (dev mode)");
        await onOutput("WARNING: no X-Content-SHA256 header - accepted because AllowInsecureCerts=true (dev mode)", TaskLogLevel.Warning).ConfigureAwait(false);
        return true;
    }

    private static async Task<SavedArchive> SaveArchiveAsync(
        HttpResponseMessage response, string destination, CancellationToken ct)
    {
        long sizeBytes = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var exceededLimit = false;
        await using (var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                sizeBytes = checked(sizeBytes + read);
                if (sizeBytes > MaxArchiveBytes)
                {
                    exceededLimit = true;
                    break;
                }
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        return new SavedArchive(sizeBytes, Convert.ToHexString(hasher.GetHashAndReset()), exceededLimit);
    }

    private async Task<bool> ValidateSavedArchiveAsync(
        string destination,
        SavedArchive archive,
        string? expectedSha256,
        long? manifestSizeBytes,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (archive.ExceededLimit)
            return await RejectSavedArchiveAsync(destination, ArchiveTooLargeMessage, onOutput).ConfigureAwait(false);
        if (archive.SizeBytes == 0)
            return await RejectSavedArchiveAsync(destination, "Downloaded archive is empty - aborting.", onOutput).ConfigureAwait(false);
        if (manifestSizeBytes is { } manifestSize && archive.SizeBytes != manifestSize)
            return await RejectSavedArchiveAsync(
                destination,
                $"Update archive size differs from manifest: expected {manifestSize}, got {archive.SizeBytes}.",
                onOutput).ConfigureAwait(false);
        await onOutput($"Downloaded {archive.SizeBytes / 1024} KB", TaskLogLevel.Info).ConfigureAwait(false);
        if (expectedSha256 is not null
            && !string.Equals(archive.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Agent self-update rejected: invalid X-Content-SHA256 (expected {Expected}, got {Actual})", expectedSha256, archive.Sha256);
            await onOutput($"update rejected: invalid X-Content-SHA256 (expected {expectedSha256}, got {archive.Sha256})", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        if (expectedSha256 is not null)
            await onOutput("SHA256 checksum verified", TaskLogLevel.Info).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> RejectSavedArchiveAsync(
        string destination,
        string message,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        File.Delete(destination);
        await onOutput(message, TaskLogLevel.Error).ConfigureAwait(false);
        return false;
    }

    private static string ArchiveTooLargeMessage =>
        $"Update archive exceeds the {MaxArchiveBytes / (1024 * 1024)} MiB safety limit - aborting.";

    private sealed record DownloadResponseValidation(bool IsValid, string? ExpectedSha256)
    {
        public static DownloadResponseValidation Invalid { get; } = new(false, null);
    }

    private sealed record SavedArchive(long SizeBytes, string Sha256, bool ExceededLimit);

    private static async Task<bool> ExtractAsync(
        string archivePath, string extractDir, bool isWindows,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!ValidateArchive(archivePath, extractDir, isWindows, out var validationError))
        {
            await onOutput($"Update archive rejected: {validationError}", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }

        if (isWindows)
        {
            // No native zip CLI guaranteed on Windows - use the framework extractor.
            await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true), ct).ConfigureAwait(false);
            return true;
        }

        // tar is universally present on Linux and preserves the executable bit, which
        // ZipFile/TarFile-managed extraction to a Unix FS would otherwise drop for the host.
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-xzf");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(extractDir);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            await onOutput($"tar extraction failed (exit {process.ExitCode}): {stderr}", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    internal static bool ValidateArchive(
        string archivePath, string extractDir, bool isWindows, out string validationError)
    {
        try
        {
            long totalBytes = 0;
            var entries = 0;
            if (isWindows)
            {
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var entry in archive.Entries)
                {
                    ValidateArchiveEntry(entry.FullName, extractDir);
                    if (IsZipSymbolicLink(entry))
                        throw new InvalidDataException($"symbolic link entry is forbidden: {entry.FullName}");
                    AddArchiveEntryBudget(entry.Length, ref totalBytes, ref entries);
                }
            }
            else
            {
                using var archive = File.OpenRead(archivePath);
                using var gzip = new GZipStream(archive, CompressionMode.Decompress);
                using var reader = new TarReader(gzip);
                TarEntry? entry;
                while ((entry = reader.GetNextEntry(copyData: false)) is not null)
                {
                    ValidateArchiveEntry(entry.Name, extractDir);
                    if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw new InvalidDataException($"unsupported tar entry type {entry.EntryType}: {entry.Name}");
                    AddArchiveEntryBudget(entry.Length, ref totalBytes, ref entries);
                }
            }

            if (entries == 0) throw new InvalidDataException("archive contains no entries");
            validationError = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            validationError = ex.Message;
            return false;
        }
    }

    private static void AddArchiveEntryBudget(long length, ref long totalBytes, ref int entries)
    {
        if (length < 0) throw new InvalidDataException("archive entry declares a negative length");
        entries = checked(entries + 1);
        if (entries > MaxArchiveEntries)
            throw new InvalidDataException($"archive contains more than {MaxArchiveEntries} entries");

        totalBytes = checked(totalBytes + length);
        if (totalBytes > MaxExtractedPayloadBytes)
            throw new InvalidDataException(
                $"expanded payload exceeds {MaxExtractedPayloadBytes / (1024 * 1024)} MiB");
    }

    private static void ValidateArchiveEntry(string name, string extractDir)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0)
            throw new InvalidDataException("archive contains an invalid empty entry name");

        var normalized = name.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            throw new InvalidDataException($"absolute archive path is forbidden: {name}");

        var root = Path.GetFullPath(extractDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, normalized));
        if (!destination.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"archive path escapes the staging directory: {name}");
        }
    }

    private static bool IsZipSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool StagedPayloadLooksValid(string extractDir, bool isWindows)
    {
        var marker = isWindows ? "Aetheus.Agent.Windows.exe" : "Aetheus.Agent.Linux.dll";
        return File.Exists(Path.Combine(extractDir, marker));
    }

    internal static string WriteWindowsUpdater(string stagingRoot) =>
        WindowsAgentUpdaterScript.Write(stagingRoot);

}
