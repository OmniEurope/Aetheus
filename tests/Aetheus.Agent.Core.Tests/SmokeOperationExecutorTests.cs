// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The smoke step exists to end an inconsistency: the three deployment paths each drew the
/// pass/fail line differently, and one of them let a slow readiness sample roll back a healthy
/// deployment. These tests pin the contract that replaced it.
/// </summary>
public sealed class SmokeOperationExecutorTests
{
    [Theory]
    [InlineData("https://demo.app.aetheus.sonytumen.com")]
    [InlineData("http://127.0.0.1:10030")]
    [InlineData("https://app.example.org:8443")]
    public void SmokeOrigin_AcceptsBareOrigins(string origin) =>
        Assert.True(OperationTargetValidator.IsValid(OperationKind.PipelineSmoke, origin));

    // The executor composes every probe path itself. Accepting a path, a query or credentials here
    // would let a step definition aim a probe at an endpoint the type never intended to expose.
    [Theory]
    [InlineData("https://demo.example.com/health/ready")]
    [InlineData("https://demo.example.com/?x=1")]
    [InlineData("https://user:pass@demo.example.com")]
    [InlineData("ftp://demo.example.com")]
    [InlineData("demo.example.com")]
    [InlineData("")]
    public void SmokeOrigin_RejectsAnythingBeyondAnOrigin(string origin) =>
        Assert.False(OperationTargetValidator.IsValid(OperationKind.PipelineSmoke, origin));

    [Fact]
    public void Samples_AreClampedIntoTheSupportedRange()
    {
        Assert.Equal(1, Parse("0"));
        Assert.Equal(50, Parse("999"));
        Assert.Equal(10, Parse("10"));
        // A malformed value falls back rather than failing: the probe still produces evidence.
        Assert.Equal(1, Parse("abc"));
        Assert.Equal(1, Parse(""));

        static int Parse(string raw) => SmokeOperationExecutor.ParseBounded(
            new Dictionary<string, string> { ["AETHEUS_SMOKE_SAMPLES"] = raw }, "AETHEUS_SMOKE_SAMPLES", 1, 1, 50);
    }

    [Fact]
    public void Budget_IsOptionalAndRejectsNonPositiveValues()
    {
        Assert.Equal(2.5, SmokeOperationExecutor.ParseBudget("2.5"));
        Assert.Null(SmokeOperationExecutor.ParseBudget(""));
        Assert.Null(SmokeOperationExecutor.ParseBudget("0"));
        Assert.Null(SmokeOperationExecutor.ParseBudget("-1"));
        Assert.Null(SmokeOperationExecutor.ParseBudget("fast"));
    }

    /// <summary>
    /// The control plane always writes the key, empty when the step declares no <c>filter:</c>, so a
    /// blank value must still restrict the suite. An empty <c>--filter</c> would run every E2E test
    /// against the production origin the cutover just switched traffic to.
    /// </summary>
    [Fact]
    public void BrowserFilter_FallsBackToTheProductionSmokeCategory()
    {
        Assert.Equal(SmokeOperationExecutor.DefaultBrowserFilter, Resolve([]));
        Assert.Equal(SmokeOperationExecutor.DefaultBrowserFilter, Resolve(new() { ["AETHEUS_SMOKE_FILTER"] = "" }));
        Assert.Equal(SmokeOperationExecutor.DefaultBrowserFilter, Resolve(new() { ["AETHEUS_SMOKE_FILTER"] = "   " }));
        Assert.Equal("TestCategory=Nightly", Resolve(new() { ["AETHEUS_SMOKE_FILTER"] = "TestCategory=Nightly" }));

        static string Resolve(Dictionary<string, string> envVars) =>
            SmokeOperationExecutor.ResolveBrowserFilter(envVars);
    }

