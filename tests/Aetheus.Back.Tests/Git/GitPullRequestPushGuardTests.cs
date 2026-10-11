// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Webhooks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Git;

/// <summary>
/// BACK-013 and decision D13 against a REAL repository and real receive-pack requests: a branch
/// ProtectedExceptPipelines refuses a direct push before git runs while the platform's own moves (the
/// merge of a pull request, the advance-branch step) keep working; StrictlyProtected also refuses the
/// advance-branch step; NotProtected accepts the push. The deletion/force-push protections of the
/// pre-receive hook keep working as before.
/// </summary>
public sealed class GitPullRequestPushGuardTests : IDisposable
{
    private const int ProjectId = 9;
    private const string Slug = "guarded";
    private static readonly string Zero = new('0', 40);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-pr-guard-{Guid.NewGuid():N}");
    private readonly string _bare;
    private readonly string _work;
    private readonly IGitLightRepository _repos = Substitute.For<IGitLightRepository>();
    private readonly GitProcessRunner _runner = new(NullLogger<GitProcessRunner>.Instance);
    private readonly GitSmartHttpService _sut;

    public GitPullRequestPushGuardTests()
    {
        _bare = Path.Combine(_root, ProjectId.ToString(), $"{Slug}.git");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.GetDirectoryName(_bare)!);
        RunGit(_root, $"init --bare --initial-branch main \"{_bare}\"");
        RunGit(_root, $"clone \"{_bare}\" \"{_work}\"");
        RunGit(_work, "config user.email tester@aetheus.test");
        RunGit(_work, "config user.name Tester");
        GitFixtureGuard.AssertOwnedBy(_work, _root);

        var repository = new GitInternalRepo { Id = 1, ProjectId = ProjectId, Slug = Slug, Name = "Guarded" };
        _repos.FindBySlugAsync(ProjectId, Slug, Arg.Any<CancellationToken>()).Returns(repository);
        _repos.GetByProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([repository]);
        Rules(Rule("main", BranchProtectionLevel.ProtectedExceptPipelines));

