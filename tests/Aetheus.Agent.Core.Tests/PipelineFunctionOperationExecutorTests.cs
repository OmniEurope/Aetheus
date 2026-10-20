// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Routing of <c>type: aetheus</c> tasks: the task is checked against the catalog, handed to the
/// registered function, bounded by its timeout, and its outputs published only on success. Every
/// failure carries an explicit code and a plain cause; none reads as success.
/// </summary>
public sealed class PipelineFunctionOperationExecutorTests
{
    private const string Workspace = "/work/repo";

    // A test-only contract: one required parameter, one optional, two outputs. The shared catalog has
    // no required parameter yet, so the executor is pointed at this one.
    private static readonly PipelineFunctionDescriptor BuildFunction = new(
        "fake-build",
        PipelineFunctionAccess.Build,
        "Test function.",
        [
            new PipelineFunctionParameter("project", Required: true, "Project."),
            new PipelineFunctionParameter("mode", Required: false, "Mode.")
        ],
        ["FAKE_STATUS", "FAKE_COUNT"]);

    private static readonly PipelineFunctionDescriptor DeploymentFunction = new(
        "fake-deploy", PipelineFunctionAccess.Deployment, "Test deployment function.", [], ["FAKE_DONE"]);

    // In the catalog, but no implementation is registered for it.
    private static readonly PipelineFunctionDescriptor UnimplementedFunction = new(
        "fake-missing", PipelineFunctionAccess.Build, "Never implemented.", [], []);

    private readonly List<(string Line, TaskLogLevel Level)> _log = [];

    private static PipelineFunctionDescriptor? Find(string? name) =>
        new[] { BuildFunction, DeploymentFunction, UnimplementedFunction }
            .FirstOrDefault(function => function.Name == name);

    private PipelineFunctionOperationExecutor Executor(params IPipelineFunction[] functions) =>
        new(functions, Find);

    private Task Capture(string line, TaskLogLevel level)
    {
        lock (_log)
            _log.Add((line, level));
        return Task.CompletedTask;
    }

    private static Dictionary<string, string> Env(string? parametersJson = """{"project":"src/App"}""")
    {
        var env = new Dictionary<string, string> { [PipelineFunctionRequest.WorkspaceVariable] = Workspace };
        if (parametersJson is not null)
            env[PipelineFunctionVariables.ParametersJson] = parametersJson;
        return env;
    }

    private Task<ExecutorResult> RunAsync(
        PipelineFunctionOperationExecutor executor,
        Dictionary<string, string> env,
        string target = "fake-build",
        OperationKind kind = OperationKind.PipelineFunction,
        int timeoutSeconds = 30) =>
        executor.ExecuteAsync(
            kind, target, env, timeoutSeconds, Capture,
            TestContext.Current.CancellationToken);

    private IEnumerable<string> SetVariableLines => _log
        .Select(entry => entry.Line)
        .Where(line => line.StartsWith("##aetheus[setvariable", StringComparison.Ordinal));

    [Fact]
    public void CanHandle_OnlyTheTwoFunctionKinds()
    {
        var executor = Executor();

        Assert.True(executor.CanHandle(OperationKind.PipelineFunction));
        Assert.True(executor.CanHandle(OperationKind.PipelineDeploymentFunction));
        Assert.False(executor.CanHandle(OperationKind.PipelineDotnetTest));
    }

    [Fact]
    public async Task Success_PublishesEveryDeclaredOutput_AndHandsTheContextToTheFunction()
    {
        PipelineFunctionContext? seen = null;
        var function = new FakeFunction("fake-build", async (context, _) =>
        {
            seen = context;
            await context.Log("scanning", TaskLogLevel.Info);
            context.SetOutput("FAKE_STATUS", "clean");
            context.SetOutput("FAKE_COUNT", "3");
            return PipelineFunctionResult.Success();
        });
        var env = Env("""{"project":"src/App","mode":"fast"}""");
        env["BUILD_ID"] = "42";

        var result = await RunAsync(Executor(function), env);

        Assert.Equal(new ExecutorResult(0, false), result);
        Assert.Equal(
            ["##aetheus[setvariable name=FAKE_STATUS]clean", "##aetheus[setvariable name=FAKE_COUNT]3"],
            SetVariableLines);
        Assert.Contains(_log, entry => entry.Line == "scanning");
        Assert.NotNull(seen);
        Assert.Equal(Workspace, seen.Workspace);
        Assert.Equal("src/App", seen.Parameters["project"]);
        Assert.Equal("fast", seen.Parameters["mode"]);
        Assert.Equal("42", seen.RunVariables["BUILD_ID"]);
    }

