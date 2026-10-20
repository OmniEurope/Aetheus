// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The source tile's commit links to the recorded commit when the run has one, else to the
/// commit in the run's repository, else to the project's repositories; a run with no commit shows none.</summary>
public sealed class PipelineRunSourceTileTests : BunitContext
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    public PipelineRunSourceTileTests() => BunitTestHelper.RegisterServices(this);

    private ShortId? Commit(PipelineRunDto run, int? repoId) =>
        Render<PipelineRunSourceTile>(p => p.Add(x => x.Run, run).Add(x => x.RepoId, repoId))
            .FindComponents<ShortId>().SingleOrDefault()?.Instance;

    [Fact]
    public void ARecordedCommit_LinksToItInTheRepository()
    {
        var run = new PipelineRunDto { ProjectId = 4, Commits = [new CommitLinkDto { Id = 9, Sha = Sha }] };

        var commit = Commit(run, repoId: 2);

        Assert.Equal((Sha, $"/git-repositories/2/commits/{Sha}"), (commit!.Value, commit.Href));
    }

    [Theory]
    [InlineData(2, 4, "/git-repositories/2/commits/" + Sha)]
    [InlineData(null, 4, "/git-repositories?projectId=4")]
    [InlineData(null, null, null)]
    public void AHashOnly_LinksToTheRepositoryOrTheProject(int? repoId, int? projectId, string? expected)
    {
        var run = new PipelineRunDto { ProjectId = projectId, CommitHash = Sha };

        var commit = Commit(run, repoId);

        Assert.Equal((Sha, expected), (commit!.Value, commit.Href));
    }

    [Fact]
    public void NoCommitAtAll_ShowsNoCommit()
    {
        var run = new PipelineRunDto { ProjectId = 4, BranchName = "main" };

        Assert.Null(Commit(run, repoId: 2));
    }
}