    [Fact]
    public void Executor_HandlesOnlyTheSmokeOperation()
    {
        var executor = CreateExecutor(Substitute.For<IShellRunner>());

        Assert.True(executor.CanHandle(OperationKind.PipelineSmoke));
        Assert.False(executor.CanHandle(OperationKind.PipelineDeploy));
        Assert.False(executor.CanHandle(OperationKind.CertbotObtain));
    }

    [Fact]
    public async Task InvalidOrigin_FailsWithoutProbing()
    {
        var shell = Substitute.For<IShellRunner>();
        var executor = CreateExecutor(shell);

        var result = await executor.ExecuteAsync(
            OperationKind.PipelineSmoke, "https://demo.example.com/health", 60,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        await shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, CancellationToken.None);
    }

    [Fact]
    public void Gate_IsAdvisoryUnlessTheStepAsksForBlocking()
    {
        Assert.False(SmokeOperationExecutor.IsBlockingGate(new Dictionary<string, string>()));
        Assert.False(SmokeOperationExecutor.IsBlockingGate(new Dictionary<string, string> { ["AETHEUS_SMOKE_GATE"] = "advisory" }));
        Assert.True(SmokeOperationExecutor.IsBlockingGate(new Dictionary<string, string> { ["AETHEUS_SMOKE_GATE"] = "blocking" }));
        Assert.True(SmokeOperationExecutor.IsBlockingGate(new Dictionary<string, string> { ["AETHEUS_SMOKE_GATE"] = " Blocking " }));
    }

    // The default is what every existing caller gets, and it must stay evidence-only.
    [Fact]
    public async Task AnOriginThatDoesNotAnswer_IsRecordedButStillExitsZeroWhenAdvisory()
    {
        var log = new List<string>();

        var result = await CreateExecutor(Substitute.For<IShellRunner>()).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", new Dictionary<string, string>(), 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("##aetheus[setvariable name=SMOKE_GATE_STATUS]1", log);
        Assert.Contains("##aetheus[setvariable name=SMOKE_BLOCKING_FINDINGS]1", log);
    }

    /// <summary>
    /// Without this the rollback stage of a deployment template can never fire: a failed() stage only
    /// runs on a failed stage, so an evidence step that always exits 0 makes the compensation dead.
    /// </summary>
    [Fact]
    public async Task AnOriginThatDoesNotAnswer_FailsTheStepWhenGatedAsBlocking()
    {
        var log = new List<string>();

        var result = await CreateExecutor(Substitute.For<IShellRunner>()).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999",
            new Dictionary<string, string> { ["AETHEUS_SMOKE_GATE"] = "blocking" }, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        // The evidence is published before the verdict, so a failed step still carries its findings.
        Assert.Contains("##aetheus[setvariable name=SMOKE_GATE_STATUS]1", log);
        Assert.Contains(log, line => line.Contains("gated as blocking", StringComparison.Ordinal));
    }

