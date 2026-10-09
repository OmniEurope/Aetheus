// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;
using System.Text.Json;
using static Aetheus.Agent.Core.Operations.BackupOperationHelpers;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// ADR-024 4.3: runs a backup (DB dump + optional file archive) and a restore-check on the target host.
/// No sudo: pg_dump/mysqldump run as a normal DB client; the password rides in <c>PGPASSWORD</c>/
/// <c>MYSQL_PWD</c> in the process env (off the process list), never in argv. The restore-check restores
/// onto a REAL throwaway DB created for the test and dropped after - a backup is only "verified" when that
/// actually succeeds (no-fake). Results are reported to the backend, which owns the run state machine.
/// </summary>
public sealed class BackupOperationExecutor(
    IServerApiClient apiClient,
    ILogger<BackupOperationExecutor> logger) : IOperationExecutor
{
    internal string BackupBaseDirectory { get; set; } = BackupConstants.BaseDir;
    internal BackupProcessInvoker? ProcessInvoker { get; set; }
    internal Action<BackupFileRestoreBoundary, int>? FileRestoreBoundaryHook { get; set; }

    public bool CanHandle(OperationKind kind) => kind is OperationKind.BackupExecute or OperationKind.BackupRestoreCheck or OperationKind.BackupRestore;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
        => ExecuteAsync(kind, target, new Dictionary<string, string>(), timeoutSeconds, onOutput, ct);

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> env,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await onOutput("Backups are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        return await ExecuteSupportedPlatformAsync(
            kind, env, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
    }

    internal async Task<ExecutorResult> ExecuteSupportedPlatformAsync(
        OperationKind kind,
        IReadOnlyDictionary<string, string> env,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct) => kind switch
        {
            OperationKind.BackupExecute => await RunBackupAsync(env, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            OperationKind.BackupRestoreCheck => await RunRestoreCheckAsync(env, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            OperationKind.BackupRestore => await RunLiveRestoreAsync(env, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            _ => new ExecutorResult(1, false)
        };

    private async Task<ExecutorResult> RunBackupAsync(
        IReadOnlyDictionary<string, string> env, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var engine = ParseEngine(env);
        var runId = GetInt(env, BackupConstants.RunIdEnvVar);
        var policyId = GetInt(env, BackupConstants.PolicyIdEnvVar);
        if (runId is null || policyId is null)
        {
            await onOutput("Missing backup run/policy id", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var dir = Path.Combine(BackupBaseDirectory, policyId.Value.ToString());
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, $"backup-{runId}.aetheus-backup");
        var databaseDump = engine == BackupDbEngine.None
            ? null
            : Path.Combine(dir, $"backup-{runId}.{(engine == BackupDbEngine.MySql ? "sql" : "dump")}.partial");

        try
        {
            var filePaths = BackupArchiveBundle.ParseFilePaths(env);
            if (engine == BackupDbEngine.None && filePaths.Count == 0)
                throw new InvalidDataException("No database or file paths are configured.");

            if (databaseDump is not null)
            {
                var (host, port, name, user, pwd) = DbParams(env, engine);
                var dump = engine == BackupDbEngine.Postgres
                    ? await RunAsync("pg_dump", BackupCommandBuilder.PgDump(host, port, user, name, databaseDump), PgEnv(pwd), timeoutSeconds, onOutput, ct).ConfigureAwait(false)
                    : await RunAsync("mysqldump", BackupCommandBuilder.MysqlDump(host, port, user, name, databaseDump), MysqlEnv(pwd), timeoutSeconds, onOutput, ct).ConfigureAwait(false);
                if (dump.ExitCode != 0)
                {
                    await apiClient.ReportBackupResultAsync(runId.Value,
                        new BackupExecuteResultDto { Success = false, Message = $"Dump failed (exit {dump.ExitCode})." }, ct).ConfigureAwait(false);
                    return dump;
                }
            }

            await BackupArchiveBundle.CreateAsync(archive, databaseDump, filePaths, ct).ConfigureAwait(false);
            var size = new FileInfo(archive).Length;
            var sha = await ComputeSha256Async(archive, ct).ConfigureAwait(false);
            BackupOperationHelpers.PruneOldArchives(
                dir, GetInt(env, BackupConstants.RetentionEnvVar) ?? 7, logger);

            await onOutput($"Backup complete: {archive} ({size} bytes, {filePaths.Count} file source(s))", TaskLogLevel.Info).ConfigureAwait(false);
            await apiClient.ReportBackupResultAsync(runId.Value,
                new BackupExecuteResultDto { Success = true, ArchivePath = archive, SizeBytes = size, Sha256 = sha }, ct).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Backup archive creation failed for run {RunId}", runId.Value);
            await onOutput($"Backup failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            await apiClient.ReportBackupResultAsync(runId.Value,
                new BackupExecuteResultDto { Success = false, Message = ex.Message }, ct).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        finally
        {
            if (databaseDump is not null && File.Exists(databaseDump)) File.Delete(databaseDump);
        }
    }

    private async Task<ExecutorResult> RunRestoreCheckAsync(
        IReadOnlyDictionary<string, string> env, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var engine = ParseEngine(env);
        var runId = GetInt(env, BackupConstants.RunIdEnvVar);
        env.TryGetValue(BackupConstants.ArchivePathEnvVar, out var archive);
        if (runId is null || string.IsNullOrEmpty(archive) || !File.Exists(archive))
        {
            if (runId is not null)
                await apiClient.ReportRestoreCheckResultAsync(runId.Value, new RestoreCheckResultDto { Verified = false, Message = "Archive not found." }, ct).ConfigureAwait(false);
            await onOutput("Restore-check: archive not found", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var openedArchive = await TryOpenVerifiedArchiveAsync(archive, engine, ct).ConfigureAwait(false);
        if (openedArchive.Error is not null)
        {
            await onOutput($"Restore-check: archive verification failed - {openedArchive.Error.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            await apiClient.ReportRestoreCheckResultAsync(runId.Value,
                new RestoreCheckResultDto { Verified = false, Message = openedArchive.Error.Message }, ct).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        var bundle = openedArchive.Bundle;
        var databaseArchive = openedArchive.DatabaseArchive;

        var (host, port, _, user, pwd) = DbParams(env, engine);
        var scratch = $"aetheus_restorecheck_{runId}";
        (bool Verified, string Message) result;
        try
        {
            result = await VerifyRestoreAsync(
                engine, bundle, databaseArchive, host, port, user, pwd, scratch,
                timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        }
        finally
        {
            // The scratch database holds restored production data: drop it on its own bounded budget,
            // because the task token is already cancelled when the restore was interrupted.
            try
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 30)));
                await DropScratchDatabaseAsync(
                    engine, host, port, user, pwd, scratch, timeoutSeconds, onOutput, cleanupCts.Token).ConfigureAwait(false);
            }
            finally
            {
                bundle?.Dispose();
            }
        }

        await onOutput($"Restore-check: {(result.Verified ? "VERIFIED" : "FAILED")} - {result.Message}", result.Verified ? TaskLogLevel.Info : TaskLogLevel.Error).ConfigureAwait(false);
        await apiClient.ReportRestoreCheckResultAsync(runId.Value, new RestoreCheckResultDto { Verified = result.Verified, Message = result.Message }, ct).ConfigureAwait(false);
        return new ExecutorResult(result.Verified ? 0 : 1, false);
    }

    private async Task<ExecutorResult> RunLiveRestoreAsync(
        IReadOnlyDictionary<string, string> env, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var engine = ParseEngine(env);
        env.TryGetValue(BackupConstants.ArchivePathEnvVar, out var archive);
        if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
        {
            await onOutput("Database restore refused: verified backup archive is not present on this server.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var openedArchive = await TryOpenVerifiedArchiveAsync(archive, engine, ct).ConfigureAwait(false);
        if (openedArchive.Error is not null)
        {
            await onOutput($"Restore refused: archive verification failed - {openedArchive.Error.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        var bundle = openedArchive.Bundle;
        var databaseArchive = openedArchive.DatabaseArchive;

        var (host, port, name, user, pwd) = DbParams(env, engine);
        var refusal = GetLiveRestoreRefusal(engine, bundle, name, user);
        if (refusal is not null)
        {
            await onOutput(refusal, TaskLogLevel.Error).ConfigureAwait(false);
            bundle?.Dispose();
            return new ExecutorResult(1, false);
        }

        try
        {
            return await ExecuteVerifiedLiveRestoreAsync(
                engine, bundle, databaseArchive, host, port, name, user, pwd,
                timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        }
        finally
        {
            bundle?.Dispose();
        }
    }

    private static async Task<(VerifiedBackupBundle? Bundle, string? DatabaseArchive)> OpenVerifiedArchiveAsync(
        string archive,
        BackupDbEngine engine,
        CancellationToken ct)
    {
        if (!Path.GetExtension(archive).Equals(".aetheus-backup", StringComparison.OrdinalIgnoreCase))
            return (null, archive);

        var bundle = await BackupArchiveBundle.OpenVerifiedAsync(archive, ct).ConfigureAwait(false);
        try
        {
            var databaseArchive = bundle.DatabaseDumpPath;
            if (engine != BackupDbEngine.None && databaseArchive is null)
                throw new InvalidDataException("Backup bundle does not contain the configured database dump.");
            return (bundle, databaseArchive);
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    private static async Task<VerifiedArchiveOpenResult> TryOpenVerifiedArchiveAsync(
        string archive,
        BackupDbEngine engine,
        CancellationToken ct)
    {
        try
        {
            var (bundle, databaseArchive) = await OpenVerifiedArchiveAsync(archive, engine, ct).ConfigureAwait(false);
            return new VerifiedArchiveOpenResult(bundle, databaseArchive, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return new VerifiedArchiveOpenResult(null, archive, ex);
        }
    }

    private sealed record VerifiedArchiveOpenResult(
        VerifiedBackupBundle? Bundle,
        string? DatabaseArchive,
        Exception? Error);

    private async Task<ExecutorResult> ExecuteVerifiedLiveRestoreAsync(
        BackupDbEngine engine, VerifiedBackupBundle? bundle, string? databaseArchive,
        string host, int port, string name, string user, string? password, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var (rollbackDump, snapshotFailure) = await CaptureRollbackSnapshotAsync(
            engine, host, port, name, user, password, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (snapshotFailure is not null)
            return snapshotFailure;

        var databaseMayHaveChanged = engine != BackupDbEngine.None;
        var removeRollbackDump = false;
        try
        {
            var restore = await RestoreDatabaseAsync(
                engine, databaseArchive, host, port, name, user, password,
                timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (restore.ExitCode != 0)
            {
                await onOutput("Database restore failed.", TaskLogLevel.Error).ConfigureAwait(false);
                removeRollbackDump = await CompensateDatabaseIfNeededAsync(
                    databaseMayHaveChanged, engine, rollbackDump, host, port, name, user, password,
                    timeoutSeconds, onOutput).ConfigureAwait(false);
                return restore;
            }

            await RestoreBundleFilesAsync(bundle, onOutput, ct, FileRestoreBoundaryHook).ConfigureAwait(false);
            removeRollbackDump = true;
            await onOutput("Verified backup restore completed.", TaskLogLevel.Warning).ConfigureAwait(false);
            return restore;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            removeRollbackDump = await CompensateDatabaseIfNeededAsync(
                databaseMayHaveChanged, engine, rollbackDump, host, port, name, user, password,
                timeoutSeconds, onOutput).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            removeRollbackDump = await CompensateDatabaseIfNeededAsync(
                databaseMayHaveChanged, engine, rollbackDump, host, port, name, user, password,
                timeoutSeconds, onOutput).ConfigureAwait(false);
            logger.LogError(ex, "File restore failed");
            await onOutput($"File restore failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        finally
        {
            if (removeRollbackDump)
                DeleteRollbackDump(rollbackDump, logger);
        }
    }

    private async Task<(string? RollbackDump, ExecutorResult? Failure)> CaptureRollbackSnapshotAsync(
        BackupDbEngine engine, string host, int port, string name, string user, string? password,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (engine == BackupDbEngine.None)
            return (null, null);

        await onOutput($"Restoring verified backup into '{name}' (explicit rollback request)…", TaskLogLevel.Warning).ConfigureAwait(false);
        var rollbackDirectory = Path.Combine(BackupBaseDirectory, "restore-rollback");
        BackupFilesystemMetadata.CreatePrivateDirectory(rollbackDirectory);
        var rollbackDump = Path.Combine(
            rollbackDirectory,
            $"database-{Guid.NewGuid():N}.{(engine == BackupDbEngine.MySql ? "sql" : "dump")}");
        var snapshot = await DumpDatabaseAsync(
            engine, rollbackDump, host, port, name, user, password,
            timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (snapshot.ExitCode != 0 || !File.Exists(rollbackDump) || new FileInfo(rollbackDump).Length == 0)
        {
            DeleteRollbackDump(rollbackDump, logger);
            await onOutput("Restore refused: current database snapshot failed.", TaskLogLevel.Error).ConfigureAwait(false);
            return (null, snapshot.ExitCode == 0 ? new ExecutorResult(1, false) : snapshot);
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(rollbackDump, BackupFilesystemMetadata.PrivateFileMode);
        return (rollbackDump, null);
    }

    private async Task<bool> CompensateDatabaseIfNeededAsync(
        bool databaseMayHaveChanged, BackupDbEngine engine, string? rollbackDump,
        string host, int port, string name, string user, string? password, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (!databaseMayHaveChanged)
            return false;
        return await TryRollbackDatabaseAsync(
            engine, rollbackDump, host, port, name, user, password,
            timeoutSeconds, onOutput).ConfigureAwait(false);
    }

    private async Task<(bool Verified, string Message)> VerifyRestoreAsync(
        BackupDbEngine engine,
        VerifiedBackupBundle? bundle,
        string? databaseArchive,
        string host,
        int port,
        string user,
        string? password,
        string scratch,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct) => engine switch
        {
            BackupDbEngine.Postgres => await VerifyPostgresRestoreAsync(
                databaseArchive!, host, port, user, password, scratch, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            BackupDbEngine.MySql => await VerifyMySqlRestoreAsync(
                databaseArchive!, host, port, user, password, scratch, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            _ when bundle is not null => (true, "File archive integrity verified."),
            _ => (false, "No DB engine configured.")
        };

    private async Task<(bool Verified, string Message)> VerifyPostgresRestoreAsync(
        string archive,
        string host,
        int port,
        string user,
        string? password,
        string scratch,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        await DropScratchDatabaseAsync(
            BackupDbEngine.Postgres, host, port, user, password, scratch,
            timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        var create = await RunAsync(
            "createdb", ["-h", host, "-p", port.ToString(), "-U", user, scratch], PgEnv(password),
            timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (create.ExitCode != 0) return (false, "Could not create scratch database.");

        var restore = await RunAsync(
            "pg_restore", BackupCommandBuilder.PgRestore(host, port, user, scratch, archive), PgEnv(password),
            timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        return RestoreResult(restore);
    }

    private async Task<(bool Verified, string Message)> VerifyMySqlRestoreAsync(
        string archive,
        string host,
        int port,
        string user,
        string? password,
        string scratch,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        await RunAsync(
            "mysql", ["-h", host, "-P", port.ToString(), "-u", user, "-e", $"DROP DATABASE IF EXISTS {scratch}; CREATE DATABASE {scratch};"], MysqlEnv(password),
            timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        var restore = await RunAsync(
            "mysql", BackupCommandBuilder.MysqlRestore(host, port, user, scratch), MysqlEnv(password),
            timeoutSeconds, onOutput, ct, stdinFile: archive).ConfigureAwait(false);
        return RestoreResult(restore);
    }

    private static (bool Verified, string Message) RestoreResult(ExecutorResult restore) =>
        restore.ExitCode == 0
            ? (true, "Database restore verified.")
            : (false, $"Restore failed (exit {restore.ExitCode}).");

    private async Task DropScratchDatabaseAsync(
        BackupDbEngine engine,
        string host,
        int port,
        string user,
        string? password,
        string scratch,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (engine == BackupDbEngine.Postgres)
            await RunAsync("dropdb", ["-h", host, "-p", port.ToString(), "-U", user, "--if-exists", scratch], PgEnv(password), timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        else if (engine == BackupDbEngine.MySql)
            await RunAsync("mysql", ["-h", host, "-P", port.ToString(), "-u", user, "-e", $"DROP DATABASE IF EXISTS {scratch};"], MysqlEnv(password), timeoutSeconds, onOutput, ct).ConfigureAwait(false);
    }

    private static string? GetLiveRestoreRefusal(
        BackupDbEngine engine,
        VerifiedBackupBundle? bundle,
        string databaseName,
        string databaseUser)
    {
        if (engine == BackupDbEngine.None && bundle is null)
            return "Restore refused: legacy archive has no database engine and no file manifest.";
        return engine != BackupDbEngine.None
            && (string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(databaseUser))
                ? "Database restore refused: database name or user is missing."
                : null;
    }

    private async Task<ExecutorResult> RestoreDatabaseAsync(
        BackupDbEngine engine,
        string? archive,
        string host,
        int port,
        string name,
        string user,
        string? password,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct) => engine switch
        {
            BackupDbEngine.None => new ExecutorResult(0, false),
            BackupDbEngine.Postgres => await RunAsync(
                "pg_restore", BackupCommandBuilder.PgRestore(host, port, user, name, archive!), PgEnv(password),
                timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            BackupDbEngine.MySql => await RunAsync(
                "mysql", BackupCommandBuilder.MysqlRestore(host, port, user, name), MysqlEnv(password),
                timeoutSeconds, onOutput, ct, stdinFile: archive).ConfigureAwait(false),
            _ => new ExecutorResult(1, false)
        };

    private async Task<ExecutorResult> DumpDatabaseAsync(
        BackupDbEngine engine,
        string output,
        string host,
        int port,
        string name,
        string user,
        string? password,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct) => engine switch
        {
            BackupDbEngine.Postgres => await RunAsync(
                "pg_dump", BackupCommandBuilder.PgDump(host, port, user, name, output), PgEnv(password),
                timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            BackupDbEngine.MySql => await RunAsync(
                "mysqldump", BackupCommandBuilder.MysqlDump(host, port, user, name, output), MysqlEnv(password),
                timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            _ => new ExecutorResult(0, false)
        };

    private async Task<bool> TryRollbackDatabaseAsync(
        BackupDbEngine engine,
        string? rollbackDump,
        string host,
        int port,
        string name,
        string user,
        string? password,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (engine == BackupDbEngine.None) return true;
        if (string.IsNullOrEmpty(rollbackDump) || !File.Exists(rollbackDump))
        {
            await onOutput("CRITICAL: database compensation snapshot is unavailable.", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }

        await onOutput("Restore failed after database mutation; compensating with the pre-restore snapshot.", TaskLogLevel.Warning).ConfigureAwait(false);
        ExecutorResult rollback;
        try
        {
            rollback = await RestoreDatabaseAsync(
                engine, rollbackDump, host, port, name, user, password,
                timeoutSeconds, onOutput, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database compensation process failed to start; snapshot retained at {Path}", rollbackDump);
            await onOutput(
                $"CRITICAL: database compensation could not run; snapshot retained at {rollbackDump}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        if (rollback.ExitCode == 0)
        {
            await onOutput("Database compensation completed.", TaskLogLevel.Warning).ConfigureAwait(false);
            return true;
        }

        await onOutput(
            $"CRITICAL: database compensation failed (exit {rollback.ExitCode}); snapshot retained at {rollbackDump}.",
            TaskLogLevel.Error).ConfigureAwait(false);
        return false;
    }

    private Task<ExecutorResult> RunAsync(
        string fileName, IReadOnlyList<string> argv, (string Key, string Value)? extraEnv,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct,
        string? stdinFile = null) =>
        BackupOperationHelpers.RunProcessAsync(
            ProcessInvoker, logger, fileName, argv, extraEnv,
            timeoutSeconds, onOutput, ct, stdinFile);

    internal static IReadOnlyList<FileInfo> GetArchivesToPrune(string directory, int keep)
        => BackupOperationHelpers.GetArchivesToPrune(directory, keep);
}
