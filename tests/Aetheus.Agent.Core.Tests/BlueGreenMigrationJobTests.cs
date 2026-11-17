// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A migration (an index build over a large table) can run for many minutes. The run log must show it
/// is in progress and not hung: the bundle's output streams live and a heartbeat names the elapsed time,
/// the migration being applied and the job container's state. Driven with a fake clock, so no test waits.
/// </summary>
public sealed class BlueGreenMigrationJobTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const string Migration = "20261112185811_VulnerabilityAffectedPackageEcosystemPatternIndex";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bg-migration-job-{Guid.NewGuid():N}");

    public BlueGreenMigrationJobTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Heartbeat_IsEmittedAtTheInterval_WithElapsedTimeAndTheMigrationBeingApplied()
    {
        var time = new FakeTimeProvider();
        var shell = new JobShell { ContainerStatus = "Up 4 minutes" };
        var log = new Log();
        var run = StartJob(time, shell, log);

        await shell.Started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await shell.EmitAsync($"Applying migration '{Migration}'.", isStandardError: false);

        time.Advance(Interval);
        await log.WaitForAsync("Migration still running:", count: 1);
        Assert.Contains(
            $"Migration still running: 30 s elapsed, applying {Migration}, job container: Up 4 minutes.", log.Lines);

        time.Advance(TimeSpan.FromSeconds(240));
        await log.WaitForAsync("Migration still running:", count: 2);
        Assert.Contains(
            log.Lines, line => line.StartsWith("Migration still running: 4 min 30 s elapsed", StringComparison.Ordinal));

        shell.Finish(new ShellExecResult(0, string.Empty, string.Empty));
        Assert.Equal(0, (await run).ExitCode);
    }

    [Fact]
    public async Task Heartbeat_BeforeAnyMigrationIsReported_SaysSo()
    {
        var time = new FakeTimeProvider();
        var shell = new JobShell { ContainerStatus = string.Empty };
        var log = new Log();
        var run = StartJob(time, shell, log);
        await shell.Started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        time.Advance(Interval);
        await log.WaitForAsync("Migration still running:", count: 1);

        Assert.Contains(
            "Migration still running: 30 s elapsed, no migration reported yet, job container: not found.", log.Lines);
        shell.Finish(new ShellExecResult(0, string.Empty, string.Empty));
        await run;
    }

    [Fact]
    public async Task Output_IsStreamedBeforeTheJobEnds_AndStderrIsAWarning()
    {
        var time = new FakeTimeProvider();
        var shell = new JobShell();
        var log = new Log();
        var run = StartJob(time, shell, log);
        await shell.Started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        await shell.EmitAsync($"Applying migration '{Migration}'.", isStandardError: false);
        await shell.EmitAsync("slow index build", isStandardError: true);

        Assert.False(run.IsCompleted, "the job must still be running while its output is already in the log");
        Assert.Contains(log.Entries, e => e.Line.StartsWith("Applying migration", StringComparison.Ordinal)
            && e.Level == TaskLogLevel.Info);
        Assert.Contains(log.Entries, e => e.Line == "slow index build" && e.Level == TaskLogLevel.Warning);
        shell.Finish(new ShellExecResult(0, string.Empty, string.Empty));
        await run;
    }

    [Fact]
    public async Task NoHeartbeat_IsEmittedOnceTheJobHasEnded()
    {
        var time = new FakeTimeProvider();
        var shell = new JobShell();
        var log = new Log();
        var run = StartJob(time, shell, log);
        await shell.Started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        time.Advance(Interval);
        await log.WaitForAsync("Migration still running:", count: 1);

        shell.Finish(new ShellExecResult(0, string.Empty, string.Empty));
        await run;
        var heartbeats = log.Lines.Count(l => l.StartsWith("Migration still running:", StringComparison.Ordinal));
        var lookups = shell.ContainerLookups;

        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(heartbeats, log.Lines.Count(l => l.StartsWith("Migration still running:", StringComparison.Ordinal)));
        Assert.Equal(lookups, shell.ContainerLookups);
    }

    [Fact]
    public async Task Failure_IsReturnedUnchanged_AndStopsTheHeartbeat()
    {
        var time = new FakeTimeProvider();
        var shell = new JobShell();
        var log = new Log();
        var run = StartJob(time, shell, log);
        await shell.Started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        shell.Finish(new ShellExecResult(1, "out", "Npgsql timeout"));
        var result = await run;
        time.Advance(Interval);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Npgsql timeout", result.StdErr);
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("Migration still running:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Executor_StreamsTheJobAndKeepsTheFailureMessage_WhenTheJobFails()
    {
        var shell = new ExecutorShell();
        var log = new List<string>();

        var result = await ExecuteMigrateAsync(shell, log);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Applying migration '{Migration}'.", log);
        Assert.Contains("Migration job failed: Npgsql timeout", log);
        Assert.DoesNotContain(log, line => line.StartsWith("Schema reconciled", StringComparison.Ordinal));
        Assert.Contains(shell.ComposeCommands, c => c.Contains("AETHEUS_MIGRATE_ONLY=true", StringComparison.Ordinal));
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private Task<ShellExecResult> StartJob(FakeTimeProvider time, JobShell shell, Log log)
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", Environment(), out var context, out var error, requireDatabase: false), error);
        var job = new BlueGreenMigrationJob(shell, new BlueGreenCompose(shell), time, Interval);
        return Task.Run(
            () => job.RunAsync(
                context!, "blue", ["--profile", "blue", "run", "--rm", "--no-deps", "back-blue"], 60, log.AddAsync,
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
    }

    private Task<ExecutorResult> ExecuteMigrateAsync(IShellRunner shell, List<string> log)
    {
        var migrations = Path.Combine(_root, "Migrations");
        Directory.CreateDirectory(migrations);
        File.WriteAllText(
            Path.Combine(migrations, "20260101000000_Base.cs"),
            "public partial class M : Migration\n{\n    protected override void Up(MigrationBuilder migrationBuilder)\n"
            + "    {\n        migrationBuilder.CreateTable(name: \"T\");\n    }\n}\n");
        var env = Environment();
        env["AETHEUS_BG_MIGRATIONS_DIR"] = migrations;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StatusHandler()));
        var executor = new BlueGreenOperationExecutor(
            shell, Options.Create(new AetheusAgentOptions()), factory, NullLogger<BlueGreenOperationExecutor>.Instance);
        return executor.ExecuteAsync(
            OperationKind.BlueGreenMigrate, "aetheus-demo", env, 60,
            (line, _) => { lock (log) log.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
    }

    private Dictionary<string, string> Environment()
    {
        var envFile = Path.Combine(_root, "stack.env");
        var composeFile = Path.Combine(_root, "compose.yml");
        if (!File.Exists(envFile))
            File.WriteAllText(envFile, "APPNAME=aetheus\nENV=demo\nDB_USER=aetheus\nDB_NAME=aetheus_demo\n");
        if (!File.Exists(composeFile)) File.WriteAllText(composeFile, "services: {}\n");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_BG_STATE_DIR"] = _root,
            ["AETHEUS_BG_ENV_FILE"] = envFile,
            ["AETHEUS_BG_COMPOSE_FILES"] = composeFile,
            ["AETHEUS_BG_PORT_FRONT_BLUE"] = "10029",
            ["AETHEUS_BG_PORT_BACK_BLUE"] = "10030",
            ["AETHEUS_BG_PORT_FRONT_GREEN"] = "10031",
            ["AETHEUS_BG_PORT_BACK_GREEN"] = "10032"
        };
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    /// <summary>Thread-safe run log, with a bounded real wait for a line to show up.</summary>
    private sealed class Log
    {
        private readonly Lock _gate = new();
        private readonly List<(string Line, TaskLogLevel Level)> _entries = [];

        internal IReadOnlyList<(string Line, TaskLogLevel Level)> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        internal IReadOnlyList<string> Lines => [.. Entries.Select(e => e.Line)];

        internal Task AddAsync(string line, TaskLogLevel level)
        {
            lock (_gate) _entries.Add((line, level));
            return Task.CompletedTask;
        }

        internal async Task WaitForAsync(string prefix, int count)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (Lines.Count(l => l.StartsWith(prefix, StringComparison.Ordinal)) < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"no '{prefix}' line #{count} in the log");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>The Compose job as a controllable process: it streams what the test emits and ends when told.</summary>
    private sealed class JobShell : IShellRunner
    {
        private readonly TaskCompletionSource<ShellExecResult> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<string, bool, Task>? _onLine;
        private int _lookups;

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string ContainerStatus { get; init; } = "Up 1 minute";

        internal int ContainerLookups => Volatile.Read(ref _lookups);

        internal Task EmitAsync(string line, bool isStandardError) => _onLine!(line, isStandardError);

        internal void Finish(ShellExecResult result) => _end.SetResult(result);

        [Obsolete("Matches the interface; the job never uses the shell form.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (args.Count > 0 && args[0] == "ps")
            {
                Interlocked.Increment(ref _lookups);
                return Task.FromResult(new ShellExecResult(0, ContainerStatus, string.Empty));
            }
            return Task.FromResult(new ShellExecResult(0, string.Empty, string.Empty));
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null) => throw new InvalidOperationException(
                "the migration job must use the streaming variant");

        public Task<ShellExecResult> RunExecStreamingAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            Func<string, bool, Task> onLine, CancellationToken ct, TimeSpan? timeout = null)
        {
            _onLine = onLine;
            Started.TrySetResult();
            return _end.Task;
        }
    }

    /// <summary>Docker, the psql history reads and a failing migration job, for the executor-level test.</summary>
    private sealed class ExecutorShell : IShellRunner
    {
        private readonly List<string> _compose = [];

        internal IReadOnlyList<string> ComposeCommands
        {
            get { lock (_compose) return [.. _compose]; }
        }

        [Obsolete("Matches the interface; the executor never uses the shell form.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (args.Count > 0 && args[0] == "info") return Ok("27.0.0");
            if (args.Contains("psql"))
                return Ok(args[^1].Contains("to_regclass", StringComparison.Ordinal)
                    ? "\"__EFMigrationsHistory\""
                    : "20260101000000_Base");
            return Ok(string.Empty);
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null) => RunExecAsync(fileName, args, ct, timeout);

        public async Task<ShellExecResult> RunExecStreamingAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            Func<string, bool, Task> onLine, CancellationToken ct, TimeSpan? timeout = null)
        {
            lock (_compose) _compose.Add(string.Join(' ', args));
            await onLine($"Applying migration '{Migration}'.", false).ConfigureAwait(false);
            await onLine("Npgsql timeout", true).ConfigureAwait(false);
            return new ShellExecResult(1, $"Applying migration '{Migration}'.", "Npgsql timeout");
        }

        private static Task<ShellExecResult> Ok(string stdout) =>
            Task.FromResult(new ShellExecResult(0, stdout, string.Empty));
    }
}