    [Fact]
    public async Task DeploymentFunction_RunsAsDeploymentKind_WithoutWorkspace()
    {
        var function = new FakeFunction("fake-deploy", (context, _) =>
        {
            context.SetOutput("FAKE_DONE", "true");
            return Task.FromResult(PipelineFunctionResult.Success());
        });

        var result = await RunAsync(
            Executor(function), new Dictionary<string, string>(), "fake-deploy", OperationKind.PipelineDeploymentFunction);

        Assert.Equal(new ExecutorResult(0, false), result);
        Assert.Equal(["##aetheus[setvariable name=FAKE_DONE]true"], SetVariableLines);
    }

    [Fact]
    public async Task UndeclaredOutput_IsRefused_AndNothingIsPublished()
    {
        var function = new FakeFunction("fake-build", (context, _) =>
        {
            context.SetOutput("FAKE_STATUS", "clean");
            context.SetOutput("NOT_IN_CATALOG", "x");
            return Task.FromResult(PipelineFunctionResult.Success());
        });

        var result = await RunAsync(Executor(function), Env());

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("NOT_IN_CATALOG", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(SetVariableLines);
    }

    [Fact]
    public async Task SuccessWithoutEveryDeclaredOutput_IsAFunctionError()
    {
        var function = new FakeFunction("fake-build", (context, _) =>
        {
            context.SetOutput("FAKE_STATUS", "clean");
            return Task.FromResult(PipelineFunctionResult.Success());
        });

        var result = await RunAsync(Executor(function), Env());

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("FAKE_COUNT", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(SetVariableLines);
    }

    [Fact]
    public async Task MultiLineOutputValue_IsRefused()
    {
        var function = new FakeFunction("fake-build", (context, _) =>
        {
            context.SetOutput("FAKE_STATUS", "clean\n##aetheus[setvariable name=FAKE_COUNT]9");
            return Task.FromResult(PipelineFunctionResult.Success());
        });

        var result = await RunAsync(Executor(function), Env());

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Empty(SetVariableLines);
    }

    [Fact]
    public async Task UnknownFunction_IsAnInvalidRequest()
    {
        var result = await RunAsync(Executor(), Env(), target: "no-such-function");

        Assert.Equal(new ExecutorResult(1, false, PipelineFunctionOperationExecutor.InvalidRequest,
            "'no-such-function' is not a function of the Aetheus pipeline catalog."), result);
        Assert.Contains(_log, entry => entry.Level == TaskLogLevel.Error && entry.Line == result.FailureReason);
    }

    [Fact]
    public async Task CatalogFunctionWithoutImplementation_IsFunctionNotImplemented()
    {
        var result = await RunAsync(Executor(), Env(parametersJson: null), target: "fake-missing");

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionNotImplemented, result.FailureCode);
        Assert.Contains("fake-missing", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedCatalogEntry_WithoutImplementation_IsFunctionNotImplemented()
    {
        // The production constructor reads the shared catalog; lot 1.0d registers no function yet.
        var executor = new PipelineFunctionOperationExecutor([]);

        var result = await executor.ExecuteAsync(
            OperationKind.PipelineFunction, PipelineFunctionCatalog.DetectProjectLanguages.Name, Env(parametersJson: null),
            30, Capture, TestContext.Current.CancellationToken);

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionNotImplemented, result.FailureCode);
    }

    [Theory]
    [InlineData("fake-build", OperationKind.PipelineDeploymentFunction)]
    [InlineData("fake-deploy", OperationKind.PipelineFunction)]
    public async Task KindInconsistentWithAccess_IsAnInvalidRequest(string target, OperationKind kind)
    {
        var called = false;
        var function = new FakeFunction(target, (_, _) =>
        {
            called = true;
            return Task.FromResult(PipelineFunctionResult.Success());
        });

        var result = await RunAsync(Executor(function), Env(), target, kind);

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
        Assert.Contains("access", result.FailureReason, StringComparison.Ordinal);
        Assert.False(called);
    }

    [Theory]
    [InlineData("""{"mode":"fast"}""", "requires the parameter 'project'")]
    [InlineData("""{"project":"   "}""", "requires the parameter 'project'")]
    [InlineData("""{"project":"a","extra":"b"}""", "has no parameter 'extra'")]
    [InlineData("""not json""", "is not a JSON object of string values")]
    [InlineData("""{"project":1}""", "is not a JSON object of string values")]
    public async Task BadParameters_AreAnInvalidRequest(string json, string expected)
    {
        var function = new FakeFunction("fake-build", (_, _) => Task.FromResult(PipelineFunctionResult.Success()));

        var result = await RunAsync(Executor(function), Env(json));

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
        Assert.Contains(expected, result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildFunctionWithoutWorkspace_IsAnInvalidRequest()
    {
        var function = new FakeFunction("fake-build", (_, _) => Task.FromResult(PipelineFunctionResult.Success()));
        var env = Env();
        env.Remove(PipelineFunctionRequest.WorkspaceVariable);

        var result = await RunAsync(Executor(function), env);

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
        Assert.Contains("WORKSPACE", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_ReportsTimedOut_AndPublishesNothing()
    {
        var function = new FakeFunction("fake-build", async (context, token) =>
        {
            context.SetOutput("FAKE_STATUS", "clean");
            await Task.Delay(Timeout.Infinite, token);
            return PipelineFunctionResult.Success();
        });

        var result = await RunAsync(Executor(function), Env(), timeoutSeconds: 1);

        Assert.Equal(new ExecutorResult(-1, true), result);
        Assert.Empty(SetVariableLines);
        Assert.Contains(_log, entry => entry.Level == TaskLogLevel.Error && entry.Line.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Timeout_BoundsAFunctionThatIgnoresItsToken()
    {
        var release = new TaskCompletionSource<PipelineFunctionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = new FakeFunction("fake-build", (_, _) => release.Task);

        var result = await RunAsync(Executor(function), Env(), timeoutSeconds: 1);
        release.SetResult(PipelineFunctionResult.Success());

        Assert.True(result.TimedOut);
        Assert.Empty(SetVariableLines);
    }

    [Fact]
    public async Task ExternalCancellation_Propagates()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var function = new FakeFunction("fake-build", async (_, token) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return PipelineFunctionResult.Success();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Executor(function).ExecuteAsync(
                OperationKind.PipelineFunction, "fake-build", Env(), 30, Capture, cancellation.Token));
        Assert.Empty(SetVariableLines);
    }

    [Fact]
    public async Task Exception_IsAFunctionError_WithItsMessageInTheLog()
    {
        var function = new FakeFunction("fake-build", (_, _) =>
            throw new IOException("disk full while scanning src/App"));

        var result = await RunAsync(Executor(function), Env());

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Equal("Function 'fake-build' failed: disk full while scanning src/App", result.FailureReason);
        Assert.Contains(_log, entry => entry.Level == TaskLogLevel.Error && entry.Line == result.FailureReason);
    }

    [Fact]
    public async Task OperationCanceledFromTheFunction_WithoutAnyCancellation_IsAFunctionError()
    {
        var function = new FakeFunction("fake-build", (_, _) =>
            throw new OperationCanceledException("inner HTTP call cancelled"));

        var result = await RunAsync(Executor(function), Env());

        Assert.False(result.TimedOut);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
    }

    [Fact]
    public async Task ReturnedFailure_IsAFunctionError_WithItsCause()
    {
        var function = new FakeFunction("fake-build", (context, _) =>
        {
            context.SetOutput("FAKE_STATUS", "clean");
            context.SetOutput("FAKE_COUNT", "1");
            return Task.FromResult(PipelineFunctionResult.Failure("src/App does not exist"));
        });

        var result = await RunAsync(Executor(function), Env());

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Equal("Function 'fake-build' failed: src/App does not exist", result.FailureReason);
        Assert.Empty(SetVariableLines);
    }

    [Fact]
    public void DuplicateRegistration_IsRefused()
    {
        var first = new FakeFunction("fake-build", (_, _) => Task.FromResult(PipelineFunctionResult.Success()));
        var second = new FakeFunction("fake-build", (_, _) => Task.FromResult(PipelineFunctionResult.Success()));

        Assert.Throws<InvalidOperationException>(() => Executor(first, second));
    }

    private sealed class FakeFunction(
        string name,
        Func<PipelineFunctionContext, CancellationToken, Task<PipelineFunctionResult>> body) : IPipelineFunction
    {
        public string Name => name;

        public Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken) =>
            body(context, cancellationToken);
    }
}
