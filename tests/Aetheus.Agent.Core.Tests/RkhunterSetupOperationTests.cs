// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-119: the RKHunter install was an <c>apt-get install ... &amp;&amp; rkhunter ... &amp;&amp; sed -i</c> shell chain run
/// without root. It is the typed <see cref="OperationKind.RkhunterSetup"/> now: the argv-exact apt grant, then the
/// root-owned aetheus-rkhunter helper, the warning address on stdin, never on a command line.</summary>
public sealed class RkhunterSetupOperationTests
{
    private const string Helper = "/usr/local/lib/aetheus/aetheus-rkhunter";
    private readonly List<string> _runs = [];
    private readonly List<string?> _inputs = [];
    private readonly List<(string Message, TaskLogLevel Level)> _output = [];

    private RkhunterOperationExecutor Executor(Func<IReadOnlyList<string>, int>? exitCodes = null, bool isLinux = true) =>
        new(Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 10, MaxTimeoutSeconds = 1200 }),
            NullLogger<RkhunterOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunSetupStepAsync = (argv, standardInput, _, _, _) =>
            {
                _runs.Add(string.Join(' ', argv));
                _inputs.Add(standardInput);
                return Task.FromResult(new ExecutorResult(exitCodes?.Invoke(argv) ?? 0, false));
            }
        };

    private Task<ExecutorResult> SetupAsync(RkhunterOperationExecutor executor, Dictionary<string, string>? env = null, string target = "-") =>
        ((IOperationExecutor)executor).ExecuteAsync(OperationKind.RkhunterSetup, target, env ?? [], 900,
            (message, level) => { _output.Add((message, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Setup_InstallsUpdatesThenBuildsTheBaseline_InThatOrder()
    {
        var result = await SetupAsync(Executor());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "-n /usr/bin/apt-get update",
                "-n /usr/bin/apt-get install -y rkhunter",
                $"-n {Helper} update",
                $"-n {Helper} propupd",
            ],
            _runs);
        Assert.All(_inputs, input => Assert.Null(input));
    }

    [Fact]
    public async Task Setup_WithAnAddress_WritesItThroughTheHelper_OnStdin()
    {
        var result = await SetupAsync(Executor(), new Dictionary<string, string> { [RkhunterSetupEnv.MailOnWarning] = "admin@example.com" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"-n {Helper} mail-on-warning", _runs[^1]);
        Assert.Equal("admin@example.com\n", _inputs[^1]);
        Assert.DoesNotContain(_runs, run => run.Contains("admin@example.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("admin@example.com\n")]
    [InlineData("admin@example.com\"; reboot; \"")]
    [InlineData("not-an-email")]
    public async Task Setup_RefusesAMalformedAddress_BeforeRunningAnything(string address)
    {
        var result = await SetupAsync(Executor(), new Dictionary<string, string> { [RkhunterSetupEnv.MailOnWarning] = address });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task Setup_StopsWhenTheInstallFails()
    {
        var result = await SetupAsync(Executor(argv => argv.Contains("install") ? 100 : 0));

        Assert.Equal(100, result.ExitCode);
        Assert.DoesNotContain(_runs, run => run.StartsWith($"-n {Helper}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setup_ADataUpdateFailure_IsReported_AndTheBaselineIsStillBuilt()
    {
        var result = await SetupAsync(Executor(argv => argv[^1] == "update" && argv[1] == Helper ? 1 : 0));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"-n {Helper} propupd", _runs);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Warning && line.Message.Contains("exit code 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setup_FailsWhenTheBaselineFails()
    {
        var result = await SetupAsync(Executor(argv => argv[^1] == "propupd" ? 2 : 0),
            new Dictionary<string, string> { [RkhunterSetupEnv.MailOnWarning] = "admin@example.com" });

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain($"-n {Helper} mail-on-warning", _runs);
    }

    [Fact]
    public async Task Setup_RefusesAnyOtherTarget_AndIsLinuxOnly()
    {
        Assert.NotEqual(0, (await SetupAsync(Executor(), target: "rkhunter")).ExitCode);
        Assert.NotEqual(0, (await SetupAsync(Executor(isLinux: false))).ExitCode);
        Assert.Empty(_runs);
    }

    [Fact]
    public void RkhunterSetup_IsHandled_AndNeedsTheRkhunterSetupCapability()
    {
        Assert.True(Executor().CanHandle(OperationKind.RkhunterSetup));
        Assert.Equal(AgentCapabilities.RkhunterSetup, AgentCapabilities.RequiredFor(OperationKind.RkhunterSetup));
        Assert.Contains(AgentCapabilities.RkhunterSetup, AgentCapabilities.SoftwareCapabilities);
    }

    /// <summary>The rkhunter drop-in names the new helper word exactly; the address never reaches sudo's argv.</summary>
    [Fact]
    public void TheRkhunterGrant_NamesTheMailOnWarningWord_WithoutAWildcard()
    {
        var dropIn = LinuxHostConfigTemplates.Read("rkhunter/sudoers.d/aetheus-rkhunter");
        Assert.Contains("#{RKHUNTER_HELPER_PATH}# mail-on-warning", dropIn, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-on-warning *", dropIn, StringComparison.Ordinal);
        var helper = LinuxHostConfigTemplates.Read("rkhunter/aetheus-rkhunter");
        Assert.Contains("mail-on-warning)", helper, StringComparison.Ordinal);
    }
}
