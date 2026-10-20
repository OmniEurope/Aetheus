// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// How a backup or restore tool is started: the injected invoker when there is one, otherwise a real process
/// with the database password in its environment (never on its argument list), and for a restore the dump
/// fed on standard input under a bounded wait. The tool is a real stand-in program.
/// </summary>
public sealed class BackupProcessRunTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aetheus-backup-process-").FullName;
    private readonly OutputLog _log = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<ExecutorResult> Run(
        string program, string? standardInputFile, int timeoutSeconds = 30, BackupProcessInvoker? invoker = null) =>
        BackupOperationHelpers.RunProcessAsync(
            invoker, NullLogger.Instance, program, ["--single-transaction", "shop"], ("PGPASSWORD", "s3cret"),
            timeoutSeconds, _log.Write, TestContext.Current.CancellationToken, standardInputFile);

    [Fact]
    public async Task AnInjectedInvoker_ReceivesTheWholeRequest_AndNoProcessStarts()
    {
        string[]? seen = null;
        BackupProcessInvoker invoker = (fileName, argv, environment, timeout, _, _, input) =>
        {
            seen = [fileName, .. argv, environment?.Key ?? "", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture), input ?? ""];
            return Task.FromResult(new ExecutorResult(5, false));
        };

        var result = await Run("pg_dump-that-does-not-exist", "dump.sql", 30, invoker);

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(seen);
        Assert.Equal(["pg_dump-that-does-not-exist", "--single-transaction", "shop", "PGPASSWORD", "30", "dump.sql"], seen);
    }

    [Fact]
    public async Task ADumpTool_SeesThePasswordInItsEnvironmentAndItsArguments_AndItsExitCodeIsKept()
    {
        var program = StandInProgram.Write(_dir, "pg_dump",
            "echo password=%PGPASSWORD%\r\necho args=%*\r\nexit /b 4",
            "echo \"password=$PGPASSWORD\"\necho \"args=$*\"\nexit 4");

        var result = await Run(program, standardInputFile: null);

        Assert.Equal(4, result.ExitCode);
        Assert.True(_log.Has("password=s3cret", TaskLogLevel.Info));
        Assert.True(_log.Has("args=--single-transaction shop", TaskLogLevel.Info));
    }

    [Fact]
    public async Task ARestoreTool_ReadsTheDumpOnItsStandardInput_AndReportsItsOutputByStream()
    {
        var dump = Path.Combine(_dir, "dump.sql");
        await File.WriteAllTextAsync(dump, "INSERT INTO t VALUES (1);\n", TestContext.Current.CancellationToken);
        var program = StandInProgram.Write(_dir, "mysql",
            "findstr \"^\"\r\necho restored 1>&2",
            "cat\necho restored >&2");

        var result = await Run(program, dump);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.True(_log.Has("INSERT INTO t VALUES (1);", TaskLogLevel.Info));
        Assert.True(_log.Has("restored", TaskLogLevel.Error));
    }

    [Fact]
    public async Task ARestoreThatOutlivesItsTimeout_IsKilled_AndReportedAsTimedOut()
    {
        var dump = Path.Combine(_dir, "dump.sql");
        await File.WriteAllTextAsync(dump, "SELECT 1;\n", TestContext.Current.CancellationToken);
        var program = StandInProgram.Write(_dir, "mysql-hangs", "ping -n 60 127.0.0.1 >nul", "sleep 60");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Run(program, dump, timeoutSeconds: 1);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.TimedOut);
        Assert.True(_log.Has("mysql restore timed out or was cancelled.", TaskLogLevel.Error));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The restore was not killed: {watch.Elapsed}.");
    }
}
