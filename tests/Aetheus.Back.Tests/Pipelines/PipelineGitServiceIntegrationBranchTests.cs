// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services.Email;
using Aetheus.Back.Tests.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-38 against a REAL temporary bare repository ({root}/{projectId}/{slug}.git): a pipeline written from the editor on
/// the default branch (<c>main</c>) must not make <c>main</c> diverge from the releases built on <c>develop</c>, or the
/// deploy step "Advance main" can no longer fast-forward it. Mocking git would prove nothing here, the property is
/// about what git itself says of the two branches afterwards, checked with the real <see cref="GitBranchAdvanceService"/>.
/// </summary>
public sealed class PipelineGitServiceIntegrationBranchTests : IDisposable
{
    private const int ProjectId = 3;
    private const string Slug = "aetheus";
    private const string PipelineName = "aetheus-demo";
    private const string PipelineFile = ".pipeline/aetheus-demo.yaml";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-r38-{Guid.NewGuid():N}");
    private readonly string _bare;
    private readonly string _work;
    private readonly IGitLightService _gitService = Substitute.For<IGitLightService>();
    private readonly IGitLightRepository _lightRepo = Substitute.For<IGitLightRepository>();
    private readonly PipelineGitService _sut;
    private readonly GitBranchAdvanceService _advance;

    public PipelineGitServiceIntegrationBranchTests()
    {
        _bare = Path.Combine(_root, ProjectId.ToString(), $"{Slug}.git");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.GetDirectoryName(_bare)!);
        RunGit(_root, $"init --bare --initial-branch main \"{_bare}\"");
        RunGit(_root, $"clone \"{_bare}\" \"{_work}\"");
        RunGit(_work, "config user.email tester@aetheus.test");
        RunGit(_work, "config user.name Tester");
        GitFixtureGuard.AssertOwnedBy(_work, _root);
        GitFixtureGuard.AssertOwnedBy(_bare, _root);

        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        var cli = new GitLightCliService(
            runner, new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);

