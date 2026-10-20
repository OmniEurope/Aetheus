// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Aetheus.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Aetheus.Agent.Core.Tests.AiProviderClientTests;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The AI executor against a fake provider: what it sends (system prompt, prompt file, context files,
/// git range), how it reads the answer (verdict, findings JSON to SARIF, advisory tolerance), and that
/// the API key never reaches a log line, a report or the git commands. No network, no binary.
/// </summary>
public sealed class AiRunOperationExecutorTests : IDisposable
{
    private const string Key = "sk-ant-test-abcdefghijklmnop";

    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-ai-executor-").FullName;
    private readonly IServerApiClient _api = Substitute.For<IServerApiClient>();
    private readonly IShellRunner _shell = Substitute.For<IShellRunner>();
    private readonly List<(string Message, TaskLogLevel Level)> _output = [];
    private PublishAiRunResultRequest? _published;
    private string _answer = "# Review\nNothing blocking.";
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _stopReason = "end_turn";
    private FakeHandler? _handler;

    public AiRunOperationExecutorTests()
    {
        _api.PublishAiRunResultAsync(
                Arg.Do<PublishAiRunResultRequest>(request => _published = request), Arg.Any<CancellationToken>())
            .Returns(call => new AiRunResultDto { Id = 1, Succeeded = call.Arg<PublishAiRunResultRequest>().Succeeded });
        _api.UploadArtifactAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 77 });
        _api.PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto());
    }

    private string Workspace => Path.Combine(_root, "workspace");

    private JsonElement SentBody => JsonDocument.Parse(Assert.Single(_handler!.Requests).Body).RootElement;

    [Fact]
    public async Task GateStep_WithVerdictPass_SucceedsAndAsksForTheVerdictInTheSystemPrompt()
    {
        _answer = "# Mock report\nAll good.\nVERDICT: PASS";

        var result = await RunAsync(Environment(gate: true));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(_published);
        Assert.Equal(AiVerdict.Pass, _published.Verdict);
        Assert.True(_published.Succeeded);
        Assert.Contains("Mock report", _published.ReportMarkdown, StringComparison.Ordinal);
        Assert.Contains("VERDICT: PASS or VERDICT: FAIL", SentBody.GetProperty("system").GetString(), StringComparison.Ordinal);
        Assert.Equal("Review the commits.", SentBody.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("low", SentBody.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(Key, Assert.Single(_handler!.Requests).Headers["x-api-key"]);
        Assert.DoesNotContain(_output, line => line.Message.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheProfileResolution_IsTheFirstStepLogLine()
    {
        var environment = Environment(gate: false);
        environment["AETHEUS_AI_PROFILE_RESOLUTION"] =
            "Profile 'claude-sonnet' named by the step was not found in the organization. "
            + "Using the pipeline's AI runner profile 'Claude'.";

        await RunAsync(environment);

        var first = _output.First();
        Assert.Equal(TaskLogLevel.Info, first.Level);
        Assert.Equal(environment["AETHEUS_AI_PROFILE_RESOLUTION"], first.Message);
    }

    [Fact]
    public async Task WithoutAProfileResolution_NoResolutionLineIsWritten()
    {
        await RunAsync(Environment());

        Assert.DoesNotContain(_output, line => line.Message.Contains("AI runner profile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GateStep_WithoutVerdict_Fails()
    {
        var result = await RunAsync(Environment(gate: true));

        Assert.Equal(-1, result.ExitCode);
        Assert.False(_published!.Succeeded);
        Assert.Contains(_output, line => line.Message.Contains("VERDICT: PASS is required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderError_IsPublishedAsAFailedResult_WithTheKeyMasked()
    {
        _status = HttpStatusCode.Unauthorized;
        _answer = "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key " + Key + "\"}}";

        var result = await RunAsync(Environment());

        Assert.Equal(-1, result.ExitCode);
        Assert.False(_published!.Succeeded);
        Assert.Contains("HTTP 401", _published.ReportMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, _published.ReportMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(_output, line => line.Message.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnswerEchoingTheKey_IsMaskedInTheReport()
    {
        _answer = $"The key is {Key}.";

        await RunAsync(Environment());

        Assert.Equal("The key is ***.", _published!.ReportMarkdown);
    }

    [Fact]
    public async Task TokenLimit_MarksTheResultTruncated()
    {
        _stopReason = "max_tokens";

        await RunAsync(Environment());

        Assert.True(_published!.Truncated);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Warning
            && line.Message.Contains("output tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CliEraTask_IsRefusedAsIncomplete_WithoutCallingAnything()
    {
        var environment = new Dictionary<string, string>
        {
            ["AETHEUS_TASK_ID"] = "9",
            ["AETHEUS_AI_PROFILE_NAME"] = "old",
            ["AETHEUS_AI_BINARY"] = "claude",
            ["AETHEUS_AI_ARGS_JSON"] = "[\"{prompt_file}\"]",
            ["AETHEUS_AI_PROMPT"] = "Review.",
            ["AETHEUS_AI_MAX_OUTPUT_BYTES"] = "200000"
        };

        var result = await RunAsync(environment);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(_output, line => line.Message == AiRunConfiguration.IncompleteError);
        Assert.Null(_handler);
    }

    [Fact]
    public async Task PromptFileAndContextPaths_AreAttached_WithinTheWorkspaceOnly()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        Directory.CreateDirectory(Path.Combine(Workspace, "src"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "prompt.md"), "Extra instructions.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "src", "A.cs"), "class A {}", TestContext.Current.CancellationToken);
        var environment = Environment();
        environment["AETHEUS_AI_PROMPT_FILE"] = "docs/prompt.md";
        environment["AETHEUS_AI_CONTEXT_PATHS_JSON"] = JsonSerializer.Serialize(new[] { "src/A.cs", "src", "missing.txt", "../outside.txt" });

        await RunAsync(environment);

        var user = SentBody.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("Extra instructions.", user, StringComparison.Ordinal);
        Assert.Contains("### src/A.cs", user, StringComparison.Ordinal);
        Assert.Contains("class A {}", user, StringComparison.Ordinal);
        Assert.Contains("- A.cs", user, StringComparison.Ordinal);
        Assert.Contains("no such file in the workspace", user, StringComparison.Ordinal);
        Assert.Contains("the path leaves the workspace", user, StringComparison.Ordinal);
    }

    // Creating a symbolic link needs a privilege on Windows; the junction test below proves the same check there.
    [PlatformFact("linux")]
    public async Task ContextFile_ThroughASymbolicLink_IsNeverRead()
    {
        Directory.CreateDirectory(Workspace);
        var secret = Path.Combine(_root, "agent-token.txt");
        await File.WriteAllTextAsync(secret, "agent-bearer-token", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(Workspace, "link.txt"), secret);
        var environment = Environment();
        environment["AETHEUS_AI_CONTEXT_PATHS_JSON"] = JsonSerializer.Serialize(new[] { "link.txt" });

        await RunAsync(environment);

        var user = SentBody.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain("agent-bearer-token", user, StringComparison.Ordinal);
        Assert.Contains("symbolic link", user, StringComparison.Ordinal);
    }

    [PlatformFact("windows")]
    public async Task ContextPath_ThroughADirectoryJunction_IsNeverRead()
    {
        // Windows' junction needs no privilege, unlike a symbolic link: the same check, provable here.
        Directory.CreateDirectory(Workspace);
        var outside = Path.Combine(_root, "agent-home");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "token"), "agent-bearer-token", TestContext.Current.CancellationToken);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", ["/c", "mklink", "/J", Path.Combine(Workspace, "home"), outside])
        { RedirectStandardOutput = true, UseShellExecute = false })!)
            await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(Path.Combine(Workspace, "home", "")), "the junction was not created");
        var environment = Environment();
        environment["AETHEUS_AI_CONTEXT_PATHS_JSON"] = JsonSerializer.Serialize(new[] { "home/token", "home" });

        try
        {
            await RunAsync(environment);
        }
        finally
        {
            // A non-recursive delete removes the junction itself, never what it points to.
            Directory.Delete(Path.Combine(Workspace, "home"));
        }

        var user = SentBody.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain("agent-bearer-token", user, StringComparison.Ordinal);
        Assert.DoesNotContain("- token", user, StringComparison.Ordinal);
        Assert.Contains("symbolic link", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedContextFile_IsCut_WithANotice()
    {
        Directory.CreateDirectory(Workspace);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "big.txt"),
            new string('x', AiPromptAssembler.ContextFileBudgetBytes + 500), TestContext.Current.CancellationToken);
        var environment = Environment();
        environment["AETHEUS_AI_CONTEXT_PATHS_JSON"] = JsonSerializer.Serialize(new[] { "big.txt" });

        await RunAsync(environment);

        var user = SentBody.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains($"(file cut at {AiPromptAssembler.ContextFileBudgetBytes} bytes)", user, StringComparison.Ordinal);
        Assert.True(user.Length < AiPromptAssembler.ContextFileBudgetBytes + 2_000);
    }

    [Fact]
    public async Task GitRange_AttachesLogAndDiff_WithArgvOnlyAndDriversDisabled()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        var calls = StubGit(log: "commit abc\n src/A.cs | 2 +-", diff: "diff --git a/src/A.cs b/src/A.cs");
        var environment = Environment();
        environment["AETHEUS_AI_GIT_RANGE"] = "0123456789abcdef0123456789abcdef01234567..HEAD";

        await RunAsync(environment);

        var user = SentBody.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("## Git context: 0123456789abcdef0123456789abcdef01234567..HEAD", user, StringComparison.Ordinal);
        Assert.Contains("src/A.cs | 2 +-", user, StringComparison.Ordinal);
        Assert.Contains("diff --git a/src/A.cs b/src/A.cs", user, StringComparison.Ordinal);
        Assert.Contains(calls, args => args.Contains("--stat") && args.Contains("0123456789abcdef0123456789abcdef01234567..HEAD"));
        Assert.Contains(calls, args => args.Contains("diff") && args.Contains("--no-ext-diff") && args.Contains("--no-textconv"));
        Assert.All(calls, args => Assert.DoesNotContain(args, arg => arg.Contains(Key, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GitRange_WithAnEmptyBaseline_FallsBackToTheLastCommits_WithAWarning()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        var calls = StubGit(log: "commit abc", diff: "commit abc\ndiff --git a/x b/x");
        var environment = Environment();
        environment["AETHEUS_AI_GIT_RANGE"] = "..HEAD";

        await RunAsync(environment);

        var user = SentBody.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains($"the last {AiPromptAssembler.FallbackCommitCount} commits", user, StringComparison.Ordinal);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Warning && line.Message.Contains("'..HEAD'", StringComparison.Ordinal));
        Assert.Contains(calls, args => args.Contains($"--max-count={AiPromptAssembler.FallbackCommitCount}") && args.Contains("-p"));
    }

    [Fact]
    public async Task GitDiff_LargerThanItsBudget_IsCut_WithANotice()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        StubGit(log: "commit abc", diff: new string('+', AiPromptAssembler.GitDiffBudgetBytes + 100));
        var environment = Environment();
        environment["AETHEUS_AI_GIT_RANGE"] = "main..HEAD";

        await RunAsync(environment);

        var user = SentBody.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains($"(git diff cut at {AiPromptAssembler.GitDiffBudgetBytes} bytes)", user, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc123..HEAD", true)]
    [InlineData("origin/main..feature/x", true)]
    [InlineData("HEAD~10..HEAD", true)]
    [InlineData("..HEAD", false)]
    [InlineData("$(DELIVERY_BASELINE_SOURCE_SHA)..HEAD", false)]
    [InlineData("--output=/tmp/x..HEAD", false)]
    [InlineData("a...b", false)]
    [InlineData("HEAD", false)]
    public void GitRange_AcceptsOnlyTwoRevisions(string range, bool valid) =>
        Assert.Equal(valid, AiPromptAssembler.IsValidRange(range));

    [Fact]
    public async Task Findings_AreConvertedToSarif_AndPublishedAsAnAiReviewReport()
    {
        _answer = """
            # Review
            One issue.

            ```json
            {"findings":[{"file":"src/A.cs","line":3,"severity":"error","rule":"null-deref","message":"May dereference null."},
                         {"file":"/etc/passwd","line":1,"severity":"bogus","rule":"x","message":"Outside path."},
                         {"file":"src/B.cs","line":2}]}
            ```
            """;

        var result = await RunAsync(Environment(findings: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fenced code block tagged json", SentBody.GetProperty("system").GetString(), StringComparison.Ordinal);
        await _api.Received(1).UploadArtifactAsync(
            42, AiFindingsPublisher.ArtifactName, "AI review", Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PublishAnalysisReportAsync(42,
            Arg.Is<PublishAnalysisReportRequest>(request =>
                request.ScannerKey == "ai-review"
                && request.Category == AnalysisCategory.CodeQuality
                && request.Format == AnalysisReportFormat.Sarif
                && request.PipelineArtifactId == 77),
            Arg.Any<CancellationToken>());
        var sarif = JsonDocument.Parse(AiFindingsPublisher.ToSarif(_answer).Sarif!).RootElement;
        Assert.Equal("2.1.0", sarif.GetProperty("version").GetString());
        var results = sarif.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("null-deref", results[0].GetProperty("ruleId").GetString());
        Assert.Equal("error", results[0].GetProperty("level").GetString());
        var location = results[0].GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal("src/A.cs", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(3, location.GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Equal("note", results[1].GetProperty("level").GetString());
        Assert.False(results[1].TryGetProperty("locations", out _));
        Assert.Contains(_output, line => line.Message.Contains("2 finding(s), 1 entries without a message skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Placeholder_OnAnEmptyFindingsBlock_PublishesOneFakeNote()
    {
        _answer = "Nothing to report.\n\n```json\n{\"findings\":[]}\n```\n";
        var environment = Environment(findings: true, advisory: true);
        environment["AETHEUS_AI_FINDINGS_PLACEHOLDER"] = "true";

        var result = await RunAsync(environment);

        Assert.Equal(0, result.ExitCode);
        await _api.Received(1).PublishAnalysisReportAsync(42,
            Arg.Is<PublishAnalysisReportRequest>(request => request.ScannerKey == "ai-review"), Arg.Any<CancellationToken>());
        Assert.Contains(_output, line => line.Message.Contains("AI findings published: 1 finding(s)", StringComparison.Ordinal));
        var results = JsonDocument.Parse(AiFindingsPublisher.ToSarif(_answer, placeholder: true).Sarif!)
            .RootElement.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(AiFindingsPublisher.PlaceholderRule, results[0].GetProperty("ruleId").GetString());
        Assert.Equal("note", results[0].GetProperty("level").GetString());
        Assert.StartsWith("[FAKE]", results[0].GetProperty("message").GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholder_NeverReplacesARealFinding_AndIsOffByDefault()
    {
        const string real = "```json\n{\"findings\":[{\"file\":\"src/A.cs\",\"line\":1,\"message\":\"Real.\"}]}\n```";
        const string empty = "```json\n{\"findings\":[]}\n```";

        var withReal = JsonDocument.Parse(AiFindingsPublisher.ToSarif(real, placeholder: true).Sarif!)
            .RootElement.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(1, withReal.GetArrayLength());
        Assert.Equal("Real.", withReal[0].GetProperty("message").GetProperty("text").GetString());
        Assert.Equal(0, AiFindingsPublisher.ToSarif(empty).Count);
    }

    [Fact]
    public async Task MissingFindings_OnAnAdvisoryStep_IsAWarning_AndTheStepSucceeds()
    {
        var result = await RunAsync(Environment(findings: true, advisory: true));

        Assert.Equal(0, result.ExitCode);
        Assert.True(_published!.Succeeded);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Warning
            && line.Message.Contains("AI findings not published (advisory step, ignored)", StringComparison.Ordinal));
        await _api.DidNotReceive().PublishAnalysisReportAsync(
            Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MalformedFindings_OnANonAdvisoryStep_FailsTheStep()
    {
        _answer = "Report.\n```json\n{not json\n```";

        var result = await RunAsync(Environment(findings: true));

        Assert.Equal(-1, result.ExitCode);
        Assert.False(_published!.Succeeded);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Error
            && line.Message.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedPublication_OnAnAdvisoryStep_DoesNotFailTheStep()
    {
        _answer = "Report.\n```json\n{\"findings\":[]}\n```";
        _api.PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns<AnalysisReportDto?>(_ => throw new HttpRequestException("400 Bad Request"));

        var result = await RunAsync(Environment(findings: true, advisory: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Warning
            && line.Message.Contains("400 Bad Request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectTask_ClonesWithEphemeralCredentials_AndPublishesTheBaseCommit()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        IReadOnlyDictionary<string, string>? cloneEnvironment = null;
        _shell.RunExecAsync(
                "git", Arg.Is<IReadOnlyList<string>>(args => args.Contains("clone")), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call =>
            {
                cloneEnvironment = call.ArgAt<IReadOnlyDictionary<string, string>>(2);
                return new ShellExecResult(0, string.Empty, string.Empty);
            });
        _shell.RunExecAsync(
                "git", Arg.Is<IReadOnlyList<string>>(args => args.Contains("rev-parse")),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, commit, string.Empty));
        var environment = Environment();
        environment.Remove("AETHEUS_AI_WORKING_DIR");
        environment["AETHEUS_AI_SOURCE_REPOSITORY_ID"] = "13";
        environment["AETHEUS_AI_SOURCE_REPOSITORY_PATH"] = "/git/7/core.git";
        environment["AETHEUS_AI_SOURCE_REF"] = "develop";
        environment["GIT_USERNAME"] = "aetheus-ai-11";
        environment["GIT_PASSWORD"] = "secret-token";

        var result = await RunAsync(environment, serverUrl: "https://backend.test");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(cloneEnvironment);
        Assert.StartsWith("Authorization: Basic ", cloneEnvironment["GIT_CONFIG_VALUE_0"], StringComparison.Ordinal);
        Assert.Equal(13, _published!.SourceRepositoryId);
        Assert.Equal(commit, _published.BaseCommitSha);
        Assert.DoesNotContain("secret-token", Assert.Single(_handler!.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_ToString_NeverPrintsTheKey()
    {
        Assert.True(AiRunConfiguration.TryRead(Environment(), out var configuration, out _));
        Assert.DoesNotContain(Key, configuration.ToString(), StringComparison.Ordinal);
    }

    private List<IReadOnlyList<string>> StubGit(string log, string diff)
    {
        var calls = new List<IReadOnlyList<string>>();
        _shell.RunExecAsync(
                "git", Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call =>
            {
                var args = call.ArgAt<IReadOnlyList<string>>(1);
                calls.Add(args);
                var output = args.Contains("--stat") ? log : diff;
                var max = call.ArgAt<int>(5);
                return new ShellExecResult(0, output, string.Empty, Truncated: Encoding.UTF8.GetByteCount(output) > max);
            });
        return calls;
    }

    private Dictionary<string, string> Environment(bool gate = false, bool findings = false, bool advisory = false)
    {
        var environment = new Dictionary<string, string>
        {
            ["AETHEUS_TASK_ID"] = "9",
            ["AETHEUS_AI_PROFILE_NAME"] = "claude-sonnet",
            ["AETHEUS_AI_PROVIDER"] = "anthropic",
            ["AETHEUS_AI_BASE_URL"] = "https://api.anthropic.com",
            ["AETHEUS_AI_MODEL"] = "claude-sonnet-5-5",
            ["AETHEUS_AI_API_KEY"] = Key,
            ["AETHEUS_AI_EFFORT"] = "low",
            ["AETHEUS_AI_MAX_OUTPUT_TOKENS"] = "4000",
            ["AETHEUS_AI_PROMPT"] = "Review the commits.",
            ["AETHEUS_AI_MAX_OUTPUT_BYTES"] = "200000",
            ["AETHEUS_AI_GATE"] = gate ? "true" : "false",
            ["AETHEUS_AI_WORKING_DIR"] = Workspace
        };
        if (advisory) environment["AETHEUS_AI_ADVISORY"] = "true";
        if (findings)
        {
            environment["AETHEUS_AI_FINDINGS"] = "true";
            environment["AETHEUS_RUN_ID"] = "42";
            environment["AETHEUS_STAGE_NAME"] = "AI review";
        }
        return environment;
    }

    private Task<ExecutorResult> RunAsync(Dictionary<string, string> environment, string serverUrl = "https://backend.test")
    {
        Directory.CreateDirectory(Workspace);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AiRunOperationExecutor.HttpClientName).Returns(_ =>
        {
            _handler = new FakeHandler(_ =>
            {
                var body = _status == HttpStatusCode.OK
                    ? JsonSerializer.Serialize(new
                    {
                        content = new[] { new { type = "text", text = _answer } },
                        stop_reason = _stopReason,
                        usage = new { input_tokens = 10, output_tokens = 5 }
                    })
                    : _answer;
                return new HttpResponseMessage(_status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
            return new HttpClient(_handler);
        });
        var executor = new AiRunOperationExecutor(
            _api, _shell, factory,
            Options.Create(new AetheusAgentOptions { WorkDirectory = Path.Combine(_root, "agent"), ServerUrl = serverUrl }),
            TimeProvider.System, NullLogger<AiRunOperationExecutor>.Instance);
        return executor.ExecuteAsync(
            OperationKind.AiRun, "ai-run", environment, 30,
            (message, level) =>
            {
                _output.Add((message, level));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        _handler?.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
