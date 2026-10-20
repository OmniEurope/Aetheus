// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/verify-nminus1-agent-contract.sh</c>, run through the real executor
/// against an in-memory backend, a <c>tar</c> stand-in and an agent launcher that only records: the
/// argument and manifest refusals, the admin session and registration token handed to the agent, the
/// three proofs and their attestation, the findings (status 1) apart from the technical failures, and
/// the agent stopped and its directory removed on every path.
/// </summary>
public sealed class VerifyNMinusOneAgentContractFunctionTests : IDisposable
{
    private const string Backend = "http://127.0.0.1:20001";

    private readonly FunctionTestHarness _harness = new("aetheus-nminus1-agent");
    private readonly FakeFunctionTools _tools = new();
    private readonly FakeQaBackend _backend = new();
    private readonly List<(ProcessStartInfo StartInfo, string Log)> _launched = [];
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["backend_url"] = Backend,
        ["admin_password_file"] = ".qa-admin-pwd",
        ["agent_release"] = ".nminus1-agent-release",
        ["expected_commit"] = AgentReleaseFixture.Commit,
        ["evidence"] = ".qa-test-results/agent-compatibility/attestation.json"
    };

    private string? _settings;
    private int _stopped;
    private int _enrollmentPoll;
    private int _taskPoll;
    private int _enrolledAtPoll = 2;
    private string[] _taskStatuses = ["1", "2", "3"];

    public VerifyNMinusOneAgentContractFunctionTests()
    {
        _harness.Write("ws/.qa-admin-pwd", "pwd\n");
        AgentReleaseFixture.Write(Path.Combine(_harness.Workspace, ".nminus1-agent-release"), AgentReleaseFixture.Manifest());
        _tools.OnRun = (process, _) =>
        {
            var target = process.ArgumentList[^1];
            File.WriteAllText(Path.Combine(target, "Aetheus.Agent.Linux.dll"), string.Empty);
            return Task.FromResult((0, Array.Empty<string>()));
        };
        _backend.Routes["POST /api/auth/login"] = (HttpStatusCode.OK, """{"token":"admin-jwt"}""");
        _backend.Routes["POST /api/auth/registration-tokens"] = (HttpStatusCode.OK, """{"token":"reg-secret","id":7}""");
        _backend.Routes["POST /api/tasks"] = (HttpStatusCode.OK, """{"id":99}""");
        _backend.Respond = route => route switch
        {
            "GET /api/auth/registration-tokens/7" => (HttpStatusCode.OK,
                ++_enrollmentPoll >= _enrolledAtPoll ? """{"usedByServerId":12}""" : """{"usedByServerId":null}"""),
            "GET /api/tasks/99" => (HttpStatusCode.OK,
                $$"""{"status":{{JsonSerializer.Serialize(_taskStatuses[Math.Min(_taskPoll++, _taskStatuses.Length - 1)])}}}"""),
            _ => null
        };
    }

    public void Dispose() => _harness.Dispose();

    private string EvidencePath => Path.Combine(_harness.Workspace, ".qa-test-results", "agent-compatibility", "attestation.json");

    private IBackgroundProcess Launch(ProcessStartInfo startInfo, string log)
    {
        _launched.Add((startInfo, log));
        _settings = File.ReadAllText(Path.Combine(startInfo.WorkingDirectory, "appsettings.json"));
        File.WriteAllText(log, "agent: enrolling\n");
        return new StoppedOnDispose(() => _stopped++);
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters)
        };
        return _harness.RunAsync(new VerifyNMinusOneAgentContractFunction(_tools, new HttpClient(_backend), Launch), env);
    }

    private JsonElement Evidence() => JsonDocument.Parse(File.ReadAllText(EvidencePath)).RootElement;

    [Fact]
    public async Task ACompatibleAgent_PassesTheThreeProofs()
    {
        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", variables["AGENT_CONTRACT_STATUS"]);
        var evidence = Evidence();
        Assert.Equal("Passed", evidence.GetProperty("status").GetString());
        Assert.Equal("NMinusOne", evidence.GetProperty("mode").GetString());
        Assert.Equal(AgentReleaseFixture.Commit, evidence.GetProperty("previousAgentCommit").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(AgentReleaseFixture.ArchiveBytes)), evidence.GetProperty("archiveSha256").GetString());
        Assert.True(evidence.GetProperty("proofs").GetProperty("taskResultPublication").GetBoolean());
        Assert.Equal(12, evidence.GetProperty("serverId").GetInt32());
        Assert.Equal(99, evidence.GetProperty("taskId").GetInt32());
        Assert.StartsWith("{\n  \"schema\": 1,", File.ReadAllText(EvidencePath).ReplaceLineEndings("\n"), StringComparison.Ordinal);

        var (startInfo, _) = Assert.Single(_launched);
        Assert.Equal(["Aetheus.Agent.Linux.dll"], startInfo.ArgumentList);
        using var settings = JsonDocument.Parse(_settings!);
        var agent = settings.RootElement.GetProperty("Aetheus");
        Assert.Equal(Backend, agent.GetProperty("ServerUrl").GetString());
        Assert.Equal("qa-agent-nminus1-7", agent.GetProperty("Name").GetString());
        Assert.Equal("reg-secret", agent.GetProperty("RegistrationToken").GetString());
        Assert.Equal(1, agent.GetProperty("MaxConcurrentTasks").GetInt32());
        Assert.Equal(1, _stopped);
        Assert.False(Directory.Exists(startInfo.WorkingDirectory));
        var task = Assert.Single(_backend.Requests, request => request.Route == "POST /api/tasks");
        Assert.Equal("admin-jwt", task.Bearer);
        Assert.Contains("\"serverId\":12,", task.Body, StringComparison.Ordinal);
        // One second between polls: one before enrollment, two before the task succeeded.
        Assert.Equal(3, _tools.Delays.Count(delay => delay == TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AnAgentThatNeverEnrolls_IsAFindingWithItsLog()
    {
        _enrolledAtPoll = int.MaxValue;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["AGENT_CONTRACT_STATUS"]);
        Assert.Equal(VerifyNMinusOneAgentContractFunction.Polls, _enrollmentPoll);
        var evidence = Evidence();
        Assert.Equal("Failed", evidence.GetProperty("status").GetString());
        Assert.False(evidence.GetProperty("proofs").GetProperty("enrollmentAndHeartbeat").GetBoolean());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("serverId").ValueKind);
        Assert.Contains(_harness.Log, entry => entry is { Line: "agent: enrolling", Level: TaskLogLevel.Error });
        Assert.Contains(_harness.Log, entry => entry.Line == "The retained N-1 agent did not enroll and heartbeat against backend N.");
        Assert.Equal(1, _stopped);
    }

    [Theory]
    [InlineData("Failed", "The retained N-1 agent failed its claim/start/complete proof: Failed")]
    [InlineData("5", "The retained N-1 agent failed its claim/start/complete proof: 5")]
    [InlineData("2", "The retained N-1 agent did not publish a successful task result.")]
    public async Task ATaskThatDoesNotSucceed_IsAFinding(string status, string message)
    {
        _taskStatuses = ["1", status];

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["AGENT_CONTRACT_STATUS"]);
        var proofs = Evidence().GetProperty("proofs");
        Assert.True(proofs.GetProperty("taskClaimAndStart").GetBoolean());
        Assert.False(proofs.GetProperty("taskResultPublication").GetBoolean());
        Assert.Equal(99, Evidence().GetProperty("taskId").GetInt32());
        Assert.Contains(_harness.Log, entry => entry.Line == message);
    }

    [Fact]
    public async Task ANativeAgent_IsStartedDirectly()
    {
        _tools.OnRun = (process, _) =>
        {
            var native = Path.Combine(process.ArgumentList[^1], "Aetheus.Agent.Linux");
            File.WriteAllText(native, "#!/bin/sh\n");
            FunctionTestHarness.MakeExecutable(native);
            return Task.FromResult((0, Array.Empty<string>()));
        };

        await RunAsync();

        var (startInfo, _) = Assert.Single(_launched);
        Assert.EndsWith("Aetheus.Agent.Linux", startInfo.FileName, StringComparison.Ordinal);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public async Task ARefusedRequest_FailsTheStepAndStopsTheAgent()
    {
        _backend.Respond = route => route == "GET /api/auth/registration-tokens/7" ? (HttpStatusCode.InternalServerError, "{}") : null;

        var (result, variables) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("answered HTTP 500", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
        Assert.Equal(1, _stopped);
    }

    [Fact]
    public async Task ALoginWithoutToken_FailsBeforeTheAgentStarts()
    {
        _backend.Routes["POST /api/auth/login"] = (HttpStatusCode.OK, "{}");

        var (result, _) = await RunAsync();

        Assert.EndsWith("QA login did not return a bearer token.", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_launched);
    }

    [Theory]
    [InlineData("commit", "\"ffff\"", "N-1 agent manifest commit mismatch.")]
    [InlineData("protocolVersion", "1", "N-1 software agent does not implement the current backend protocol.")]
    [InlineData("archives", "[]", "N-1 Linux agent archive is missing.")]
    [InlineData("archives", "[{\"fileName\":\"aetheus-agent-linux-x64.tar.gz\",\"platform\":\"linux\",\"architecture\":\"x64\",\"sizeBytes\":1,\"sha256\":\"00\"}]",
        "N-1 Linux agent archive integrity mismatch.")]
    public async Task AManifestThatDoesNotHold_FailsBeforeAnyRequest(string property, string json, string reason)
    {
        var manifest = AgentReleaseFixture.Manifest();
        manifest[property] = System.Text.Json.Nodes.JsonNode.Parse(json);
        AgentReleaseFixture.Write(Path.Combine(_harness.Workspace, ".nminus1-agent-release"), manifest);

        var (result, _) = await RunAsync();

        Assert.EndsWith(reason, result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_backend.Requests);
    }

    [Theory]
    [InlineData("backend_url", "http://10.0.0.5:20001", "N-1 agent proof is restricted to a loopback QA backend.")]
    [InlineData("expected_commit", "xyz", "N-1 commit must be hexadecimal.")]
    [InlineData("admin_password_file", "missing", "The admin password file is missing or empty: ")]
    [InlineData("agent_release", "missing", "The N-1 agent release has no manifest: ")]
    public async Task Arguments_AreRefusedBeforeAnything(string parameter, string value, string reason)
    {
        _parameters[parameter] = value;

        var (result, _) = await RunAsync();

        Assert.Contains(reason, result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_backend.Requests);
        Assert.Empty(_tools.Runs);
    }

    private sealed class StoppedOnDispose(Action onStop) : IBackgroundProcess
    {
        public ValueTask DisposeAsync()
        {
            onStop();
            return ValueTask.CompletedTask;
        }
    }
}
