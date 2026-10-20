// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Recette R1-43: the expand/contract gate of the migrate step ends every path with one
/// <c>##aetheus[migration-gate]</c> line carrying its verdict in JSON, which the control plane keeps
/// against the run. Driven through the real executor, with a shell that answers the psql history reads.
/// </summary>
public sealed class BlueGreenMigrationGateReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bg-gate-report-{Guid.NewGuid():N}");
    private readonly string _migrations;

    public BlueGreenMigrationGateReportTests()
    {
        Directory.CreateDirectory(_root);
        _migrations = Path.Combine(_root, "Migrations");
        Directory.CreateDirectory(_migrations);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Refused_ReportsEachPendingMigrationWithItsViolations()
    {
        WriteMigration("20260101000000_Base", "migrationBuilder.CreateTable(name: \"T\");");
        WriteMigration("20260102000000_AddNote", "migrationBuilder.AddColumn<string>(name: \"Note\", table: \"T\", nullable: true);");
        WriteMigration("20260103000000_DropOld", "migrationBuilder.DropColumn(name: \"Old\", table: \"T\");");
        var log = new List<string>();

        var result = await Execute(log, new HistoryShell(["20260101000000_Base"]));

        Assert.Equal(1, result.ExitCode);
        var report = SingleReport(log);
        Assert.Equal(MigrationGateOutcome.Refused, report.Outcome);
        Assert.Equal(2, report.PendingCount);
        Assert.Collection(report.Migrations,
            first =>
            {
                Assert.Equal("20260102000000_AddNote", first.MigrationId);
                Assert.True(first.Compatible);
                Assert.Empty(first.Violations);
            },
            second =>
            {
                Assert.Equal("20260103000000_DropOld", second.MigrationId);
                Assert.False(second.Compatible);
                Assert.Contains(second.Violations, v => v.Contains("not expand-compatible", StringComparison.Ordinal));
            });
        // The human log lines are still written: the directive adds to them, it replaces nothing.
        Assert.Contains(log, line => line.StartsWith("Pending migrations are not expand-compatible", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compatible_ReportsThePendingMigrationsAndLetsTheBundleRun()
    {
        WriteMigration("20260101000000_Base", "migrationBuilder.CreateTable(name: \"T\");");
        WriteMigration("20260102000000_AddNote", "migrationBuilder.AddColumn<string>(name: \"Note\", table: \"T\", nullable: true);");
        var log = new List<string>();

        var result = await Execute(log, new HistoryShell(["20260101000000_Base"]));

        Assert.Equal(0, result.ExitCode);
        var report = SingleReport(log);
        Assert.Equal(MigrationGateOutcome.Compatible, report.Outcome);
        Assert.Equal(1, report.PendingCount);
        Assert.True(Assert.Single(report.Migrations).Compatible);
        Assert.Contains(log, line => line.StartsWith("Schema reconciled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoPending_IsReportedWithoutMigrations()
    {
        WriteMigration("20260101000000_Base", "migrationBuilder.CreateTable(name: \"T\");");
        var log = new List<string>();

        var result = await Execute(log, new HistoryShell(["20260101000000_Base"]));

        Assert.Equal(0, result.ExitCode);
        var report = SingleReport(log);
        Assert.Equal(MigrationGateOutcome.NoPending, report.Outcome);
        Assert.Empty(report.Migrations);
    }

    [Fact]
    public async Task InitialCreation_ReportsTheCountOnly()
    {
        WriteMigration("20260101000000_Base", "migrationBuilder.CreateTable(name: \"T\");");
        WriteMigration("20260102000000_Second", "migrationBuilder.DropColumn(name: \"Old\", table: \"T\");");
        var log = new List<string>();

        var result = await Execute(log, new HistoryShell(applied: null));

        Assert.Equal(0, result.ExitCode);
        var report = SingleReport(log);
        Assert.Equal(MigrationGateOutcome.InitialCreation, report.Outcome);
        Assert.Equal(2, report.PendingCount);
        Assert.Empty(report.Migrations);
    }

    [Fact]
    public async Task MissingSources_ReportsUnavailableWithTheReason()
    {
        var env = BaseEnvironment();
        env["AETHEUS_BG_MIGRATIONS_DIR"] = Path.Combine(_root, "nowhere");
        var log = new List<string>();

        var result = await Execute(log, new HistoryShell([]), env);

        Assert.NotEqual(0, result.ExitCode);
        var report = SingleReport(log);
        Assert.Equal(MigrationGateOutcome.Unavailable, report.Outcome);
        Assert.Contains("cannot inspect what is about to run", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableHistory_ReportsUnavailable()
    {
        WriteMigration("20260101000000_Base", "migrationBuilder.CreateTable(name: \"T\");");
        var log = new List<string>();

        var result = await Execute(log, new HistoryShell([], historyFails: true));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(MigrationGateOutcome.Unavailable, SingleReport(log).Outcome);
    }

    [Fact]
    public void DirectiveLine_StaysOneLine()
    {
        var line = BlueGreenMigrationGateCheck.DirectiveLine(new MigrationGateReport
        {
            Outcome = MigrationGateOutcome.Refused,
            Migrations = [new MigrationGateMigrationVerdict { MigrationId = "X", Violations = ["a\nb"] }]
        });

        Assert.StartsWith(MigrationGateReport.Directive + "{", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("\"Refused\"", line, StringComparison.Ordinal);
    }

    private static MigrationGateReport SingleReport(List<string> log)
    {
        var line = Assert.Single(log, l => l.StartsWith(MigrationGateReport.Directive, StringComparison.Ordinal));
        return JsonSerializer.Deserialize<MigrationGateReport>(
            line[MigrationGateReport.Directive.Length..], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private void WriteMigration(string id, string upBody) => File.WriteAllText(
        Path.Combine(_migrations, id + ".cs"),
        "public partial class M : Migration\n{\n"
        + "    protected override void Up(MigrationBuilder migrationBuilder)\n    {\n"
        + "        " + upBody + "\n    }\n\n"
        + "    protected override void Down(MigrationBuilder migrationBuilder)\n    {\n    }\n}\n");

    private Task<ExecutorResult> Execute(List<string> log, IShellRunner shell, Dictionary<string, string>? env = null)
    {
        if (env is null)
        {
            env = BaseEnvironment();
            env["AETHEUS_BG_MIGRATIONS_DIR"] = _migrations;
        }
        var executor = new BlueGreenOperationExecutor(
            shell, Options.Create(new AetheusAgentOptions()), HttpFactory(), NullLogger<BlueGreenOperationExecutor>.Instance);
        return executor.ExecuteAsync(
            OperationKind.BlueGreenMigrate, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
    }

    private Dictionary<string, string> BaseEnvironment()
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

    private static IHttpClientFactory HttpFactory()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new FixedStatusHandler(HttpStatusCode.ServiceUnavailable)));
        return factory;
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>
    /// Answers Docker and Compose successfully and the two psql history reads with the given state:
    /// <paramref name="applied"/> null means no history table yet (initial creation).
    /// </summary>
    private sealed class HistoryShell(IReadOnlyList<string>? applied, bool historyFails = false) : IShellRunner
    {
        [Obsolete("Matches the interface; the executor never uses the shell form.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (args.Count > 0 && args[0] == "info")
                return Ok("27.0.0");
            if (!args.Contains("psql"))
                return Ok(string.Empty);
            if (historyFails)
                return Task.FromResult(new ShellExecResult(1, string.Empty, "connection refused"));
            var sql = args[^1];
            if (sql.Contains("to_regclass", StringComparison.Ordinal))
                return Ok(applied is null ? string.Empty : "\"__EFMigrationsHistory\"");
            return Ok(string.Join('\n', applied ?? []));
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null) => RunExecAsync(fileName, args, ct, timeout);

        private static Task<ShellExecResult> Ok(string stdout) =>
            Task.FromResult(new ShellExecResult(0, stdout, string.Empty));
    }
}
