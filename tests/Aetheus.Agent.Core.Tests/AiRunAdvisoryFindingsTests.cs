// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The AI step's advisory, findings_sarif and capture_diff switches, and the agent home handed to the CLI.
/// </summary>
public sealed class AiRunAdvisoryFindingsTests : IDisposable
{
    private const string Sarif =
        """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"ai"}},"results":[{"ruleId":"R1","level":"warning","message":{"text":"x"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/A.cs"},"region":{"startLine":3}}}]}]}]}""";

    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-ai-findings-").FullName;
    private readonly IServerApiClient _api = Substitute.For<IServerApiClient>();
    private readonly IShellRunner _shell = Substitute.For<IShellRunner>();
    private readonly List<(string Message, TaskLogLevel Level)> _output = [];
    private PublishAiRunResultRequest? _published;

    public AiRunAdvisoryFindingsTests()
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

    [Fact]
    public void RunnerEnvironment_CarriesTheAgentHome_AndAProfileValueWins()
    {
        var agent = new Dictionary<string, string> { ["HOME"] = "/home/aetheus", ["USERPROFILE"] = @"C:\Users\aetheus" };
        var task = new Dictionary<string, string>
        {
            ["AETHEUS_AI_BINARY"] = "claude",
            ["GIT_PASSWORD"] = "secret",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "token"
        };

        var linux = AiRunConfiguration.ReadRunnerEnvironment(task, name => agent.GetValueOrDefault(name), isWindows: false);
        var windows = AiRunConfiguration.ReadRunnerEnvironment(task, name => agent.GetValueOrDefault(name), isWindows: true);
        var overridden = AiRunConfiguration.ReadRunnerEnvironment(
            new Dictionary<string, string> { ["HOME"] = "/srv/claude-home" }, name => agent.GetValueOrDefault(name), isWindows: false);

        Assert.Equal(["CLAUDE_CODE_OAUTH_TOKEN", "HOME"], linux.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("/home/aetheus", linux["HOME"]);
        Assert.Equal(@"C:\Users\aetheus", windows["USERPROFILE"]);
        Assert.Equal("/srv/claude-home", overridden["HOME"]);
    }

    [Fact]
    public async Task FindingsSarif_IsPublishedAsAnAiReviewReport_AndCaptureDiffFalseRunsNoGit()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        CliWrites(".ai-review/findings.sarif", Sarif);

        var result = await RunAsync(advisory: true, captureDiff: false);

        Assert.Equal(0, result.ExitCode);
        await _api.Received(1).UploadArtifactAsync(
            42, AiFindingsPublisher.ArtifactName, "AI review", Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PublishAnalysisReportAsync(42,
            Arg.Is<PublishAnalysisReportRequest>(request =>
                request.ScannerKey == "ai-review"
                && request.Category == AnalysisCategory.CodeQuality
                && request.Format == AnalysisReportFormat.Sarif
                && request.PipelineArtifactId == 77
                && request.ReportContent == Sarif),
            Arg.Any<CancellationToken>());
        await _shell.DidNotReceive().RunExecAsync(
            "git", Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
        Assert.NotNull(_published);
        Assert.Null(_published.DiffPatch);
        Assert.True(_published.Succeeded);
    }

    [Fact]
    public async Task MissingSarif_OnAnAdvisoryStep_IsAWarning_AndAStaleFileIsNeverPublished()
    {
        // Left by an earlier step: the CLI does not write it this time, so it must not be published.
        Directory.CreateDirectory(Path.Combine(Workspace, ".ai-review"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, ".ai-review", "findings.sarif"), Sarif,
            TestContext.Current.CancellationToken);
        CliWrites(null, null);

        var result = await RunAsync(advisory: true, captureDiff: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Warning
            && line.Message.Contains("AI findings not published", StringComparison.Ordinal));
        await _api.DidNotReceive().PublishAnalysisReportAsync(
            Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidSarif_OnANonAdvisoryStep_FailsTheStep()
    {
        CliWrites(".ai-review/findings.sarif", "not json");

        var result = await RunAsync(advisory: false, captureDiff: false);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Error
            && line.Message.Contains("not a SARIF document", StringComparison.Ordinal));
        Assert.NotNull(_published);
        Assert.False(_published.Succeeded);
    }

    [Fact]
    public async Task RejectedPublication_OnAnAdvisoryStep_DoesNotFailTheStep()
    {
        CliWrites(".ai-review/findings.sarif", Sarif);
        _api.PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns<AnalysisReportDto?>(_ => throw new HttpRequestException("400 Bad Request"));

        var result = await RunAsync(advisory: true, captureDiff: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Warning
            && line.Message.Contains("400 Bad Request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureDiff_StaysOnByDefault()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        CliWrites(null, null);
        _shell.RunExecAsync(
                "git", Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, string.Empty, string.Empty));

        await RunAsync(advisory: false, captureDiff: null, findings: false);

        await _shell.Received().RunExecAsync(
            "git", Arg.Is<IReadOnlyList<string>>(args => args.Contains("diff")),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    private void CliWrites(string? relativePath, string? content)
    {
        _shell.RunExecAsync(
                "claude", Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(), false, Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call =>
            {
                if (relativePath is not null)
                {
                    var path = Path.Combine(call.ArgAt<string>(3), relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, content);
                }
                return new ShellExecResult(0, "# Review\nNothing blocking.", string.Empty);
            });
    }

    private Task<ExecutorResult> RunAsync(bool advisory, bool? captureDiff, bool findings = true)
    {
        Directory.CreateDirectory(Workspace);
        var environment = new Dictionary<string, string>
        {
            ["AETHEUS_TASK_ID"] = "9",
            ["AETHEUS_AI_PROFILE_NAME"] = "claude-sonnet",
            ["AETHEUS_AI_BINARY"] = "claude",
            ["AETHEUS_AI_ARGS_JSON"] = JsonSerializer.Serialize(new[] { "-p", "{prompt_file}" }),
            ["AETHEUS_AI_PROMPT"] = "Review the commits.",
            ["AETHEUS_AI_MAX_OUTPUT_BYTES"] = "200000",
            ["AETHEUS_AI_WORKING_DIR"] = Workspace
        };
        if (advisory) environment["AETHEUS_AI_ADVISORY"] = "true";
        if (captureDiff == false) environment["AETHEUS_AI_CAPTURE_DIFF"] = "false";
        if (findings)
        {
            environment["AETHEUS_AI_FINDINGS_SARIF"] = ".ai-review/findings.sarif";
            environment["AETHEUS_RUN_ID"] = "42";
            environment["AETHEUS_STAGE_NAME"] = "AI review";
        }
        var executor = new AiRunOperationExecutor(
            _api, _shell, Options.Create(new AetheusAgentOptions { WorkDirectory = Path.Combine(_root, "agent") }),
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

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
