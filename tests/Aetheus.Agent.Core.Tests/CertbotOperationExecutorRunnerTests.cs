// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Certificate issuing and management run through an injected process runner, so the argv of the
/// two root-owned helpers, the environment that carries the domains, the mode check, the target validation
/// and the platform refusal are asserted on any platform without sudo or certbot.</summary>
public class CertbotOperationExecutorRunnerTests
{
    private const string IssueHelper = "/usr/local/lib/aetheus/aetheus-certbot-issue";
    private const string ManageHelper = "/usr/local/lib/aetheus/aetheus-certbot-manage";

    private sealed class EnvironmentCapture
    {
        public Dictionary<string, string?> Last { get; private set; } = [];

        public Task<ExecutorResult> Run(
            ProcessStartInfo startInfo, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput,
            ILogger logger, CancellationToken cancellationToken)
        {
            Last = startInfo.Environment.ToDictionary(e => e.Key, e => e.Value);
            return Task.FromResult(new ExecutorResult(0, false));
        }
    }

    private static CertbotOperationExecutor Build(ProcessRunCalls calls, bool isLinux = true) =>
        new(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }),
            NullLogger<CertbotOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunProcessAsync = calls.RunAsync
        };

    private static Task<ExecutorResult> Obtain(
        CertbotOperationExecutor executor, string domain, Dictionary<string, string> env, OutputLog log, int timeout = 60) =>
        executor.ExecuteAsync(
            OperationKind.CertbotObtain, domain, env, timeout, log.Write, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Obtain_RunsTheIssueHelperForTheDomain_AnnouncesTheRealAcmeRequest()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Obtain(Build(calls), "app.example.org", [], log);

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", IssueHelper, "app.example.org"], run.Argv);
        Assert.True(log.Has("Requesting certificate for app.example.org (real ACME via Apache webroot).", TaskLogLevel.Info));
    }

    [Fact]
    public async Task Obtain_CarriesDomainsEmailAndModeInTheEnvironment_NeverInTheArgv()
    {
        var capture = new EnvironmentCapture();
        var executor = new CertbotOperationExecutor(
            Options.Create(new AetheusAgentOptions()), NullLogger<CertbotOperationExecutor>.Instance)
        {
            IsLinux = true,
            RunProcessAsync = capture.Run
        };
        var env = new Dictionary<string, string>
        {
            ["AETHEUS_CERTBOT_DOMAINS"] = "app.example.org,www.example.org",
            ["AETHEUS_CERTBOT_EMAIL"] = "ops@example.org",
            ["AETHEUS_CERTBOT_MODE"] = "local"
        };
        var log = new OutputLog();

        await Obtain(executor, "app.example.org", env, log);

        Assert.Equal("app.example.org,www.example.org", capture.Last["AETHEUS_CERTBOT_DOMAINS"]);
        Assert.Equal("ops@example.org", capture.Last["AETHEUS_CERTBOT_EMAIL"]);
        Assert.Equal("local", capture.Last["AETHEUS_CERTBOT_MODE"]);
        Assert.True(log.Has("Creating local certificate for app.example.org (ACME disabled).", TaskLogLevel.Info));
    }

    [Fact]
    public async Task Obtain_WithoutEnvironment_DefaultsToTheDomainAnEmptyEmailAndProductionMode()
    {
        var capture = new EnvironmentCapture();
        var executor = new CertbotOperationExecutor(
            Options.Create(new AetheusAgentOptions()), NullLogger<CertbotOperationExecutor>.Instance)
        {
            IsLinux = true,
            RunProcessAsync = capture.Run
        };

        await Obtain(executor, "app.example.org", [], new OutputLog());

        Assert.Equal("app.example.org", capture.Last["AETHEUS_CERTBOT_DOMAINS"]);
        Assert.Equal(string.Empty, capture.Last["AETHEUS_CERTBOT_EMAIL"]);
        Assert.Equal("production", capture.Last["AETHEUS_CERTBOT_MODE"]);
    }

    [Fact]
    public async Task Obtain_WithAnUnknownMode_IsRefusedWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Obtain(
            Build(calls), "app.example.org", new Dictionary<string, string> { ["AETHEUS_CERTBOT_MODE"] = "staging" }, log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("Invalid certificate mode 'staging'.", TaskLogLevel.Error));
    }

    [Theory]
    [InlineData("not a domain")]
    [InlineData("a.example.org; rm -rf /")]
    [InlineData("")]
    public async Task Obtain_WithAnInvalidDomain_IsRefusedWithoutRunningAnything(string domain)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Obtain(Build(calls), domain, [], log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("Invalid certbot target domain", TaskLogLevel.Error));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1000, 100)]
    [InlineData(30, 30)]
    public async Task TheTimeoutIsClampedToTheConfiguredBounds(int requested, int expected)
    {
        var calls = new ProcessRunCalls();

        await Obtain(Build(calls), "app.example.org", [], new OutputLog(), requested);
        await Build(calls).ExecuteAsync(
            OperationKind.CertbotRenewAll, "-", requested, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal([expected, expected], calls.Runs.Select(r => r.TimeoutSeconds));
    }

    [Theory]
    [InlineData(OperationKind.CertbotObtain)]
    [InlineData(OperationKind.CertbotRenew)]
    public async Task OutsideLinux_RefusesWithoutRunningAnything(OperationKind kind)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Build(calls, isLinux: false).ExecuteAsync(
            kind, "app.example.org", 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
    }

    [Theory]
    [InlineData(OperationKind.CertbotRenew, "renew")]
    [InlineData(OperationKind.CertbotDelete, "delete")]
    [InlineData(OperationKind.CertbotRevoke, "revoke")]
    public async Task ManagingALineage_RunsTheManageHelperWithTheVerbAndTheCertificateName(OperationKind kind, string verb)
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(2, false));
        var log = new OutputLog();

        var result = await Build(calls).ExecuteAsync(
            kind, "app.example.org", 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", ManageHelper, verb, "app.example.org"], run.Argv);
        Assert.True(log.Has($"certbot {verb} app.example.org", TaskLogLevel.Info));
    }

    [Theory]
    [InlineData(OperationKind.CertbotRenewAll, "renew-all")]
    [InlineData(OperationKind.CertbotNormalize, "normalize")]
    [InlineData(OperationKind.CertbotRenewalCheck, "renewal-check")]
    public async Task ManagingEveryLineage_RunsTheManageHelperWithTheVerbOnly(OperationKind kind, string verb)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        await Build(calls).ExecuteAsync(kind, "-", 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(["-n", ManageHelper, verb], Assert.Single(calls.Runs).Argv);
        Assert.True(log.Has($"certbot {verb} (all)", TaskLogLevel.Info));
    }

    [Fact]
    public async Task ManagingALineage_WithAnInvalidCertificateName_IsRefusedWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Build(calls).ExecuteAsync(
            OperationKind.CertbotDelete, "../../etc/passwd", 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("Invalid certbot certificate name", TaskLogLevel.Error));
    }

    [Fact]
    public async Task AKindThatIsNotACertbotOperation_IsRefusedWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();

        var result = await Build(calls).ExecuteAsync(
            OperationKind.ServiceStart, "nginx", 60, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
    }
}
