// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A <c>type: aetheus</c> step of a container-isolated stage: the task is checked on the host, then
/// <c>aetheus-fn</c> runs inside the step container, mounted read-only, with the container workspace,
/// and its exit code comes back as the same failure codes as on the host.
/// </summary>
public sealed class ContainerPipelineFunctionExecutorTests : IDisposable
{
    private const string Function = "detect-project-languages";

    private static readonly ContainerSpec Spec = new() { Image = "node@sha256:abc", Shell = "sh", WorkspaceKey = 42 };

    private readonly string _functionsDirectory =
        Path.Combine(Path.GetTempPath(), $"aetheus-fn-exec-{Guid.NewGuid():N}");
    private readonly IContainerExecutor _container = Substitute.For<IContainerExecutor>();
    private readonly List<(string Line, TaskLogLevel Level)> _log = [];

    public ContainerPipelineFunctionExecutorTests()
    {
        foreach (var executable in ContainerPipelineFunctionExecutor.Executables)
        {
            var path = Path.Combine(_functionsDirectory, executable);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "binary");
        }
        ContainerReturns(new ExecutorResult(0, false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_functionsDirectory))
            Directory.Delete(_functionsDirectory, recursive: true);
    }

    private void ContainerReturns(ExecutorResult result) =>
        _container.ExecuteAsync(
                Arg.Any<ContainerSpec>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<ContainerReadOnlyMount>?>())
            .Returns(result);

    private ContainerPipelineFunctionExecutor Executor(params IPipelineFunction[] functions) =>
        new(functions.Length == 0 ? PipelineFunctions.CreateAll() : functions, _container, _functionsDirectory,
            PipelineFunctionCatalog.Find);

    private Task Capture(string line, TaskLogLevel level)
    {
        lock (_log)
            _log.Add((line, level));
        return Task.CompletedTask;
    }

    private static Dictionary<string, string> Env(string parametersJson = """{"root":"src"}""") => new()
    {
        ["WORKSPACE"] = "/w",
        [PipelineFunctionVariables.ParametersJson] = parametersJson,
        ["AETHEUS_RUN_ID"] = "42"
    };

    private Task<ExecutorResult> RunAsync(
        ContainerPipelineFunctionExecutor executor, Dictionary<string, string>? env = null,
        string target = Function, CancellationToken? token = null) =>
        executor.ExecuteAsync(
            Spec, OperationKind.PipelineFunction, target, env ?? Env(), 120, Capture,
            token ?? TestContext.Current.CancellationToken);

    private Task NoContainerStarted() =>
        _container.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!, default!, default!, default, default!, TestContext.Current.CancellationToken, default);

    [Fact]
    public async Task TheFunctionRunsInsideTheStepContainerWithAetheusFnMountedReadOnly()
    {
        var result = await RunAsync(Executor());

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.FailureCode);
        await _container.Received(1).ExecuteAsync(
            Spec,
            ContainerPipelineFunctionExecutor.BuildCommand(Function),
            Arg.Any<Dictionary<string, string>>(),
            120,
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyList<ContainerReadOnlyMount>?>(mounts =>
                mounts != null && mounts.Count == 1
                && mounts[0].HostPath == _functionsDirectory
                && mounts[0].ContainerPath == "/opt/aetheus-fn"));
    }

    [Fact]
    public async Task TheContainerSeesTheTaskEnvironmentWithTheContainerWorkspace()
    {
        var env = Env();
        env["WORKSPACE"] = "/var/lib/aetheus-agent/w/0000002a/s";

        await RunAsync(Executor(), env);

        await _container.Received(1).ExecuteAsync(
            Arg.Any<ContainerSpec>(),
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string>>(seen =>
                seen["WORKSPACE"] == "/w"
                && seen[PipelineFunctionVariables.ParametersJson] == """{"root":"src"}"""
                && seen["AETHEUS_RUN_ID"] == "42"),
            Arg.Any<int>(),
            Arg.Any<Func<string, TaskLogLevel, Task>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<ContainerReadOnlyMount>?>());
        // The task's own dictionary is not rewritten.
        Assert.Equal("/var/lib/aetheus-agent/w/0000002a/s", env["WORKSPACE"]);
    }

    [Fact]
    public void TheStepScriptPicksTheExecutableForTheContainerCLibraryAndQuotesTheName()
    {
        var command = ContainerPipelineFunctionExecutor.BuildCommand(Function);

        Assert.Contains("[ -e /lib/ld-musl-x86_64.so.1 ]", command, StringComparison.Ordinal);
        Assert.Contains("fn=/opt/aetheus-fn/linux-musl-x64/aetheus-fn", command, StringComparison.Ordinal);
        Assert.Contains("fn=/opt/aetheus-fn/linux-x64/aetheus-fn", command, StringComparison.Ordinal);
        Assert.EndsWith("exec \"$fn\" 'detect-project-languages'", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x'; rm -rf /w; '")]
    [InlineData("a\nb")]
    [InlineData(" ")]
    public void AStepScriptIsNeverBuiltFromAnUnquotableName(string function) =>
        Assert.ThrowsAny<ArgumentException>(() => ContainerPipelineFunctionExecutor.BuildCommand(function));

    [Fact]
    public async Task AnUnknownFunctionIsAnInvalidRequestAndStartsNoContainer()
    {
        var result = await RunAsync(Executor(), target: "no-such-function");

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
        Assert.Equal(1, result.ExitCode);
        await NoContainerStarted();
    }

    [Fact]
    public async Task BadParametersAreAnInvalidRequestAndStartNoContainer()
    {
        var result = await RunAsync(Executor(), Env("not json"));

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
        await NoContainerStarted();
    }

    [Fact]
    public async Task ACatalogFunctionTheAgentDoesNotImplementIsNotImplemented()
    {
        var other = Substitute.For<IPipelineFunction>();
        other.Name.Returns("something-else");

        var result = await RunAsync(Executor(other));

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionNotImplemented, result.FailureCode);
        await NoContainerStarted();
    }

    [Fact]
    public async Task AnAgentWithoutAetheusFnIsNotImplementedAndSaysWhatIsMissing()
    {
        File.Delete(Path.Combine(_functionsDirectory, "linux-musl-x64", "aetheus-fn"));

        var result = await RunAsync(Executor());

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionNotImplemented, result.FailureCode);
        Assert.Contains("linux-musl-x64/aetheus-fn", result.FailureReason, StringComparison.Ordinal);
        await NoContainerStarted();
    }

    [Theory]
    [InlineData(AetheusFnExitCodes.InvalidRequest, PipelineFunctionOperationExecutor.InvalidRequest)]
    [InlineData(AetheusFnExitCodes.FunctionNotImplemented, PipelineFunctionOperationExecutor.FunctionNotImplemented)]
    [InlineData(AetheusFnExitCodes.FunctionError, PipelineFunctionOperationExecutor.FunctionError)]
    [InlineData(127, PipelineFunctionOperationExecutor.FunctionError)]
    [InlineData(137, PipelineFunctionOperationExecutor.FunctionError)]
    public async Task AnAetheusFnExitCodeBecomesTheHostFailureCode(int exitCode, string expected)
    {
        ContainerReturns(new ExecutorResult(exitCode, false));

        var result = await RunAsync(Executor());

        Assert.Equal(expected, result.FailureCode);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(_log, entry => entry.Level == TaskLogLevel.Error && entry.Line == result.FailureReason);
    }

    [Fact]
    public async Task AnExecutableThatCannotStartNamesTheLikelyCause()
    {
        ContainerReturns(new ExecutorResult(127, false));

        var result = await RunAsync(Executor());

        Assert.Contains("could not start in the step image", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutIsReportedAsATimeoutNotAsAFunctionError()
    {
        ContainerReturns(new ExecutorResult(-1, true));

        var result = await RunAsync(Executor());

        Assert.True(result.TimedOut);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task AnInfrastructureFailureKeepsItsOwnCode()
    {
        ContainerReturns(new ExecutorResult(86, false, TaskFailureCodes.InfrastructureMismatch, "Toolchain mismatch."));

        var result = await RunAsync(Executor());

        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, result.FailureCode);
        Assert.Equal("Toolchain mismatch.", result.FailureReason);
    }

    [Fact]
    public async Task AnExternalCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _container.ExecuteAsync(
                Arg.Any<ContainerSpec>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<ContainerReadOnlyMount>?>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(Executor(), token: cancellation.Token));
    }

    [Theory]
    [InlineData(OperationKind.PipelineFunction, true)]
    [InlineData(OperationKind.PipelineDeploymentFunction, true)]
    [InlineData(OperationKind.AiRun, false)]
    [InlineData(OperationKind.None, false)]
    public void OnlyTheFunctionKindsAreRoutedHere(OperationKind kind, bool expected) =>
        Assert.Equal(expected, ContainerPipelineFunctionExecutor.Handles(kind));
}
