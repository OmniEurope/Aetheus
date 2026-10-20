// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A first deployment that already moved traffic cannot be rolled back: there is no previous colour
/// to restore, and stopping the only one that serves would be an outage dressed up as a rollback. The
/// executor refused that case correctly and told the operator to "retire it explicitly" - but nothing
/// could, so the journal went on rejecting every later deployment with "An unfinished deployment
/// transaction is present (state=SWITCHED)". Resolving it meant editing a live host by hand.
///
/// These pin the operation that closes that gap, and the symmetry that keeps it honest: retiring is
/// for the case a rollback refuses, and refuses the case a rollback handles.
/// </summary>
public sealed class BlueGreenRetireTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bg-retire-{Guid.NewGuid():N}");

    public BlueGreenRetireTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Executor_HandlesTheRetireOperation() =>
        Assert.True(CreateExecutor(Substitute.For<IShellRunner>()).CanHandle(OperationKind.BlueGreenRetire));

    [Fact]
    public void Retire_TakesTheComposeProjectAsItsTarget()
    {
        Assert.True(OperationTargetValidator.IsValid(OperationKind.BlueGreenRetire, "aetheus-prod"));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.BlueGreenRetire, "../escape"));
    }

    [Fact]
    public async Task NoOpenTransaction_IsNotAnError()
    {
        var result = await RunRetireAsync(WriteJournal: null);

        // Retiring an environment nobody deployed to must be safe to run, so a recovery procedure can
        // be re-run without having to guess whether it already worked.
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task SwitchedInitialDeployment_IsUndoneAndTheTransactionClosed()
    {
        var conf = Path.Combine(_root, "upstream.conf");
        File.WriteAllText(conf, "Define AETHEUS_FRONT_PORT 1\n");

        var result = await RunRetireAsync(
            WriteJournal: dir =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "state"), "SWITCHED");
                File.WriteAllText(Path.Combine(dir, "previous-live"), "none");
                File.WriteAllText(Path.Combine(dir, "idle"), "blue");
                File.WriteAllText(Path.Combine(dir, "upstream.before"), "# Active colour: none\n");
            },
            confPath: conf);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "deployment-transaction")));
        // The recorded configuration is what goes back, not something reconstructed.
        Assert.Equal("# Active colour: none\n", File.ReadAllText(conf));
        // No colour serves after a retirement; a stale record would make the next deployment believe
        // it has a live predecessor to fall back to.
        Assert.False(File.Exists(Path.Combine(_root, "live-color")));
    }

    [Fact]
    public async Task SwitchedInitialDeployment_WithNoIdleColour_ClosesTheTransactionWithoutStoppingAnything()
    {
        var conf = Path.Combine(_root, "upstream.conf");
        File.WriteAllText(conf, "Define AETHEUS_FRONT_PORT 1\n");
        var messages = new List<string>();

        var result = await RunRetireAsync(
            WriteJournal: dir =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "state"), "SWITCHED");
                File.WriteAllText(Path.Combine(dir, "previous-live"), "none");
                File.WriteAllText(Path.Combine(dir, "idle"), "none");
                File.WriteAllText(Path.Combine(dir, "upstream.before"), "# Active colour: none\n");
            },
            confPath: conf,
            onOutput: messages.Add,
            stopExitCode: 1);

        // A stop that Docker would refuse is never attempted: there is no colour to stop.
        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "deployment-transaction")));
        Assert.Contains(messages, m => m.Contains("colour none is stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpstreamRestoreFailing_KeepsTheTransactionAndStopsNothing()
    {
        var conf = Path.Combine(_root, "upstream.conf");
        File.WriteAllText(conf, "Define AETHEUS_FRONT_PORT 1\n");
        var messages = new List<string>();

        var result = await RunRetireAsync(
            WriteJournal: dir =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "state"), "SWITCHED");
                File.WriteAllText(Path.Combine(dir, "previous-live"), "none");
                File.WriteAllText(Path.Combine(dir, "idle"), "blue");
                File.WriteAllText(Path.Combine(dir, "upstream.before"), "# Active colour: none\n");
            },
            confPath: conf,
            onOutput: messages.Add,
            reloadExitCode: 1);

        // Traffic could not be moved off the colour, so stopping it would be an outage: the journal
        // stays for the manual intervention the message asks for.
        Assert.Equal(1, result.ExitCode);
        Assert.True(Directory.Exists(Path.Combine(_root, "deployment-transaction")));
        Assert.Contains(messages, m => m.Contains("manual intervention required", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("BLUEGREEN_RETIRED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopRefusedByDocker_KeepsTheTransactionAndFails()
    {
        var conf = Path.Combine(_root, "upstream.conf");
        File.WriteAllText(conf, "Define AETHEUS_FRONT_PORT 1\n");
        var messages = new List<string>();

        var result = await RunRetireAsync(
            WriteJournal: dir =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "state"), "SWITCHED");
                File.WriteAllText(Path.Combine(dir, "previous-live"), "none");
                File.WriteAllText(Path.Combine(dir, "idle"), "blue");
                File.WriteAllText(Path.Combine(dir, "upstream.before"), "# Active colour: none\n");
            },
            confPath: conf,
            onOutput: messages.Add,
            stopExitCode: 1);

        // Containers that are still running must not be announced as retired, and the journal that
        // records what remains to stop must survive for the retry.
        Assert.Equal(1, result.ExitCode);
        Assert.True(Directory.Exists(Path.Combine(_root, "deployment-transaction")));
        Assert.DoesNotContain(messages, m => m.Contains("BLUEGREEN_RETIRED", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("could not be stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransactionWithAPreviousColour_IsRefusedBecauseThatIsARollback()
    {
        var conf = Path.Combine(_root, "upstream.conf");
        File.WriteAllText(conf, "Define AETHEUS_FRONT_PORT 1\n");
        var messages = new List<string>();

        var result = await RunRetireAsync(
            WriteJournal: dir =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "state"), "SWITCHED");
                File.WriteAllText(Path.Combine(dir, "previous-live"), "blue");
                File.WriteAllText(Path.Combine(dir, "idle"), "green");
                File.WriteAllText(Path.Combine(dir, "upstream.before"), "# Active colour: blue\n");
            },
            confPath: conf,
            onOutput: messages.Add);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("bluegreen-rollback", StringComparison.Ordinal));
        // Refused means untouched: the journal has to survive so the rollback can still read it.
        Assert.True(Directory.Exists(Path.Combine(_root, "deployment-transaction")));
    }

    [Fact]
    public async Task MissingUpstreamBindings_AreRefusedRatherThanGuessed()
    {
        var messages = new List<string>();

        var result = await RunRetireAsync(
            WriteJournal: dir =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "state"), "SWITCHED");
                File.WriteAllText(Path.Combine(dir, "previous-live"), "none");
                File.WriteAllText(Path.Combine(dir, "idle"), "blue");
            },
            confPath: null,
            onOutput: messages.Add);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("AETHEUS_BG_UPSTREAM_CONF", StringComparison.Ordinal));
    }

    private async Task<ExecutorResult> RunRetireAsync(
        Action<string>? WriteJournal,
        string? confPath = null,
        Action<string>? onOutput = null,
        int stopExitCode = 0,
        int reloadExitCode = 0)
    {
        WriteJournal?.Invoke(Path.Combine(_root, "deployment-transaction"));

        var env = BaseEnvironment();
        if (confPath is not null)
        {
            env["AETHEUS_BG_UPSTREAM_CONF"] = confPath;
            env["AETHEUS_BG_RELOAD_HELPER"] = "/bin/true";
        }

        // A shell that answers the Docker probe, so a failure here is the executor's own decision
        // rather than the absence of Docker on the test machine.
        var shell = new RetireShell(stopExitCode, reloadExitCode);

        return await CreateExecutor(shell).ExecuteAsync(
            OperationKind.BlueGreenRetire, "aetheus-demo", env, 60,
            (message, _) => { onOutput?.Invoke(message); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
    }

    private Dictionary<string, string> BaseEnvironment()
    {
        var envFile = Path.Combine(_root, ".env");
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

    private static BlueGreenOperationExecutor CreateExecutor(IShellRunner shell)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new RetireStatusHandler(HttpStatusCode.ServiceUnavailable)));
        return new BlueGreenOperationExecutor(
            shell, Options.Create(new AetheusAgentOptions()), factory,
            NullLogger<BlueGreenOperationExecutor>.Instance);
    }

    private sealed class RetireStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>Answers the Docker probe and lets every other invocation succeed.</summary>
    private sealed class RetireShell(int stopExitCode = 0, int reloadExitCode = 0) : IShellRunner
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
            if (args.Count > 0 && args[0] == "info") return Task.FromResult(new ShellExecResult(0, "27.0.0", string.Empty));
            if (fileName == "sudo") return Task.FromResult(new ShellExecResult(reloadExitCode, string.Empty, "reload failed"));
            if (args.Contains("stop")) return Task.FromResult(new ShellExecResult(stopExitCode, string.Empty, "refused"));
            return Task.FromResult(new ShellExecResult(0, string.Empty, string.Empty));
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null) =>
            RunExecAsync(fileName, args, ct, timeout);
    }
}