    /// <summary>
    /// The regression this permanently forbids: a response-time budget once rolled back a demo that
    /// was answering correctly. A cold cache or a busy host is enough to blow the budget, so it stays
    /// a finding even when the step is gated as blocking.
    /// </summary>
    [Fact]
    public async Task AResponseTimeBudgetOverrun_NeverFailsTheStepEvenWhenGatedAsBlocking()
    {
        var log = new List<string>();

        // Readiness answers, so the application is serving; only the budget is blown.
        var result = await CreateExecutor(Substitute.For<IShellRunner>(), HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999",
            new Dictionary<string, string>
            {
                ["AETHEUS_SMOKE_GATE"] = "blocking",
                // Any real measurement exceeds this, so the overrun is deterministic.
                ["AETHEUS_SMOKE_MAX_SECONDS"] = "0.000000001"
            },
            60, (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("##aetheus[setvariable name=SMOKE_GATE_STATUS]1", log);
        Assert.Contains("##aetheus[setvariable name=SMOKE_BLOCKING_FINDINGS]0", log);
    }

    /// <summary>
    /// Docker answering 125/126/127, or a filter that selects nothing, ran no test at all: that is a
    /// step without evidence, never a suite graded as failing (which the advisory gate turns green).
    /// </summary>
    [Theory]
    [InlineData(125, "")]
    [InlineData(126, "")]
    [InlineData(127, "")]
    [InlineData(0, "No test matches the given testcase filter `TestCategory=ProductionSmoke`")]
    public async Task ABrowserSuiteThatRanNoTest_FailsTheStepInsteadOfBeingGraded(int dockerExitCode, string stdOut)
    {
        var shell = DockerShell();
        StreamingRun(shell).Returns(new ShellExecResult(dockerExitCode, stdOut, "docker: Error response from daemon"));
        var log = new List<string>();

        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", BrowserEnv(),
            60, (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.StartsWith("Browser suite did not run", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("SMOKE_GATE_STATUS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABrowserSuiteThatPasses_KeepsTheContractAndNeverTouchesTheContainer()
    {
        var shell = DockerShell();
        IReadOnlyList<string>? runArgs = null;
        StreamingRun(shell).Returns(call =>
        {
            runArgs = call.ArgAt<IReadOnlyList<string>>(1);
            return new ShellExecResult(0, "Passed!", string.Empty);
        });
        var log = new List<string>();

        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", BrowserEnv(),
            60, (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Browser suite passed.", log);
        Assert.Contains("##aetheus[setvariable name=SMOKE_GATE_STATUS]0", log);
        Assert.NotNull(runArgs);
        // The container runs under Docker's init (R-40: dotnet test as PID 1 never exits) and has a name.
        Assert.Equal(["run", "--rm", "--init", "--name"], runArgs.Take(4));
        Assert.Contains("TestCategory=ProductionSmoke", runArgs);
        Assert.Equal("Release", runArgs[runArgs.ToList().IndexOf("--configuration") + 1]);
        Assert.Equal("dotnet", runArgs[runArgs.ToList().IndexOf("dotnet")]);
        await shell.DidNotReceive().RunExecAsync(
            "docker", Arg.Is<IReadOnlyList<string>>(a => a[0] == "rm"), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task ABrowserSuiteFailure_IsGradedAsEvidenceNotAsAFault()
    {
        var shell = DockerShell();
        StreamingRun(shell).Returns(new ShellExecResult(1, "Failed!", string.Empty));
        var log = new List<string>();

        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", BrowserEnv(),
            60, (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Browser suite reported failing tests.", log);
        Assert.Contains("##aetheus[setvariable name=SMOKE_BLOCKING_FINDINGS]1", log);
    }

    [Fact]
    public async Task SuiteOutput_IsStreamedLineByLineAsItArrives()
    {
        var shell = DockerShell();
        var log = new List<(string Line, TaskLogLevel Level)>();
        var seenWhileRunning = new List<int>();
        StreamingRun(shell).Returns(async call =>
        {
            var onLine = call.ArgAt<Func<string, bool, Task>>(6);
            await onLine("first", false);
            seenWhileRunning.Add(log.Count(entry => entry.Line is "first"));
            await onLine("a warning", true);
            await onLine(string.Empty, false);
            await onLine("second", false);
            await onLine(new string('x', SmokeOperationExecutor.MaxLogLineLength + 10), false);
            return new ShellExecResult(0, "first\nsecond\n", "a warning\n");
        });

        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", BrowserEnv(),
            60, (line, level) => { log.Add((line, level)); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        // "first" was already in the step log while the suite was still running.
        Assert.Equal([1], seenWhileRunning);
        var suiteLines = log.Select(entry => entry.Line)
            .SkipWhile(line => line != "first").TakeWhile(line => line != "Browser suite passed.").ToList();
        Assert.Equal(["first", "a warning", "second"], suiteLines.Take(3));
        Assert.EndsWith(" …[truncated]", suiteLines[3], StringComparison.Ordinal);
        Assert.Contains(("a warning", TaskLogLevel.Warning), log);
        Assert.Contains(("first", TaskLogLevel.Info), log);
        // Nothing is emitted a second time from the final capture.
        Assert.Single(log, entry => entry.Line == "second");
    }

    [Fact]
    public async Task AHungSuite_IsAbortedAtItsOwnDelayAndItsNamedContainerIsRemoved()
    {
        var shell = DockerShell();
        IReadOnlyList<string>? runArgs = null;
        CancellationToken suiteToken = default;
        StreamingRun(shell).Returns(async call =>
        {
            runArgs = call.ArgAt<IReadOnlyList<string>>(1);
            suiteToken = call.ArgAt<CancellationToken>(7);
            await Task.Delay(Timeout.Infinite, suiteToken);
            return new ShellExecResult(0, string.Empty, string.Empty);
        });
        var removalCalls = new List<(IReadOnlyList<string> Args, CancellationToken Token)>();
        shell.RunExecAsync(
                "docker", Arg.Is<IReadOnlyList<string>>(a => a[0] == "rm"), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call =>
            {
                removalCalls.Add((call.ArgAt<IReadOnlyList<string>>(1), call.ArgAt<CancellationToken>(2)));
                return new ShellExecResult(0, "x", string.Empty);
            });
        var log = new List<string>();

        // The step allows 60 s; the suite is limited to 1 s by its own variable, which is what fires.
        var result = await CreateExecutor(shell, HttpStatusCode.OK, minTimeoutSeconds: 1).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999",
            BrowserEnv(new() { [SmokeOperationExecutor.SuiteTimeoutVariable] = "1", ["AETHEUS_RUN_ID"] = "3289", ["AETHEUS_TASK_ID"] = "77" }),
            60, (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(runArgs);
        var name = runArgs[runArgs.ToList().IndexOf("--name") + 1];
        Assert.Equal("aetheus-smoke-3289-77", name);
        var removal = Assert.Single(removalCalls);
        Assert.Equal(["rm", "-f", name], removal.Args);
        // The removal does not ride on the token that was just cancelled.
        Assert.False(removal.Token.IsCancellationRequested);
        Assert.Contains(log, line => line.Contains("exceeded its 1s delay", StringComparison.Ordinal)
            && line.Contains($"container {name} was removed", StringComparison.Ordinal));
        Assert.Contains("Browser smoke could not run; no evidence was produced.", log);
    }

    [Fact]
    public async Task ACancelledStep_RemovesItsNamedContainerAndStaysCancelled()
    {
        var shell = DockerShell();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        IReadOnlyList<string>? runArgs = null;
        StreamingRun(shell).Returns(async call =>
        {
            runArgs = call.ArgAt<IReadOnlyList<string>>(1);
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(7));
            return new ShellExecResult(0, string.Empty, string.Empty);
        });
        shell.RunExecAsync(
                "docker", Arg.Is<IReadOnlyList<string>>(a => a[0] == "rm"), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "x", string.Empty));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", BrowserEnv(),
            60, (_, _) => Task.CompletedTask, cancellation.Token));

        Assert.NotNull(runArgs);
        var name = runArgs[runArgs.ToList().IndexOf("--name") + 1];
        await shell.Received(1).RunExecAsync(
            "docker", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "rm", "-f", name })),
            CancellationToken.None, Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task AnUnremovableContainer_IsReportedLoudlyInsteadOfClaimedRemoved()
    {
        var shell = DockerShell();
        StreamingRun(shell).Returns<Task<ShellExecResult>>(_ => throw new OperationCanceledException());
        shell.RunExecAsync(
                "docker", Arg.Is<IReadOnlyList<string>>(a => a[0] == "rm"), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(1, string.Empty, "Error response from daemon: permission denied"));
        var log = new List<string>();

        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", BrowserEnv(),
            60, (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.Contains("could not be removed", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("was removed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, 3600, "limit 600s")]
    [InlineData(null, 120, "limit 120s")]
    [InlineData("45", 3600, "limit 45s")]
    [InlineData("5000", 3600, "limit 3600s")]
    [InlineData("junk", 3600, "limit 600s")]
    public async Task SuiteDelay_DefaultsToTenMinutesAndNeverExceedsTheStepTimeout(
        string? stepOverride, int stepTimeoutSeconds, string expected)
    {
        var shell = DockerShell();
        StreamingRun(shell).Returns(new ShellExecResult(0, string.Empty, string.Empty));
        var log = new List<string>();
        var env = BrowserEnv();
        if (stepOverride is not null) env[SmokeOperationExecutor.SuiteTimeoutVariable] = stepOverride;

        await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.PipelineSmoke, "http://127.0.0.1:19999", env,
            stepTimeoutSeconds, (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Contains(log, line => line.StartsWith("Running the browser suite", StringComparison.Ordinal)
            && line.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ContainerName_IsDeterministicFromTheTaskAndUsesDockersCharset()
    {
        var name = SmokeOperationExecutor.ResolveContainerName(
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "3289", ["AETHEUS_TASK_ID"] = "77" });

        Assert.Equal("aetheus-smoke-3289-77", name);
        Assert.Equal(name, SmokeOperationExecutor.ResolveContainerName(
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "3289", ["AETHEUS_TASK_ID"] = "77" }));

        var hostile = SmokeOperationExecutor.ResolveContainerName(new Dictionary<string, string>
        {
            ["AETHEUS_RUN_ID"] = "1; rm -rf /",
            ["AETHEUS_TASK_ID"] = new string('9', 100)
        });
        Assert.Matches("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", hostile);
        Assert.True(hostile.Length <= 100);
    }

    [Fact]
    public void ContainerName_WithoutATaskId_StaysUniqueAndValid()
    {
        var first = SmokeOperationExecutor.ResolveContainerName(new Dictionary<string, string>());
        var second = SmokeOperationExecutor.ResolveContainerName(new Dictionary<string, string>());

        Assert.NotEqual(first, second);
        Assert.Matches("^aetheus-smoke-[a-f0-9]{12}$", first);
    }

    private static Dictionary<string, string> BrowserEnv(Dictionary<string, string>? extra = null)
    {
        var env = new Dictionary<string, string>
        {
            ["AETHEUS_SMOKE_FRONTEND"] = "false",
            ["AETHEUS_SMOKE_BROWSER_IMAGE"] = "registry.local/browser-smoke@sha256:" + new string('a', 64)
        };
        foreach (var (key, value) in extra ?? []) env[key] = value;
        return env;
    }

    /// <summary>A docker that is reachable and holds the image; the suite run itself is scripted per test.</summary>
    private static IShellRunner DockerShell()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "27.0.0", string.Empty));
        return shell;
    }

    private static Task<ShellExecResult> StreamingRun(IShellRunner shell) =>
        shell.RunExecStreamingAsync(
            "docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<Func<string, bool, Task>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());

    private static SmokeOperationExecutor CreateExecutor(
        IShellRunner shell, HttpStatusCode probeStatus = HttpStatusCode.ServiceUnavailable, int? minTimeoutSeconds = null) =>
        new(shell,
            Options.Create(minTimeoutSeconds is { } min ? new AetheusAgentOptions { MinTimeoutSeconds = min } : new AetheusAgentOptions()),
            HttpFactory(probeStatus),
            NullLogger<SmokeOperationExecutor>.Instance);

    /// <summary>
    /// A factory whose client answers every probe with one status. Agent tests may not open a
    /// listening socket - a Windows firewall prompt would stall the run - so the probes are exercised
    /// through the handler the executor is given rather than against a real origin.
    /// </summary>
    private static IHttpClientFactory HttpFactory(HttpStatusCode status)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new FixedStatusHandler(status)));
        return factory;
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
