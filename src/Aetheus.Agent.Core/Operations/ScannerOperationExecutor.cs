// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static Aetheus.Agent.Core.Operations.ScannerOperationSupport;

namespace Aetheus.Agent.Core.Operations;

public sealed class ScannerOperationExecutor(
    IServerApiClient apiClient,
    IHttpClientFactory httpClientFactory,
    IOptions<AetheusAgentOptions> options,
    IScannerProcessRunner processRunner,
    ScannerSourceProjectionManager sourceProjectionManager,
    TimeProvider timeProvider,
    ILogger<ScannerOperationExecutor> logger) : EnvironmentOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;
    private readonly ScannerReportPublisher _reportPublisher = new(apiClient, timeProvider);
    public override bool CanHandle(OperationKind kind) => kind == OperationKind.PipelineRunScanner;
    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (kind != OperationKind.PipelineRunScanner) return new ExecutorResult(-1, false);
        var scanner = ScannerManifestCatalog.Find(target);
        if (scanner is null)
        {
            await onOutput("Scanner refused: key is absent from the immutable manifest.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!TryPositiveInt(envVars, "AETHEUS_RUN_ID", out var runId))
        {
            await onOutput("Scanner refused: pipeline run identity is missing.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Max(1, timeoutSeconds);
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var operationToken = operationTimeout.Token;
        var scanRoot = Path.Combine(_options.WorkDirectory, "scans", $"{runId}-{scanner.Key}-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(scanRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        ScannerFileSecurity.TrySetOwnerOnly(scanRoot);
        ScannerFileSecurity.TrySetOwnerOnly(outputDirectory);
        var resources = new ScannerExecutionResources();
        var invocation = new ScanInvocation(
            scanner, runId, envVars, timeoutSeconds, startedAt, scanRoot, outputDirectory, onOutput, operationToken);

        try
        {
            return await ExecuteScanCoreAsync(invocation, resources).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EnsureOperationTimedOut(cancellationToken, operationTimeout);
            return await _reportPublisher.PublishFailureAsync(
                scanner,
                runId,
                envVars,
                startedAt,
                AnalysisReportStatus.TimedOut,
                $"Scanner timed out after {timeoutSeconds} seconds including resource admission and preparation.",
                onOutput,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await CleanupAsync(
                    runId, scanRoot, resources.SourceProjection, resources.Egress, onOutput).ConfigureAwait(false);
            }
            finally
            {
                if (resources.ResourceReservation is not null)
                    await resources.ResourceReservation.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<ExecutorResult> ExecuteScanCoreAsync(
        ScanInvocation invocation,
        ScannerExecutionResources resources)
    {
        await AcquireResourceBudgetAsync(invocation, resources).ConfigureAwait(false);
        var configuredSourceDirectory = ScannerOperationSupport.ResolveSourceDirectory(
            _options.WorkDirectory, invocation.RunId, invocation.Environment);
        var workspaceFailure = await ValidateWorkspaceAsync(
            invocation.Scanner, configuredSourceDirectory, invocation.RunId, invocation.Environment,
            invocation.StartedAt, invocation.OnOutput, invocation.CancellationToken).ConfigureAwait(false);
        if (workspaceFailure is not null) return workspaceFailure;

        var effectiveEnvironment = await GitleaksHistoryModeResolver.ResolveEnvironmentAsync(
            invocation.Scanner, configuredSourceDirectory, invocation.Environment,
            invocation.OnOutput, invocation.CancellationToken).ConfigureAwait(false);
        var sourceDirectory = await AcquireSourceProjectionAsync(
            invocation, configuredSourceDirectory, resources).ConfigureAwait(false);
        var scannerVersion = ScannerOperationValidator.ResolveScannerVersion(invocation.Scanner, sourceDirectory);
        if (scannerVersion is null)
            return await FailAsync(invocation, AnalysisReportStatus.Unavailable,
                $"Scanner is unavailable: project package '{invocation.Scanner.VersionPackage}' is not present in the immutable dependency lock.")
                .ConfigureAwait(false);
        if (!ScannerOperationValidator.ValidateDastTarget(
                invocation.Scanner, invocation.Environment, timeProvider, out var dastError))
            return await FailAsync(invocation, AnalysisReportStatus.Error, dastError).ConfigureAwait(false);
        var imageAssociation = await ValidateImageAssociationAsync(
            invocation.Scanner, sourceDirectory, invocation.Environment, invocation.CancellationToken)
            .ConfigureAwait(false);
        if (!imageAssociation.IsValid)
            return await FailAsync(invocation, AnalysisReportStatus.Error,
                $"Built-artifact analysis refused: {imageAssociation.Reason}.").ConfigureAwait(false);

        resources.Egress = await RestrictedScannerEgress.StartAsync(
            invocation.Scanner, invocation.Environment, processRunner,
            invocation.OnOutput, invocation.CancellationToken).ConfigureAwait(false);
        var cacheDirectory = PrepareScannerCache(invocation.Scanner);
        var reportPath = Path.GetFullPath(Path.Combine(invocation.OutputDirectory, invocation.Scanner.ReportPath));
        if (!IsChildPath(invocation.OutputDirectory, reportPath))
            return await FailAsync(invocation, AnalysisReportStatus.Error,
                "Scanner manifest report path escapes its output directory.").ConfigureAwait(false);

        var rulesDirectory = await ExtractEmbeddedRulesAsync(
            invocation.Scanner, invocation.ScanRoot, invocation.CancellationToken).ConfigureAwait(false);
        var trustedHarnessPath = await ScannerContainerProcessBuilder.ExtractEmbeddedHarnessAsync(
            invocation.Scanner, invocation.ScanRoot, invocation.CancellationToken).ConfigureAwait(false);
        if (ScannerApiSpecifications.IsPartitioned(effectiveEnvironment))
            return await RunPartitionsAndPublishAsync(
                invocation, sourceDirectory, reportPath, rulesDirectory, cacheDirectory, trustedHarnessPath,
                scannerVersion, effectiveEnvironment, resources.Egress).ConfigureAwait(false);
        var process = await CreateScannerProcessAsync(
            invocation, sourceDirectory, reportPath, rulesDirectory, cacheDirectory,
            trustedHarnessPath, effectiveEnvironment, resources.Egress).ConfigureAwait(false);
        return process.Failure ?? await RunAndPublishAsync(
            invocation, process.Process!, sourceDirectory, reportPath, rulesDirectory,
            cacheDirectory, scannerVersion, effectiveEnvironment).ConfigureAwait(false);
    }

    private async Task AcquireResourceBudgetAsync(
        ScanInvocation invocation,
        ScannerExecutionResources resources)
    {
        await invocation.OnOutput(
            $"Scanner resource request: memory={invocation.Scanner.Memory}, cpus={invocation.Scanner.Cpus}; " +
            "candidate budget=6.5 Go/7CPU, at most one heavy scanner.",
            TaskLogLevel.Info).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        resources.ResourceReservation = await ScannerResourceBudget.Shared.AcquireAsync(
            invocation.Scanner, invocation.CancellationToken).ConfigureAwait(false);
        timer.Stop();
        await invocation.OnOutput(
            "##aetheus[pipelinemetric key=scanner.budget.wait;type=Duration;unit=s]" +
            timer.Elapsed.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
            TaskLogLevel.Info).ConfigureAwait(false);
        await invocation.OnOutput(
            $"Scanner resource budget admitted {invocation.Scanner.Key} after {timer.Elapsed.TotalSeconds:0.###}s.",
            TaskLogLevel.Info).ConfigureAwait(false);
    }

    private async Task<string> AcquireSourceProjectionAsync(
        ScanInvocation invocation,
        string configuredSourceDirectory,
        ScannerExecutionResources resources)
    {
        var timer = Stopwatch.StartNew();
        resources.SourceProjection = await sourceProjectionManager.AcquireAsync(
            invocation.RunId,
            configuredSourceDirectory,
            _options.WorkDirectory,
            !string.IsNullOrWhiteSpace(invocation.Scanner.VersionPackage),
            invocation.CancellationToken).ConfigureAwait(false);
        timer.Stop();
        await invocation.OnOutput(
            "##aetheus[pipelinemetric key=scanner.projection;type=Duration;unit=s]" +
            timer.Elapsed.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
            TaskLogLevel.Info).ConfigureAwait(false);
        return resources.SourceProjection.SourceDirectory;
    }

    private async Task<ProcessCreation> CreateScannerProcessAsync(
        ScanInvocation invocation,
        string sourceDirectory,
        string reportPath,
        string? rulesDirectory,
        string? cacheDirectory,
        string? trustedHarnessPath,
        IReadOnlyDictionary<string, string> effectiveEnvironment,
        RestrictedScannerEgress? egress)
    {
        var skipDatabaseUpdate = await TrivyDatabaseFreshness.ShouldSkipUpdateAsync(
            invocation.Scanner, cacheDirectory, timeProvider.GetUtcNow(), invocation.OnOutput).ConfigureAwait(false);
        try
        {
            var process = string.Equals(invocation.Scanner.Execution, "container", StringComparison.Ordinal)
                ? ScannerContainerProcessBuilder.Build(
                    invocation.Scanner, sourceDirectory, invocation.OutputDirectory, cacheDirectory,
                    trustedHarnessPath, effectiveEnvironment, egress, skipDatabaseUpdate)
                : await BuildBinaryProcessAsync(
                    invocation.Scanner, sourceDirectory, reportPath, rulesDirectory,
                    invocation.CancellationToken).ConfigureAwait(false);
            return new ProcessCreation(process, null);
        }
        catch (HttpRequestException ex)
        {
            return await ScannerUnavailableAsync(invocation, ex).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return await ScannerUnavailableAsync(invocation, ex).ConfigureAwait(false);
        }
    }

    private async Task<ProcessCreation> ScannerUnavailableAsync(ScanInvocation invocation, Exception exception)
    {
        logger.LogWarning(exception, "Scanner {ScannerKey} is unavailable", invocation.Scanner.Key);
        var failure = await FailAsync(
            invocation, AnalysisReportStatus.Unavailable, GetScannerUnavailableMessage(exception)).ConfigureAwait(false);
        return new ProcessCreation(null, failure);
    }

    private async Task<ExecutorResult> RunAndPublishAsync(
        ScanInvocation invocation,
        ProcessStartInfo process,
        string sourceDirectory,
        string reportPath,
        string? rulesDirectory,
        string? cacheDirectory,
        string scannerVersion,
        IReadOnlyDictionary<string, string> effectiveEnvironment)
    {
        await LogStartedAsync(invocation, scannerVersion).ConfigureAwait(false);
        var run = await RunToReportAsync(
            invocation, process, sourceDirectory, reportPath, cacheDirectory, effectiveEnvironment, string.Empty)
            .ConfigureAwait(false);
        return run.Failure ?? await PublishSuccessfulReportAsync(
            invocation, reportPath, rulesDirectory, scannerVersion, run.ExitCode).ConfigureAwait(false);
    }

    private static Task LogStartedAsync(ScanInvocation invocation, string scannerVersion) => invocation.OnOutput(
        $"Scanner {invocation.Scanner.Name} {scannerVersion} started ({invocation.Scanner.Execution}, network={invocation.Scanner.Network}).",
        TaskLogLevel.Info);

    /// <summary>Runs one scanner process and checks its report; <paramref name="scope"/> prefixes every failure.</summary>
    private async Task<(ExecutorResult? Failure, int ExitCode)> RunToReportAsync(
        ScanInvocation invocation,
        ProcessStartInfo process,
        string sourceDirectory,
        string reportPath,
        string? cacheDirectory,
        IReadOnlyDictionary<string, string> effectiveEnvironment,
        string scope)
    {
        var executionRunner = new ScannerProcessExecutionRunner(processRunner, timeProvider);
        var (result, outputQuotaExceeded) = await executionRunner.RunAsync(
            invocation.Scanner, process, reportPath, invocation.OutputDirectory, cacheDirectory,
            invocation.TimeoutSeconds, invocation.OnOutput, invocation.CancellationToken, sourceDirectory)
            .ConfigureAwait(false);
        var status = AnalysisReportStatus.Error;
        string? error;
        if (outputQuotaExceeded)
            error = "Scanner output or persistent cache exceeded its configured quota.";
        else if (result.TimedOut)
            (status, error) = (AnalysisReportStatus.TimedOut, $"Scanner timed out after {invocation.TimeoutSeconds} seconds.");
        else if (result.ExitCode != 0 && !invocation.Scanner.FindingExitCodes.Contains(result.ExitCode))
            error = $"Scanner exited with code {result.ExitCode}.";
        else
            error = await CheckReportAsync(invocation, reportPath, effectiveEnvironment).ConfigureAwait(false);
        return error is null
            ? (null, result.ExitCode)
            : (await FailAsync(invocation, status, scope + error).ConfigureAwait(false), result.ExitCode);
    }

    /// <summary>The definitions of one step, scanned in sequence, published as one
    /// report.</summary>
    private async Task<ExecutorResult> RunPartitionsAndPublishAsync(
        ScanInvocation invocation, string sourceDirectory, string reportPath, string? rulesDirectory,
        string? cacheDirectory, string? trustedHarnessPath, string scannerVersion,
        IReadOnlyDictionary<string, string> environment, RestrictedScannerEgress? egress)
    {
        await LogStartedAsync(invocation, scannerVersion).ConfigureAwait(false);
        var outcome = await ScannerApiSpecifications.ScanInSequenceAsync(
            invocation, environment, reportPath,
            async (partition, partitionEnvironment, partitionReport, scope) =>
            {
                var process = await CreateScannerProcessAsync(
                    partition, sourceDirectory, partitionReport, rulesDirectory, cacheDirectory,
                    trustedHarnessPath, partitionEnvironment, egress).ConfigureAwait(false);
                return process.Failure is not null
                    ? (process.Failure, 0)
                    : await RunToReportAsync(
                        partition, process.Process!, sourceDirectory, partitionReport, cacheDirectory,
                        partitionEnvironment, scope).ConfigureAwait(false);
            }).ConfigureAwait(false);
        if (outcome.Failure is not null) return outcome.Failure;
        var merged = new FileInfo(reportPath);
        if (merged.Length > invocation.Scanner.MaxReportBytes)
            return await FailAsync(invocation, AnalysisReportStatus.Error,
                $"Merged scanner report size {merged.Length} is outside the allowed range.").ConfigureAwait(false);
        return await PublishSuccessfulReportAsync(
            invocation, reportPath, rulesDirectory, scannerVersion, outcome.ExitCode).ConfigureAwait(false);
    }

    private async Task<ExecutorResult> PublishSuccessfulReportAsync(
        ScanInvocation invocation,
        string reportPath,
        string? rulesDirectory,
        string scannerVersion,
        int exitCode)
    {
        var artifactId = await UploadRawReportAsync(
            invocation.Scanner, invocation.RunId, invocation.Environment, reportPath,
            invocation.ScanRoot, invocation.CancellationToken).ConfigureAwait(false);
        var reportContent = await File.ReadAllTextAsync(reportPath, invocation.CancellationToken).ConfigureAwait(false);
        var request = CreatePublishRequest(
            invocation, rulesDirectory, scannerVersion, exitCode, artifactId, reportContent);
        var published = await apiClient.PublishAnalysisReportAsync(
            invocation.RunId, request, invocation.CancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            await invocation.OnOutput(
                "Scanner report publication returned no result.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        var level = published.GateStatus is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error
            ? TaskLogLevel.Warning
            : TaskLogLevel.Info;
        await invocation.OnOutput(
            $"Scanner report published: gate={published.GateStatus}, findings={published.FindingCount}, new={published.NewFindingCount}.",
            level).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    private PublishAnalysisReportRequest CreatePublishRequest(
        ScanInvocation invocation,
        string? rulesDirectory,
        string scannerVersion,
        int exitCode,
        int artifactId,
        string reportContent) => new()
        {
            ScannerKey = invocation.Scanner.Key,
            ScannerName = invocation.Scanner.Name,
            ScannerVersion = scannerVersion,
            Category = Enum.Parse<AnalysisCategory>(invocation.Scanner.Category, ignoreCase: true),
            Status = exitCode == 0 ? AnalysisReportStatus.Passed : AnalysisReportStatus.Failed,
            Format = Enum.Parse<AnalysisReportFormat>(invocation.Scanner.ReportFormat, ignoreCase: true),
            ReportContent = reportContent,
            ReportPath = invocation.Scanner.ReportPath,
            PipelineArtifactId = artifactId,
            StageName = Value(invocation.Environment, "AETHEUS_STAGE_NAME"),
            StepName = Value(invocation.Environment, "AETHEUS_STEP_NAME"),
            EnvironmentName = Value(invocation.Environment, "AETHEUS_SCANNER_ENVIRONMENT_NAME"),
            RuleSetHash = ScannerOperationValidator.ComputeRuleSetHash(rulesDirectory, invocation.Scanner),
            DastLeaseToken = Value(invocation.Environment, "AETHEUS_SCANNER_DAST_LEASE_TOKEN"),
            DastTargetUrl = Value(invocation.Environment, "AETHEUS_SCANNER_TARGET_URL"),
            StartedAt = invocation.StartedAt,
            CompletedAt = timeProvider.GetUtcNow().UtcDateTime
        };

    private Task<ExecutorResult> FailAsync(
        ScanInvocation invocation,
        AnalysisReportStatus status,
        string message) => _reportPublisher.PublishFailureAsync(
        invocation.Scanner,
        invocation.RunId,
        invocation.Environment,
        invocation.StartedAt,
        status,
        message,
        invocation.OnOutput,
        invocation.CancellationToken);

    private static bool IsChildPath(string parentDirectory, string path) =>
        path.StartsWith(
            Path.GetFullPath(parentDirectory) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    private static void EnsureOperationTimedOut(
        CancellationToken cancellationToken,
        CancellationTokenSource operationTimeout)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!operationTimeout.IsCancellationRequested)
            throw new OperationCanceledException(operationTimeout.Token);
    }

    private async Task<ExecutorResult?> ValidateWorkspaceAsync(
        ScannerManifestEntry scanner,
        string sourceDirectory,
        int runId,
        IReadOnlyDictionary<string, string> envVars,
        DateTime startedAt,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
            return await _reportPublisher.PublishFailureAsync(scanner, runId, envVars, startedAt,
                AnalysisReportStatus.Unavailable, "Scanner workspace is unavailable.", onOutput, cancellationToken)
                .ConfigureAwait(false);
        if (!ScannerOperationValidator.HasApplicableSource(scanner, sourceDirectory))
            return await _reportPublisher.PublishStatusAsync(scanner, scanner.Version, runId, envVars, startedAt,
                AnalysisReportStatus.NotApplicable,
                "Scanner is not applicable: no supported source file was detected.",
                onOutput, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task CleanupAsync(
        int runId,
        string scanRoot,
        ScannerSourceProjectionLease? sourceProjection,
        RestrictedScannerEgress? egress,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        // Every resource is released even when an earlier one fails. The error reported is the network's
        // when it failed, else the projection's, else the workspace's.
        var projectionError = await TryReleaseAsync(sourceProjection, ex =>
            logger.LogError(ex, "Shared scanner source projection release failed for run {RunId}", runId)).ConfigureAwait(false);
        var egressError = await TryReleaseAsync(egress, ex =>
            logger.LogError(ex, "Scanner network cleanup failed for {ScanRoot}", scanRoot)).ConfigureAwait(false);
        var workspaceError = TryDeleteScanRoot(scanRoot);
        var cleanupError = egressError ?? projectionError ?? workspaceError;
        if (cleanupError is null)
            return;
        await onOutput("Scanner workspace cleanup failed; operator intervention is required.", TaskLogLevel.Error)
            .ConfigureAwait(false);
        throw new IOException("Scanner resource cleanup failed.", cleanupError);
    }

    private static async Task<Exception?> TryReleaseAsync(IAsyncDisposable? resource, Action<Exception> logFailure)
    {
        if (resource is null) return null;
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            logFailure(ex);
            return ex;
        }
    }

    private Exception? TryDeleteScanRoot(string scanRoot)
    {
        try
        {
            if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scanner workspace cleanup failed for {ScanRoot}", scanRoot);
            return ex;
        }
    }

    private async Task<ProcessStartInfo> BuildBinaryProcessAsync(
        ScannerManifestEntry scanner,
        string sourceDirectory,
        string reportPath,
        string? rulesDirectory,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This verified scanner binary is available only for Linux x64.");
        OpenGrepSandbox.RequireVerifiedDownload(scanner);

        var binaryPath = OpenGrepSandbox.BinaryPath(_options.WorkDirectory, scanner);
        await OpenGrepSandbox.EnsureBinaryAsync(scanner, binaryPath, DownloadScannerAsync, ct).ConfigureAwait(false);
        var outputDirectory = RequireBinaryRuntime(scanner, reportPath, rulesDirectory);
        List<SandboxMount> mounts =
        [
            new(sourceDirectory, "/src", ReadOnly: true),
            new(outputDirectory, "/out", ReadOnly: false)
        ];
        if (!string.IsNullOrWhiteSpace(rulesDirectory))
            mounts.Add(new SandboxMount(rulesDirectory, "/rules", ReadOnly: true));
        return OpenGrepSandbox.BuildDockerProcess(
            scanner, binaryPath, mounts,
            ScannerOperationValidator.ResolveArguments(scanner.Arguments, new Dictionary<string, string>(), "/src",
                "/out/" + scanner.ReportPath, "/rules", "/out"));
    }

    /// <summary>The report directory, once the scanner has its rules (OpenGrep) and a runtime image
    /// pinned by digest.</summary>
    internal static string RequireBinaryRuntime(ScannerManifestEntry scanner, string reportPath, string? rulesDirectory)
    {
        if (string.Equals(scanner.Key, OpenGrepSandbox.ScannerKey, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(rulesDirectory))
            throw new IOException("Embedded versioned OpenGrep rules are unavailable.");
        OpenGrepSandbox.RequirePinnedRuntime(scanner);
        return Path.GetDirectoryName(reportPath)
            ?? throw new IOException("Binary scanner report directory is unavailable.");
    }

    /// <summary>The scanner download through the agent's named client, refused off Linux where the
    /// binary could never run.</summary>
    private async Task DownloadScannerAsync(Uri downloadUri, string destinationPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Scanner binary installation requires Linux.");
        using var client = httpClientFactory.CreateClient("AetheusScannerDownload");
        await using var source = await client.GetStreamAsync(downloadUri, ct).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <summary>Every in-house OpenGrep rule file, extracted into the scan root; null for any other
    /// scanner.</summary>
    private static async Task<string?> ExtractEmbeddedRulesAsync(
        ScannerManifestEntry scanner,
        string scanRoot,
        CancellationToken ct)
    {
        if (!string.Equals(scanner.Key, OpenGrepSandbox.ScannerKey, StringComparison.OrdinalIgnoreCase)) return null;
        var directory = Path.Combine(scanRoot, "rules");
        await OpenGrepSandbox.ExtractEmbeddedRulesAsync(directory, ct).ConfigureAwait(false);
        return directory;
    }

    private async Task<int> UploadRawReportAsync(
        ScannerManifestEntry scanner,
        int runId,
        IReadOnlyDictionary<string, string> envVars,
        string reportPath,
        string scanRoot,
        CancellationToken ct)
    {
        var zipPath = Path.Combine(scanRoot, "report.zip");
        await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
        {
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry(scanner.ReportPath, CompressionLevel.SmallestSize);
                await using var entryStream = entry.Open();
                await using var source = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await source.CopyToAsync(entryStream, ct).ConfigureAwait(false);
            }
            zipStream.Position = 0;
            var artifact = await apiClient.UploadArtifactAsync(
                runId, ScannerOperationSupport.BuildRawArtifactName(scanner, envVars),
                Value(envVars, "AETHEUS_STAGE_NAME"), zipStream, ct).ConfigureAwait(false);
            return artifact?.Id
                ?? throw new InvalidOperationException($"Scanner report artifact '{scanner.Key}' upload returned no artifact.");
        }
    }

    private string? PrepareScannerCache(ScannerManifestEntry scanner)
    {
        if (string.IsNullOrWhiteSpace(scanner.CacheDirectoryName)) return null;
        var path = Path.Combine(_options.WorkDirectory, "scanner-cache", scanner.CacheDirectoryName);
        Directory.CreateDirectory(path);
        ScannerFileSecurity.TrySetOwnerOnly(path);
        if (ScannerProcessExecutionRunner.DirectorySize(path, scanner.MaxCacheBytes) > scanner.MaxCacheBytes)
            throw new IOException($"Scanner cache '{scanner.CacheDirectoryName}' exceeds its configured quota.");
        return path;
    }

    internal static string GetScannerUnavailableMessage(Exception exception) =>
        ScannerOperationSupport.ScannerUnavailableMessage(exception);

    internal static Task<bool> ValidateReportStructureAsync(
        ScannerManifestEntry scanner, string path, CancellationToken ct) =>
        ScannerOperationSupport.ValidateReportStructureAsync(scanner, path, ct);

}
