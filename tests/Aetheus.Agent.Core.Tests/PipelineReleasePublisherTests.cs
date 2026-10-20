// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>A release step creates the release on the server with the revision of the working tree and,
/// when asked, the changelog read from a real git history.</summary>
public sealed class PipelineReleasePublisherTests : IDisposable
{
    private readonly string _repository = Directory.CreateTempSubdirectory("aetheus-release-publisher-").FullName;
    private readonly IServerApiClient _api = Substitute.For<IServerApiClient>();
    private readonly List<(string Line, TaskLogLevel Level)> _output = [];

    public PipelineReleasePublisherTests() =>
        _api.CreateReleaseAsync(default, default, default!, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new ReleaseCreatedResponse { Id = 12, Version = "1.4.0", BuildNumber = 3 });

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_repository, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_repository, recursive: true);
    }

    private Task<ExecutorResult> PublishAsync(bool changelog) =>
        PipelineReleasePublisher.PublishAsync(_api, NullLogger.Instance, "1.4.0",
            new Dictionary<string, string>
            {
                ["AETHEUS_PROJECT_ID"] = "4",
                ["AETHEUS_RUN_ID"] = "9",
                ["AETHEUS_WORKING_DIR"] = _repository,
                ["AETHEUS_CHANGELOG"] = changelog ? "true" : "false",
            },
            (line, level) => { _output.Add((line, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task InAGitTree_TheChangelogAndRevisionComeFromItsHistory()
    {
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@aetheus.invalid");
        Git("config", "user.name", "Aetheus Tests");
        Git("commit", "--allow-empty", "-m", "feat: first change");
        Git("commit", "--allow-empty", "-m", "fix: second change");
        var head = Git("rev-parse", "HEAD");

        var result = await PublishAsync(changelog: true);

        Assert.Equal(0, result.ExitCode);
        await _api.Received(1).CreateReleaseAsync(4, 9, "1.4.0",
            Arg.Is<string?>(log => log!.Contains("fix: second change", StringComparison.Ordinal)
                && log.Contains("feat: first change", StringComparison.Ordinal)),
            head, "main", null, false, Arg.Any<CancellationToken>());
        Assert.Contains(_output, entry => entry.Line.StartsWith("Changelog: ", StringComparison.Ordinal));
        Assert.Contains(_output, entry => entry.Line == "##aetheus[setvariable name=RELEASE_ID]12");
    }

    [Fact]
    public async Task OutsideAGitTree_TheChangelogIsSkippedWithAWarning()
    {
        var result = await PublishAsync(changelog: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_output, entry => entry.Level == TaskLogLevel.Warning && entry.Line == "No .git directory found, skipping changelog");
        await _api.Received(1).CreateReleaseAsync(4, 9, "1.4.0", null, null, null, null, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHistoryGitCannotRead_GivesNoChangelog()
    {
        Directory.CreateDirectory(Path.Combine(_repository, ".git"));

        var result = await PublishAsync(changelog: true);

        Assert.Equal(0, result.ExitCode);
        await _api.Received(1).CreateReleaseAsync(4, 9, "1.4.0", null, Arg.Any<string?>(), Arg.Any<string?>(), null, false, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(_output, entry => entry.Line.StartsWith("Changelog: ", StringComparison.Ordinal));
    }

    private string Git(params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = _repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        // A repository-local git variable inherited from a hook would point this call at the real repository.
        GitRepositoryEnvironment.Neutralize(startInfo.Environment);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }
}
