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

public sealed class AiRunOperationExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_MockedCliPublishesPassingGateReport()
    {
        var workDirectory = Path.Combine(
            Path.GetTempPath(), "aetheus-ai-mock-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var api = Substitute.For<IServerApiClient>();
            var shell = Substitute.For<IShellRunner>();
            PublishAiRunResultRequest? published = null;
            shell.RunExecAsync(
                    "mock-ai",
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<string>(),
                    false,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<TimeSpan?>())
                .Returns(new ShellExecResult(
                    0,
                    "# Mock AI report\nThe mocked review completed.\nVERDICT: PASS",
                    string.Empty));
            api.PublishAiRunResultAsync(
                    Arg.Do<PublishAiRunResultRequest>(request => published = request),
                    Arg.Any<CancellationToken>())
                .Returns(call => new AiRunResultDto
                {
                    Id = 1,
                    ServerTaskId = call.Arg<PublishAiRunResultRequest>().ServerTaskId,
                    ReportMarkdown = call.Arg<PublishAiRunResultRequest>().ReportMarkdown,
                    Verdict = call.Arg<PublishAiRunResultRequest>().Verdict,
                    Succeeded = call.Arg<PublishAiRunResultRequest>().Succeeded
                });
            var executor = new AiRunOperationExecutor(
                api,
                shell,
                Options.Create(new AetheusAgentOptions { WorkDirectory = workDirectory }),
                TimeProvider.System,
                NullLogger<AiRunOperationExecutor>.Instance);
            var environment = new Dictionary<string, string>
            {
                ["AETHEUS_TASK_ID"] = "41",
                ["AETHEUS_AI_PROFILE_NAME"] = "Mock CLI",
                ["AETHEUS_AI_BINARY"] = "mock-ai",
                ["AETHEUS_AI_ARGS_JSON"] = JsonSerializer.Serialize(new[] { "{prompt_file}" }),
                ["AETHEUS_AI_PROMPT"] = "Review this mocked workspace.",
                ["AETHEUS_AI_GATE"] = "true",
                ["AETHEUS_AI_SENDS_DATA_EXTERNALLY"] = "false",
                ["AETHEUS_AI_MAX_OUTPUT_BYTES"] = "200000",
                ["AETHEUS_AI_WORKING_DIR"] = string.Empty,
                ["AI_PROVIDER_TOKEN"] = "profile-secret"
            };

            var result = await executor.ExecuteAsync(
                OperationKind.AiRun,
                "ai-run",
                environment,
                30,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(published);
            Assert.Equal(41, published.ServerTaskId);
            Assert.Equal(AiVerdict.Pass, published.Verdict);
            Assert.True(published.Succeeded);
            Assert.Contains("Mock AI report", published.ReportMarkdown, StringComparison.Ordinal);
            await shell.Received(1).RunExecAsync(
                "mock-ai",
                Arg.Any<IReadOnlyList<string>>(),
                // The profile's variable, the agent's home (AiRunConfiguration.ReadRunnerEnvironment),
                // and none of the executor's own AETHEUS_* settings.
                Arg.Is<IReadOnlyDictionary<string, string>>(variables =>
                    variables["AI_PROVIDER_TOKEN"] == "profile-secret"
                    && variables.Keys.All(key => key == "AI_PROVIDER_TOKEN" || key == "HOME" || key == "USERPROFILE")),
                Arg.Any<string>(),
                false,
                200000,
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>());
            await api.Received(1).PublishAiRunResultAsync(
                Arg.Any<PublishAiRunResultRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AutonomousRun_ClonesAuthenticatedRepositoryAndPublishesBaseCommit()
    {
        var workDirectory = Path.Combine(
            Path.GetTempPath(), "aetheus-ai-checkout-tests", Guid.NewGuid().ToString("N"));
        try
        {
            const string commit = "0123456789abcdef0123456789abcdef01234567";
            var api = Substitute.For<IServerApiClient>();
            var shell = Substitute.For<IShellRunner>();
            PublishAiRunResultRequest? published = null;
            IReadOnlyDictionary<string, string>? cloneEnvironment = null;
            IReadOnlyDictionary<string, string>? runnerEnvironment = null;
            shell.RunExecAsync(
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<TimeSpan?>())
                .Returns(call =>
                {
                    var executable = call.ArgAt<string>(0);
                    var environment = call.ArgAt<IReadOnlyDictionary<string, string>>(2);
                    if (executable == "git")
                    {
                        cloneEnvironment = environment;
                        return new ShellExecResult(0, string.Empty, string.Empty);
                    }
                    runnerEnvironment = environment;
                    return new ShellExecResult(
                        0,
                        "Autonomous review complete.\nVERDICT: PASS",
                        string.Empty);
                });
            shell.RunExecAsync(
                    "git",
                    Arg.Is<IReadOnlyList<string>>(arguments =>
                        arguments.Contains("rev-parse", StringComparer.Ordinal)),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<TimeSpan?>())
                .Returns(new ShellExecResult(0, commit, string.Empty));
            api.PublishAiRunResultAsync(
                    Arg.Do<PublishAiRunResultRequest>(request => published = request),
                    Arg.Any<CancellationToken>())
                .Returns(call => new AiRunResultDto
                {
                    Id = 1,
                    ServerTaskId = call.Arg<PublishAiRunResultRequest>().ServerTaskId,
                    Succeeded = true
                });
            var executor = new AiRunOperationExecutor(
                api,
                shell,
                Options.Create(new AetheusAgentOptions
                {
                    WorkDirectory = workDirectory,
                    ServerUrl = "https://backend.test"
                }),
                TimeProvider.System,
                NullLogger<AiRunOperationExecutor>.Instance);
            var environment = new Dictionary<string, string>
            {
                ["AETHEUS_TASK_ID"] = "51",
                ["AETHEUS_AI_PROFILE_NAME"] = "Autonomous",
                ["AETHEUS_AI_BINARY"] = "mock-ai",
                ["AETHEUS_AI_ARGS_JSON"] = "[]",
                ["AETHEUS_AI_PROMPT"] = "Review.",
                ["AETHEUS_AI_MAX_OUTPUT_BYTES"] = "4096",
                ["AETHEUS_AI_SOURCE_REPOSITORY_ID"] = "13",
                ["AETHEUS_AI_SOURCE_REPOSITORY_PATH"] = "/git/7/core.git",
                ["AETHEUS_AI_SOURCE_REF"] = "develop",
                ["GIT_USERNAME"] = "aetheus-ai-11",
                ["GIT_PASSWORD"] = "secret-token",
                ["AI_PROVIDER_TOKEN"] = "provider-token"
            };

            var output = new List<string>();
            var result = await executor.ExecuteAsync(
                OperationKind.AiRun,
                "ai-run",
                environment,
                30,
                (message, _) =>
                {
                    output.Add(message);
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.ExitCode == 0, string.Join(Environment.NewLine, output));
            Assert.NotNull(cloneEnvironment);
            Assert.Equal("1", cloneEnvironment["GIT_CONFIG_COUNT"]);
            Assert.StartsWith(
                "Authorization: Basic ",
                cloneEnvironment["GIT_CONFIG_VALUE_0"],
                StringComparison.Ordinal);
            Assert.NotNull(runnerEnvironment);
            Assert.Equal("provider-token", runnerEnvironment["AI_PROVIDER_TOKEN"]);
            Assert.DoesNotContain("GIT_USERNAME", runnerEnvironment.Keys);
            Assert.DoesNotContain("GIT_PASSWORD", runnerEnvironment.Keys);
            Assert.NotNull(published);
            Assert.Equal(13, published.SourceRepositoryId);
            Assert.Equal(commit, published.BaseCommitSha);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BoundedRunnerSignalsTruncationToBackend()
    {
        var workDirectory = Path.Combine(
            Path.GetTempPath(), "aetheus-ai-truncation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var api = Substitute.For<IServerApiClient>();
            var shell = Substitute.For<IShellRunner>();
            PublishAiRunResultRequest? published = null;
            shell.RunExecAsync(
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<string>(),
                    false,
                    1024,
                    Arg.Any<CancellationToken>(),
                    Arg.Any<TimeSpan?>())
                .Returns(new ShellExecResult(
                    0,
                    "VERDICT: PASS",
                    string.Empty,
                    Truncated: true));
            api.PublishAiRunResultAsync(
                    Arg.Do<PublishAiRunResultRequest>(request => published = request),
                    Arg.Any<CancellationToken>())
                .Returns(new AiRunResultDto { Id = 1, ServerTaskId = 61, Succeeded = true });
            var executor = new AiRunOperationExecutor(
                api,
                shell,
                Options.Create(new AetheusAgentOptions { WorkDirectory = workDirectory }),
                TimeProvider.System,
                NullLogger<AiRunOperationExecutor>.Instance);

            var result = await executor.ExecuteAsync(
                OperationKind.AiRun,
                "ai-run",
                new Dictionary<string, string>
                {
                    ["AETHEUS_TASK_ID"] = "61",
                    ["AETHEUS_AI_PROFILE_NAME"] = "bounded",
                    ["AETHEUS_AI_BINARY"] = "mock-ai",
                    ["AETHEUS_AI_ARGS_JSON"] = "[]",
                    ["AETHEUS_AI_PROMPT"] = "Review.",
                    ["AETHEUS_AI_MAX_OUTPUT_BYTES"] = "1024"
                },
                30,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(published);
            Assert.True(published.Truncated);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);
        }
    }
}
