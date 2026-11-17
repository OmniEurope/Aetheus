// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-34: a deployment run lists the head of its source branch, but delivers the candidate release it restores.
/// The release is read back from the run's variables and the project's release record: which commit and version
/// are deployed, and whether the approver must be warned (older than the head, already in production).
/// </summary>
public sealed class PipelineRunDeployedReleasesTests : IDisposable
{
    private const string CandidateCommit = "7abbb42eff9ab68b138774ae556dbf39d7b7d089";
    private const string HeadCommit = "e8c21992aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime At = new(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync(
        string runCommit = HeadCommit, ReleaseStatus status = ReleaseStatus.Published,
        string variables = """{"AETHEUS_CANDIDATE_VERSION":"c-7abbb42"}""")
    {
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "wordmorrow-deploy-prod", ProjectId = 4 });
        _db.Releases.Add(new Release
        {
            Id = 9,
            ProjectId = 4,
            Version = "c-7abbb42",
            Status = status,
            CommitHash = CandidateCommit,
            DetectedAt = At
        });
        _db.PipelineRuns.AddRange(
            new PipelineRun
            {
                Id = 3167,
                PipelineId = 1,
                Status = PipelineStatus.Success,
                StartedAt = At,
                BranchName = "develop",
                CommitHash = runCommit,
                ResolvedVariablesJson = variables
            },
            new PipelineRun
            {
                Id = 3168,
                PipelineId = 1,
                Status = PipelineStatus.Success,
                StartedAt = At,
                BranchName = "develop",
                CommitHash = runCommit,
                ResolvedVariablesJson = """{"OTHER":"x"}"""
            });
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ADeployRunThatRestoresAnOldCandidate_NamesTheCandidateCommitAndVersion_NotTheBranchHead()
    {
        await SeedAsync();

        var resolved = await PipelineRunDeployedReleases.ResolveAsync(_db, [3167], includeAlreadyDeployed: false, Ct);

        var release = Assert.Single(resolved).Value;
        Assert.Equal("c-7abbb42", release.Version);
        Assert.Equal(CandidateCommit, release.CommitHash);
        Assert.Equal(9, release.ReleaseId);
        // The branch head the run checked out stays available, labelled as the source.
        Assert.Equal(HeadCommit, release.SourceCommitHash);
        Assert.Equal("develop", release.SourceBranch);
        Assert.True(release.IsBehindSourceHead);
        Assert.False(release.IsAlreadyDeployed);
    }

    [Fact]
    public async Task TheRepositoryTheRunBuilt_IsResolved_SoTheCommitsLinkToTheirPages()
    {
        await SeedAsync();
        _db.GitInternalRepos.Add(new GitInternalRepo { Id = 21, ProjectId = 4, Slug = "wordmorrow" });
        var run = await _db.PipelineRuns.FindAsync([3167], Ct);
        run!.RepositoryUrl = "https://aetheus.example/git/4/wordmorrow.git";
        await _db.SaveChangesAsync(Ct);

        var release = Assert.Single(await PipelineRunDeployedReleases.ResolveAsync(_db, [3167], false, Ct)).Value;

        Assert.Equal(21, release.RepositoryId);
    }

    [Fact]
    public async Task ACandidateBuiltFromTheHeadTheRunCheckedOut_IsNotBehindTheHead()
    {
        await SeedAsync(runCommit: CandidateCommit);

        var release = Assert.Single(await PipelineRunDeployedReleases.ResolveAsync(_db, [3167], false, Ct)).Value;

        Assert.False(release.IsBehindSourceHead);
        Assert.False(release.NeedsConfirmation);
    }

    [Fact]
    public async Task AReleaseAlreadyInProduction_IsFlaggedOnlyWhenTheApprovalAsksForIt()
    {
        await SeedAsync(runCommit: CandidateCommit, status: ReleaseStatus.Deployed);

        var forApproval = Assert.Single(await PipelineRunDeployedReleases.ResolveAsync(_db, [3167], true, Ct)).Value;
        var forDisplay = Assert.Single(await PipelineRunDeployedReleases.ResolveAsync(_db, [3167], false, Ct)).Value;

        Assert.True(forApproval.IsAlreadyDeployed);
        Assert.True(forApproval.NeedsConfirmation);
        // A finished run marks its own release deployed: that says nothing about what it replaced.
        Assert.False(forDisplay.IsAlreadyDeployed);
    }

    [Fact]
    public async Task ARunNamingNoRelease_OrAReleaseTheProjectDoesNotHave_HasNoDeployedRelease()
    {
        await SeedAsync(variables: """{"AETHEUS_CANDIDATE_VERSION":"c-unknown"}""");

        var resolved = await PipelineRunDeployedReleases.ResolveAsync(_db, [3167, 3168], false, Ct);

        Assert.Empty(resolved);
    }

    [Theory]
    [InlineData("""{"AETHEUS_CANDIDATE_VERSION":"  v1.2.3  "}""", "v1.2.3")]
    [InlineData("""{"AETHEUS_CANDIDATE_VERSION":"$(candidateVersion)"}""", null)]
    [InlineData("""{"AETHEUS_CANDIDATE_VERSION":""}""", null)]
    [InlineData("""{"OTHER":"x"}""", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void TheCandidateVersion_IsReadFromTheRunVariables_AndNeverFromAPlaceholder(string? json, string? expected) =>
        Assert.Equal(expected, PipelineRunDeployedReleases.ReadCandidateVersion(json));

    [Fact]
    public void AShortHash_MatchesTheFullHashItStarts_AndAMissingCommitIsNeverAWarning()
    {
        var shortHash = PipelineRunDeployedReleases.Describe(1, "v1", ReleaseStatus.Published, "7abbb42", "main", CandidateCommit, false);
        var noCommit = PipelineRunDeployedReleases.Describe(1, "v1", ReleaseStatus.Published, null, "main", HeadCommit, false);
        var different = PipelineRunDeployedReleases.Describe(1, "v1", ReleaseStatus.Published, "7abbb42", "main", HeadCommit, false);

        Assert.False(shortHash.IsBehindSourceHead);
        Assert.False(noCommit.IsBehindSourceHead);
        Assert.True(different.IsBehindSourceHead);
    }

    [Fact]
    public async Task TheRunList_GetsTheDeployedRelease_OnlyOnTheRunsThatDeployOne()
    {
        await SeedAsync();
        var rows = new List<PipelineRunDto> { new() { Id = 3167 }, new() { Id = 3168 } };

        var applied = await PipelineRunDeployedReleases.ApplyAsync(_db, rows, Ct);

        Assert.Equal(CandidateCommit, applied.Single(run => run.Id == 3167).DeployedRelease?.CommitHash);
        Assert.Null(applied.Single(run => run.Id == 3168).DeployedRelease);
    }

    [Fact]
    public async Task TheRunLookupPort_ReadsTheSameAnswerForOneRun()
    {
        await SeedAsync(status: ReleaseStatus.Deployed);
        var port = new Aetheus.Back.Components.Pipelines.PipelineRunLookupRepository(_db);

        var release = await port.GetDeployedReleaseAsync(3167, includeAlreadyDeployed: true, Ct);

        Assert.NotNull(release);
        Assert.True(release.IsBehindSourceHead);
        Assert.True(release.IsAlreadyDeployed);
        Assert.Null(await port.GetDeployedReleaseAsync(3168, true, Ct));
    }
}
