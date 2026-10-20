// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The <c>aetheus-fn</c> executable body, run on a real temporary workspace: the function's
/// <c>##aetheus[setvariable]</c> lines on stdout, errors on stderr, and one exit code per failure code.
/// </summary>
public sealed class AetheusFnCommandTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"aetheus-fn-cli-{Guid.NewGuid():N}");
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public AetheusFnCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src", "App"));
        File.WriteAllText(Path.Combine(_workspace, "src", "App", "App.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(_workspace, "web"));
        File.WriteAllText(Path.Combine(_workspace, "web", "main.ts"), "export {};");
    }

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private Dictionary<string, string> Env(string? parametersJson = null)
    {
        var env = new Dictionary<string, string> { [PipelineFunctionRequest.WorkspaceVariable] = _workspace };
        if (parametersJson is not null)
            env[PipelineFunctionVariables.ParametersJson] = parametersJson;
        return env;
    }

    private Task<int> RunAsync(Dictionary<string, string> env, params string[] arguments) =>
        AetheusFnCommand.RunAsync(arguments, env, _stdout, _stderr, TestContext.Current.CancellationToken);

    private string[] StdoutLines => _stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task AFunctionRunPrintsItsOutputsAndExitsZero()
    {
        var exitCode = await RunAsync(Env(), "detect-project-languages");

        Assert.Equal(AetheusFnExitCodes.Success, exitCode);
        Assert.Equal(
            [
                "##aetheus[setvariable name=HAS_DOTNET]true",
                "##aetheus[setvariable name=HAS_JAVASCRIPT]true",
                "##aetheus[setvariable name=HAS_PYTHON]false",
                "##aetheus[setvariable name=HAS_JAVA]false"
            ],
            StdoutLines.Where(line => line.StartsWith("##aetheus[", StringComparison.Ordinal)));
        Assert.Equal(string.Empty, _stderr.ToString());
    }

    [Fact]
    public async Task TheParametersComeFromTheEnvironment()
    {
        var exitCode = await RunAsync(Env("""{"root":"web"}"""), "detect-project-languages");

        Assert.Equal(AetheusFnExitCodes.Success, exitCode);
        Assert.Contains("##aetheus[setvariable name=HAS_DOTNET]false", StdoutLines);
        Assert.Contains("##aetheus[setvariable name=HAS_JAVASCRIPT]true", StdoutLines);
    }

    [Fact]
    public async Task AnUnknownFunctionIsAnInvalidRequest()
    {
        var exitCode = await RunAsync(Env(), "no-such-function");

        Assert.Equal(AetheusFnExitCodes.InvalidRequest, exitCode);
        Assert.Contains("'no-such-function' is not a function of the Aetheus pipeline catalog.", _stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("##aetheus[", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"root":1}""")]
    [InlineData("""["root"]""")]
    public async Task BadParameterJsonIsAnInvalidRequest(string parametersJson)
    {
        var exitCode = await RunAsync(Env(parametersJson), "detect-project-languages");

        Assert.Equal(AetheusFnExitCodes.InvalidRequest, exitCode);
        Assert.Contains(PipelineFunctionVariables.ParametersJson, _stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("##aetheus[", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingWorkspaceIsAnInvalidRequest()
    {
        var exitCode = await RunAsync([], "detect-project-languages");

        Assert.Equal(AetheusFnExitCodes.InvalidRequest, exitCode);
        Assert.Contains("WORKSPACE is required", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingFunctionIsAFunctionErrorAndPublishesNothing()
    {
        var exitCode = await RunAsync(Env("""{"root":"../outside"}"""), "detect-project-languages");

        Assert.Equal(AetheusFnExitCodes.FunctionError, exitCode);
        Assert.Contains("is outside the workspace", _stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("##aetheus[", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("detect-project-languages|extra")]
    [InlineData(" ")]
    public async Task AnythingButOneFunctionNameIsAUsageError(string joinedArguments)
    {
        var arguments = joinedArguments.Length == 0 ? [] : joinedArguments.Split('|');

        var exitCode = await RunAsync(Env(), arguments);

        Assert.Equal(AetheusFnExitCodes.InvalidRequest, exitCode);
        Assert.Contains("Usage: aetheus-fn <function>", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false, null, AetheusFnExitCodes.Success)]
    [InlineData(1, false, PipelineFunctionOperationExecutor.InvalidRequest, AetheusFnExitCodes.InvalidRequest)]
    [InlineData(1, false, PipelineFunctionOperationExecutor.FunctionNotImplemented, AetheusFnExitCodes.FunctionNotImplemented)]
    [InlineData(1, false, PipelineFunctionOperationExecutor.FunctionError, AetheusFnExitCodes.FunctionError)]
    [InlineData(-1, true, null, AetheusFnExitCodes.FunctionError)]
    public void EveryResultMapsToItsExitCode(int exitCode, bool timedOut, string? failureCode, int expected) =>
        Assert.Equal(expected, AetheusFnExitCodes.FromResult(new ExecutorResult(exitCode, timedOut, failureCode)));

    [Fact]
    public void TheExitCodesRoundTripToTheFailureCodes()
    {
        foreach (var code in new[]
                 {
                     PipelineFunctionOperationExecutor.InvalidRequest,
                     PipelineFunctionOperationExecutor.FunctionNotImplemented,
                     PipelineFunctionOperationExecutor.FunctionError
                 })
            Assert.Equal(code, AetheusFnExitCodes.ToFailureCode(AetheusFnExitCodes.FromResult(new ExecutorResult(1, false, code))));
        Assert.Null(AetheusFnExitCodes.ToFailureCode(127));
    }
}
