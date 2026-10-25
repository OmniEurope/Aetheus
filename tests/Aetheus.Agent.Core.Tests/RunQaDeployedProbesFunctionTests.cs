// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>run-qa-deployed-integration-suite.sh</c> script, run through the real
/// executor against an in-memory QA backend: the argument refusals, the health endpoints as a technical
/// failure, the five graded probes, the proof and the failed entry the gate step publishes.
/// </summary>
public sealed class RunQaDeployedProbesFunctionTests : IDisposable
{
    private const string Backend = "http://127.0.0.1:20001";

    private readonly FunctionTestHarness _harness = new("aetheus-deployed-probes");
    private readonly FakeQaBackend _backend = new();
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["backend_url"] = Backend,
        ["admin_password_file"] = ".qa-admin-pwd",
        ["label"] = "current"
    };

    public RunQaDeployedProbesFunctionTests()
    {
        _harness.Write("ws/.qa-admin-pwd", "0123abcd\n");
        _backend.Routes["GET /health/live"] = (HttpStatusCode.OK, "Healthy");
        _backend.Routes["GET /health/ready"] = (HttpStatusCode.OK, "Healthy");
        _backend.Routes["POST /api/auth/login"] = (HttpStatusCode.OK, """{"token":"jwt-1","expiresAt":"x"}""");
        _backend.Routes["GET /api/users/me/permissions"] = (HttpStatusCode.OK, """{"username": "admin","roles":["Viewer","Admin"]}""");
        _backend.Routes["GET /api/projects?page=1&pageSize=1"] = (HttpStatusCode.OK, """{"items":[ {"id":1}],"total":1}""");
    }

    public void Dispose() => _harness.Dispose();

    private string Results => Path.Combine(_harness.Workspace, ".qa-test-results", "current", "deployed");

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters)
        };
        return _harness.RunAsync(new RunQaDeployedProbesFunction(new HttpClient(_backend)), env);
    }

    private async Task AssertFailedProbesAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["DEPLOYED_PROBE_STATUS"]);
        Assert.Equal("Deployed QA probes (current)\n", File.ReadAllText(Path.Combine(Results, "failed-tests.txt")));
        Assert.Equal("""{"schema":1,"suite":"current","executedProbes":5,"status":"Failed"}""" + "\n",
            File.ReadAllText(Path.Combine(Results, "deployed-proof.json")));
    }

    [Fact]
    public async Task AllProbes_PassWithTheProofAndTheAdminSession()
    {
        _harness.Write("ws/.qa-test-results/current/deployed/stale.txt", "old");

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", variables["DEPLOYED_PROBE_STATUS"]);
        Assert.Equal("""{"schema":1,"suite":"current","executedProbes":5,"status":"Passed"}""" + "\n",
            File.ReadAllText(Path.Combine(Results, "deployed-proof.json")));
        Assert.False(File.Exists(Path.Combine(Results, "failed-tests.txt")));
        Assert.False(File.Exists(Path.Combine(Results, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(Results, "projects.json")));
        var login = Assert.Single(_backend.Requests, request => request.Route == "POST /api/auth/login");
        Assert.Equal("""{"username":"admin","password":"0123abcd"}""", login.Body);
        Assert.All(_backend.Requests.Where(request => request.Route.StartsWith("GET /api/", StringComparison.Ordinal)),
            request => Assert.Equal("jwt-1", request.Bearer));
        Assert.Contains(_harness.Log, entry => entry.Line == "Deployed QA integration current passed 5/5 probes against the migrated stack.");
    }

    [Theory]
    [InlineData("POST /api/auth/login", "{\"expiresAt\":\"x\"}")]
    [InlineData("GET /api/users/me/permissions", "{\"username\":\"someone\",\"roles\":[\"Admin\"]}")]
    [InlineData("GET /api/users/me/permissions", "{\"username\":\"admin\",\"roles\":[\"Viewer\"]}")]
    [InlineData("GET /api/projects?page=1&pageSize=1", "{\"items\":[],\"total\":0}")]
    public async Task AProbeThatDoesNotHold_IsAFinding(string route, string body)
    {
        _backend.Routes[route] = (HttpStatusCode.OK, body);

        await AssertFailedProbesAsync();
    }

    [Fact]
    public async Task ARefusedProbeRequest_IsAFinding()
    {
        _backend.Routes["GET /api/users/me/permissions"] = (HttpStatusCode.Forbidden, "{}");

        await AssertFailedProbesAsync();
    }

    [Fact]
    public async Task AnUnreachableProbe_IsAFinding()
    {
        _backend.Refuse.Add("POST /api/auth/login");

        await AssertFailedProbesAsync();
    }

    [Theory]
    [InlineData("GET /health/live")]
    [InlineData("GET /health/ready")]
    public async Task AnUnhealthyBackend_FailsTheStep(string route)
    {
        _backend.Routes[route] = (HttpStatusCode.ServiceUnavailable, "Unhealthy");

        var (result, variables) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("answered HTTP 503", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Theory]
    [InlineData("backend_url", "http://10.0.0.5:20001", "QA backend URL must be loopback HTTP.")]
    [InlineData("backend_url", "https://127.0.0.1:20001", "QA backend URL must be loopback HTTP.")]
    [InlineData("backend_url", "http://localhost:20001/api", "QA backend URL must contain only a numeric port.")]
    [InlineData("backend_url", "http://127.0.0.1:", "QA backend URL must contain only a numeric port.")]
    [InlineData("label", "current/../x", "Invalid deployed integration result label.")]
    [InlineData("admin_password_file", "missing", "The admin password file is missing or empty: ")]
    public async Task Arguments_AreRefusedBeforeAnyRequest(string parameter, string value, string reason)
    {
        _parameters[parameter] = value;

        var (result, _) = await RunAsync();

        Assert.Contains(reason, result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void Token_IsTheLastOfItsLineAsSedPrintedIt() =>
        Assert.Equal("b", RunQaDeployedProbesFunction.Token("{\"token\":\"a\",\"x\":{\"token\":\"b\"}}"));
}