        List<GitLightRepoDto> repos = [new GitLightRepoDto
        {
            Id = 1, ProjectId = ProjectId, Slug = Slug, DefaultBranch = "main", IsEmpty = false
        }];
        _gitService.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(repos);
        _gitService.ResolveDiskPath(ProjectId, Slug).Returns(_bare);
        var senderResolver = Substitute.For<IEmailSenderConfigurationResolver>();
        senderResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution());
        _sut = new PipelineGitService(
            _gitService, cli, Substitute.For<IPipelineRepository>(), Substitute.For<IGitCommitRecorder>(),
            senderResolver, NullLogger<PipelineGitService>.Instance);

        _lightRepo.GetByProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new Aetheus.Back.Data.Entities.GitInternalRepo { Id = 1, ProjectId = ProjectId, Slug = Slug, Name = "Aetheus" }]);
        _lightRepo.GetBranchProtectionRulesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<Aetheus.Back.Data.Entities.BranchProtectionRule>());
        _advance = new GitBranchAdvanceService(
            _lightRepo, runner, Substitute.For<IGitBranchMovedAnnouncer>(),
            Options.Create(new GitLightOptions { RepositoriesPath = _root }), NullLogger<GitBranchAdvanceService>.Instance);
    }

    [Fact]
    public async Task EditorWriteOnMain_IsMergedIntoDevelop_SoTheNextCandidateCanStillAdvanceMain()
    {
        SeedMainAndDevelopAhead();

        var (outcome, error) = await _sut.WriteProjectPipelineYamlAsync(
            ProjectId, PipelineName, "name: aetheus-demo\nschedule: nightly\n", "someone",
            TestContext.Current.CancellationToken, sourceBranch: "main");

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Null(error);
        Assert.Contains("schedule: nightly", RunGit(_bare, $"show main:{PipelineFile}"), StringComparison.Ordinal);
        // develop carries the change through a merge, so main's tip is one of its ancestors.
        Assert.Contains("schedule: nightly", RunGit(_bare, $"show develop:{PipelineFile}"), StringComparison.Ordinal);
        RunGit(_bare, "merge-base --is-ancestor main develop");

        // The deploy of a candidate built on develop's head: "Advance main" fast-forwards instead of refusing.
        var candidate = RunGit(_bare, "rev-parse develop").Trim();
        var result = await _advance.TryFastForwardAsync(ProjectId, "main", candidate, TestContext.Current.CancellationToken);
        Assert.Equal(GitBranchAdvanceOutcome.Advanced, result.Outcome);
        Assert.Equal(candidate, RunGit(_bare, "rev-parse main").Trim());
    }

    [Fact]
    public async Task EditorUpdateOnMain_ThroughApplyChange_IsMergedIntoDevelop_AndNamesNoDivergence()
    {
        SeedMainAndDevelopAhead();
        var location = new PipelineGitDefinitionLocation(ProjectId, PipelineName, "main");

        var (outcome, error) = await _sut.ApplyProjectPipelineChangeAsync(
            location, location, "name: aetheus-demo\nschedule: weekly\n", "someone", TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Null(error);
        RunGit(_bare, "merge-base --is-ancestor main develop");
        var candidate = RunGit(_bare, "rev-parse develop").Trim();
        var result = await _advance.TryFastForwardAsync(ProjectId, "main", candidate, TestContext.Current.CancellationToken);
        Assert.Equal(GitBranchAdvanceOutcome.Advanced, result.Outcome);
    }

    [Fact]
    public async Task EditorDeleteOnMain_IsMergedIntoDevelopToo()
    {
        SeedMainAndDevelopAhead();
        var location = new PipelineGitDefinitionLocation(ProjectId, PipelineName, "main");

        var (outcome, error) = await _sut.ApplyProjectPipelineChangeAsync(
            location, null, null, "someone", TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Null(error);
        RunGit(_bare, "merge-base --is-ancestor main develop");
        Assert.Equal(string.Empty, RunGit(_bare, "ls-tree -r --name-only develop -- .pipeline").Trim());
    }

    [Fact]
    public async Task EditorWriteOnMain_WithoutADevelopBranch_CreatesNoBranchAndTouchesOnlyMain()
    {
        SeedMain();

        var (outcome, error) = await _sut.WriteProjectPipelineYamlAsync(
            ProjectId, PipelineName, "name: aetheus-demo\nschedule: nightly\n", "someone",
            TestContext.Current.CancellationToken, sourceBranch: "main");

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Null(error);
        Assert.Equal("main", RunGit(_bare, "branch --format=%(refname:short)").Trim());
    }

    [Fact]
    public async Task EditorWriteOnAnotherBranch_LeavesDevelopAlone()
    {
        SeedMainAndDevelopAhead();
        RunGit(_work, "checkout -q -b release main");
        RunGit(_work, "push -q origin release");
        var developBefore = RunGit(_bare, "rev-parse develop").Trim();

        var (outcome, error) = await _sut.WriteProjectPipelineYamlAsync(
            ProjectId, PipelineName, "name: aetheus-demo\nschedule: nightly\n", "someone",
            TestContext.Current.CancellationToken, sourceBranch: "release");

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Null(error);
        Assert.Equal(developBefore, RunGit(_bare, "rev-parse develop").Trim());
    }

    [Fact]
    public async Task EditorWriteOnMain_WhenDevelopConflicts_StaysCommittedAndSaysWhatToDo()
    {
        SeedMainAndDevelopAhead();
        // develop changed the very file the editor writes on main: the merge cannot be made.
        RunGit(_work, "checkout -q develop");
        File.WriteAllText(Path.Combine(_work, PipelineFile), "name: aetheus-demo\nschedule: hourly\n");
        RunGit(_work, $"commit -q -am develop-edit");
        RunGit(_work, "push -q origin develop");

        var (outcome, error) = await _sut.WriteProjectPipelineYamlAsync(
            ProjectId, PipelineName, "name: aetheus-demo\nschedule: nightly\n", "someone",
            TestContext.Current.CancellationToken, sourceBranch: "main");

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Contains("schedule: nightly", RunGit(_bare, $"show main:{PipelineFile}"), StringComparison.Ordinal);
        Assert.NotNull(error);
        Assert.Contains("'develop' could not take it back", error, StringComparison.Ordinal);
        Assert.Contains("merge 'main' into 'develop' by hand", error, StringComparison.Ordinal);
        // The conflicting merge left develop where it was, with no half-merged state.
        Assert.Contains("schedule: hourly", RunGit(_bare, $"show develop:{PipelineFile}"), StringComparison.Ordinal);
    }

    // main holds the pipeline file; develop is main plus one commit of unreleased work (the shape of the real repository).
    private void SeedMainAndDevelopAhead()
    {
        SeedMain();
        RunGit(_work, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(_work, "feature.txt"), "unreleased\n");
        RunGit(_work, "add feature.txt");
        RunGit(_work, "commit -q -m feature");
        RunGit(_work, "push -q origin develop");
        RunGit(_work, "checkout -q main");
    }

    private void SeedMain()
    {
        Directory.CreateDirectory(Path.Combine(_work, ".pipeline"));
        File.WriteAllText(Path.Combine(_work, PipelineFile), "name: aetheus-demo\nschedule: daily\n");
        RunGit(_work, "add .");
        RunGit(_work, "commit -q -m initial");
        RunGit(_work, "branch -M main");
        RunGit(_work, "push -q origin main");
    }

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
        // WHY: hook env inheritance incident - GIT_DIR/GIT_INDEX_FILE would redirect this fixture git call to the real repository.
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{git}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(20000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed ({process.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            GitProcessRunner.ClearReadOnlyAttributes(_root);
            Directory.Delete(_root, true);
        }
        catch (IOException) { /* best effort: a temp directory left behind fails nothing */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }
}