        var hubClients = Substitute.For<IHubClients>();
        hubClients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hub = Substitute.For<IHubContext<GitRealtimeHub>>();
        hub.Clients.Returns(hubClients);
        _sut = new GitSmartHttpService(
            _repos,
            Substitute.For<IAuthService>(),
            Substitute.For<IWebhookService>(),
            Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<IGitLightCliService>(),
            hub,
            Options.Create(new GitLightOptions { RepositoriesPath = _root }),
            NullLogger<GitSmartHttpService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Substitute.For<IHostApplicationLifetime>());
    }

    public void Dispose()
    {
        GitProcessRunner.ClearReadOnlyAttributes(_root);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task DirectPushToAPullRequestBranch_IsRefusedBeforeGitRuns()
    {
        var first = CommitAndSeed("one");
        var second = Commit("two");

        var refused = await Assert.ThrowsAsync<BadHttpRequestException>(() => PushAsync(first, second, "refs/heads/main"));

        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Equal(first, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task CreatingAPullRequestBranchByPush_IsRefused()
    {
        var first = CommitAndSeed("one", branch: "develop");

        await Assert.ThrowsAsync<BadHttpRequestException>(() => PushAsync(Zero, first, "refs/heads/main"));

        Assert.Null(TryReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task PushToAnUnprotectedBranch_IsAccepted()
    {
        var first = CommitAndSeed("one");
        var second = Commit("two");

        var response = await PushAsync(Zero, second, "refs/heads/feature", basis: first);

        Assert.NotNull(response);
        Assert.Contains(response.UpdatedRefs, update => update.Reference == "refs/heads/feature");
        Assert.Equal(second, ReadBareRef("refs/heads/feature"));
    }

    [Fact]
    public async Task PullRequestMerge_StillMovesTheProtectedBranch()
    {
        var first = CommitAndSeed("one");
        var feature = Commit("feature");
        RunGit(_work, $"push --quiet origin {feature}:refs/heads/feature");
        var cli = new GitLightCliService(
            _runner, new GitLightCliWriter(_runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);

        var (success, mergeSha, error) = await cli.MergeBranchesAsync(
            _bare, "feature", "main", "Reviewer", "reviewer@aetheus.test", TestContext.Current.CancellationToken);

        Assert.True(success, error);
        Assert.NotEqual(first, ReadBareRef("refs/heads/main"));
        Assert.Equal(mergeSha, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task AdvanceBranchStep_StillMovesTheProtectedBranch()
    {
        var first = CommitAndSeed("one");
        var released = Commit("released");
        RunGit(_work, $"push --quiet origin +{released}:refs/fixtures/objects");
        var advance = new GitBranchAdvanceService(
            _repos, _runner, Options.Create(new GitLightOptions { RepositoriesPath = _root }),
            NullLogger<GitBranchAdvanceService>.Instance);

        var result = await advance.TryFastForwardAsync(ProjectId, "main", released, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.Advanced, result.Outcome);
        Assert.Equal(first, result.PreviousSha);
        Assert.Equal(released, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task DeletionAndForcePushProtections_StayWithThePreReceiveHook()
    {
        var first = CommitAndSeed("one", branch: "release");
        var cli = new GitLightCliService(
            _runner, new GitLightCliWriter(_runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);
        await cli.InstallPreReceiveHookAsync(_bare, TestContext.Current.CancellationToken);
        await cli.WriteProtectionConfigAsync(_bare, [("release", true, true)], TestContext.Current.CancellationToken);
        Rules(new BranchProtectionRule { GitInternalRepoId = 1, Pattern = "release", PreventDeletion = true, PreventForcePush = true });

        // The pull-request guard has no say here (the rule does not require one); the hook still refuses.
        RunGit(_work, $"reset --quiet --hard {first}~0");
        var rewritten = Commit("rewritten", amend: true);
        var forced = await PushAsync(first, rewritten, "refs/heads/release");
        var deleted = await PushAsync(first, Zero, "refs/heads/release");

        Assert.True(forced is null || forced.UpdatedRefs.All(update => update.Reference != "refs/heads/release"));
        Assert.True(deleted is null || deleted.UpdatedRefs.All(update => update.Reference != "refs/heads/release"));
        Assert.Equal(first, ReadBareRef("refs/heads/release"));
    }

    [Fact]
    public async Task D13_StrictlyProtected_RefusesTheDirectPush()
    {
        Rules(Rule("main", BranchProtectionLevel.StrictlyProtected));
        var first = CommitAndSeed("one");
        var second = Commit("two");

        var refused = await Assert.ThrowsAsync<BadHttpRequestException>(() => PushAsync(first, second, "refs/heads/main"));

        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Contains("strictly protected", refused.Message, StringComparison.Ordinal);
        Assert.Equal(first, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task D13_StrictlyProtected_StillLetsAPullRequestMergeMoveTheBranch()
    {
        Rules(Rule("main", BranchProtectionLevel.StrictlyProtected));
        var first = CommitAndSeed("one");
        var feature = Commit("feature");
        RunGit(_work, $"push --quiet origin {feature}:refs/heads/feature");
        var cli = new GitLightCliService(
            _runner, new GitLightCliWriter(_runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);

        var (success, mergeSha, error) = await cli.MergeBranchesAsync(
            _bare, "feature", "main", "Reviewer", "reviewer@aetheus.test", TestContext.Current.CancellationToken);

        Assert.True(success, error);
        Assert.NotEqual(first, ReadBareRef("refs/heads/main"));
        Assert.Equal(mergeSha, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task D13_StrictlyProtected_RefusesTheAdvanceBranchStep()
    {
        // A broad NotProtected rule never loosens the stricter rule on main: the strictest level wins.
        Rules(Rule("*", BranchProtectionLevel.NotProtected), Rule("main", BranchProtectionLevel.StrictlyProtected));
        var first = CommitAndSeed("one");
        var released = Commit("released");
        RunGit(_work, $"push --quiet origin +{released}:refs/fixtures/objects");

        var result = await Advance().TryFastForwardAsync(ProjectId, "main", released, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.Protected, result.Outcome);
        Assert.Contains("strictly protected", result.Detail, StringComparison.Ordinal);
        Assert.Equal(first, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task D13_NotProtected_AcceptsTheDirectPushAndTheAdvanceBranchStep()
    {
        Rules(Rule("main", BranchProtectionLevel.NotProtected));
        var first = CommitAndSeed("one");
        var second = Commit("two");
        var third = Commit("three");

        var response = await PushAsync(first, second, "refs/heads/main");
        RunGit(_work, $"push --quiet origin +{third}:refs/fixtures/objects");
        var result = await Advance().TryFastForwardAsync(ProjectId, "main", third, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Contains(response.UpdatedRefs, update => update.Reference == "refs/heads/main");
        Assert.Equal(GitBranchAdvanceOutcome.Advanced, result.Outcome);
        Assert.Equal(third, ReadBareRef("refs/heads/main"));
    }

    private GitBranchAdvanceService Advance() => new(
        _repos, _runner, Options.Create(new GitLightOptions { RepositoriesPath = _root }),
        NullLogger<GitBranchAdvanceService>.Instance);

    private static BranchProtectionRule Rule(string pattern, BranchProtectionLevel level) =>
        new() { GitInternalRepoId = 1, Pattern = pattern, ProtectionLevel = level, PreventDeletion = false, PreventForcePush = false };

    private void Rules(params BranchProtectionRule[] rules) =>
        _repos.GetBranchProtectionRulesAsync(1, Arg.Any<CancellationToken>()).Returns(rules.ToList());

    private async Task<GitSmartHttpResponse?> PushAsync(string oldSha, string newSha, string reference, string? basis = null)
    {
        var body = new MemoryStream();
        var command = Encoding.UTF8.GetBytes($"{oldSha} {newSha} {reference}\0report-status\n");
        body.Write(Encoding.ASCII.GetBytes((command.Length + 4).ToString("x4")));
        body.Write(command);
        body.Write("0000"u8);
        if (newSha != Zero)
            body.Write(PackObjects(newSha, oldSha != Zero ? oldSha : basis));
        body.Position = 0;
        return await _sut.ExecuteServiceAsync(ProjectId, Slug, "git-receive-pack", body, TestContext.Current.CancellationToken);
    }

    private byte[] PackObjects(string tip, string? exclude)
    {
        var psi = new ProcessStartInfo(ExecutableLocator.Require("git"), "pack-objects --stdout --revs --quiet")
        {
            WorkingDirectory = _work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var process = Process.Start(psi)!;
        process.StandardInput.Write(exclude is null ? $"{tip}\n" : $"{tip}\n^{exclude}\n");
        process.StandardInput.Close();
        using var pack = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(pack);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return pack.ToArray();
    }

    // The starting tip is set by a local push straight into the bare repository: no smart HTTP, so no
    // guard, exactly like the history a protected branch already has.
    private string CommitAndSeed(string content, string branch = "main")
    {
        var sha = Commit(content);
        RunGit(_work, $"push --quiet origin +{sha}:refs/heads/{branch}");
        return sha;
    }

    private string Commit(string content, bool amend = false)
    {
        File.WriteAllText(Path.Combine(_work, "file.txt"), content);
        RunGit(_work, "add file.txt");
        RunGit(_work, amend ? $"commit --amend -m \"{content}\"" : $"commit -m \"{content}\"");
        return RunGit(_work, "rev-parse HEAD").Trim();
    }

    private string ReadBareRef(string reference) => TryReadBareRef(reference)
        ?? throw new InvalidOperationException($"{reference} does not exist.");

    private string? TryReadBareRef(string reference)
    {
        var output = RunGit(_bare, $"for-each-ref --format=%(objectname) {reference}").Trim();
        return output.Length == 0 ? null : output;
    }

    private static string RunGit(string workDir, string args)
    {
        var psi = new ProcessStartInfo(ExecutableLocator.Require("git"), args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed ({process.ExitCode}): {stderr}");
        return stdout;
    }
}
