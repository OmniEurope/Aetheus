// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using GitFixtureGuard = Aetheus.Back.Tests.Git.GitFixtureGuard;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The incident of 2026-10-22: the deploy pipeline moves <c>main</c> with the backend's own ref update,
/// no push happens, so the stored definitions (whose cron the scheduler reads) stayed at the state of
/// the last real push. This walks the whole path with a REAL bare repository, the real advance service,
/// the real announcer, the real dispatcher and the real synchronizer; only the pipeline persistence is
/// a double, which is what records what would have been stored. It proves the stored definition follows
/// the branch and that nothing a push would trigger is triggered.
/// </summary>
public sealed class BranchAdvanceDefinitionSyncTests : IDisposable
{
    private const int ProjectId = 7;
    private const string Slug = "aetheus";
    private const string Stale = "name: aetheus-demo\ntrigger: schedule\nschedule: \"0 0 3 * * *\"\nsource_branch: main\nstages: []\n";
    private const string Fresh = "name: aetheus-demo\ntrigger: schedule\nschedule: \"0 0 2 * * *\"\nsource_branch: main\nstages: []\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-advance-sync-{Guid.NewGuid():N}");
    private readonly string _bare;
    private readonly string _work;
    private readonly IPipelineService _pipelines = Substitute.For<IPipelineService>();
    private readonly IDomainEventHandler<GitPushProcessedEvent> _pushHandler =
        Substitute.For<IDomainEventHandler<GitPushProcessedEvent>>();
    private readonly GitBranchAdvanceService _advance;

    public BranchAdvanceDefinitionSyncTests()
    {
        _bare = Path.Combine(_root, ProjectId.ToString(), $"{Slug}.git");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.GetDirectoryName(_bare)!);
        RunGit(_root, $"init --bare --initial-branch main \"{_bare}\"");
        RunGit(_root, $"clone \"{_bare}\" \"{_work}\"");
        RunGit(_work, "config user.email tester@aetheus.test");
        RunGit(_work, "config user.name Tester");
        GitFixtureGuard.AssertOwnedBy(_work, _root);

        var repositories = Substitute.For<IGitLightRepository>();
        repositories.GetByProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitInternalRepo
            {
                Id = 3, ProjectId = ProjectId, Slug = Slug, Name = "Aetheus", DefaultBranch = "main"
            }]);
        repositories.GetBranchProtectionRulesAsync(3, Arg.Any<CancellationToken>())
            .Returns(new List<BranchProtectionRule>());

        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        var cli = new GitLightCliService(
            runner, new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);

        // The registrations the Pipelines module makes for the two events, built by hand because the
        // module's own wiring needs the whole application.
        var services = new ServiceCollection();
        services.AddSingleton(_pushHandler);
        services.AddSingleton<IDomainEventHandler<GitBranchMovedEvent>>(
            new GitBranchMovedPipelineSyncHandler(new PipelineDefinitionSynchronizer(
                _pipelines, cli, NullLogger<PipelineDefinitionSynchronizer>.Instance)));
        var dispatcher = new DomainEventDispatcher(
            services.BuildServiceProvider(), Substitute.For<IBackgroundTaskQueue>(),
            NullLogger<DomainEventDispatcher>.Instance);

        _advance = new GitBranchAdvanceService(
            repositories, runner, new GitBranchMovedAnnouncer(dispatcher),
            Options.Create(new GitLightOptions { RepositoriesPath = _root }),
            NullLogger<GitBranchAdvanceService>.Instance);
    }

    [Fact]
    public async Task AdvancingMain_SynchronizesTheDefinitionsOfTheDeployedCommit_AndTriggersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var stale = CommitDefinition(Stale);
        PushRef("refs/heads/main", stale);
        var deployed = CommitDefinition(Fresh);

        var result = await _advance.TryFastForwardAsync(ProjectId, "main", deployed, ct);

        Assert.Equal(GitBranchAdvanceOutcome.Advanced, result.Outcome);
        Assert.Equal(deployed, RunGit(_bare, "rev-parse --verify refs/heads/main").Trim());
        // The stored definition receives the content of the deployed commit, not of the old tip, for
        // the branch that moved and with the repository the commit lives in.
        await _pipelines.Received(1).UpsertPipelineFromYamlAsync(
            "aetheus-demo", Fresh, ProjectId, "schedule", Arg.Any<CancellationToken>(), "main", "main", 3);
        // A branch the server moves is not a push: no pipeline is looked up for triggering and the
        // push subscriber is never invoked.
        await _pipelines.DidNotReceiveWithAnyArgs().GetWebhookTriggeredPipelinesForProjectAsync(default, TestContext.Current.CancellationToken);
        await _pushHandler.DidNotReceiveWithAnyArgs().HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AdvancingMain_ToACommitThatNeedsNoMove_SynchronizesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var only = CommitDefinition(Fresh);
        PushRef("refs/heads/main", only);

        var result = await _advance.TryFastForwardAsync(ProjectId, "main", only, ct);

        Assert.Equal(GitBranchAdvanceOutcome.AlreadyUpToDate, result.Outcome);
        await _pipelines.DidNotReceiveWithAnyArgs().UpsertPipelineFromYamlAsync(
            default!, default!, default, default!, TestContext.Current.CancellationToken, default, default, default);
    }

    private string CommitDefinition(string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_work, ".pipeline"));
        File.WriteAllText(Path.Combine(_work, ".pipeline", "aetheus-demo.yaml"), yaml);
        RunGit(_work, "add .pipeline/aetheus-demo.yaml");
        RunGit(_work, $"commit -m \"definition {Guid.NewGuid():N}\"");
        var sha = RunGit(_work, "rev-parse HEAD").Trim();
        // Parked on a private ref so the bare repository holds the commit before any branch names it.
        RunGit(_work, $"push --quiet origin +{sha}:refs/fixtures/objects");
        return sha;
    }

    private void PushRef(string reference, string sha) =>
        RunGit(_work, $"push --quiet origin +{sha}:{reference}");

    private static string RunGit(string workDir, string args)
    {
        var git = ExecutableLocator.Require("git");
        var psi = new ProcessStartInfo(git, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // WHY: pre-push hook env inheritance incident - GIT_DIR/GIT_INDEX_FILE would redirect this
        // fixture git call to the real repository.
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Could not start '{git}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(20000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed ({process.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        GitProcessRunner.ClearReadOnlyAttributes(_root);
        Directory.Delete(_root, true);
    }
}
