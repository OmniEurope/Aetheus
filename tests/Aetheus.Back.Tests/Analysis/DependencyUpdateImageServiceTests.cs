// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.DependencyUpdates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-24: the automatic update of a base image, from the candidate to the CI launch. The plan over the Dockerfile is the real
/// <see cref="DependencyBumper"/>; the registry, the git write and the pipeline launch are doubles, and the rows are real EF rows (in
/// memory). So this proves the orchestration and the refusal reasons, not a real registry call, a real push nor a real CI run.
/// </summary>
public sealed class DependencyUpdateImageServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 4, 30, 0, TimeSpan.Zero);
    private const string Image = "mcr.microsoft.com/dotnet/aspnet";
    private const string Old = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string New = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Dockerfile = "deploy/docker/Dockerfile.back";
    private const string Branch = "aetheus/deps/mcr.microsoft.com-dotnet-aspnet-10.0.15";

    private readonly AppDbContext _db;
    private readonly ICveWatchQueryService _query = Substitute.For<ICveWatchQueryService>();
    private readonly FakeGit _git;
    private readonly FakePipelines _pipelines = new();
    private readonly FakeDigests _digests = new();
    private readonly DependencyUpdateService _service;
    private readonly GitInternalRepo _repo = new() { Id = 7, ProjectId = 3, Name = "app", Slug = "app", DefaultBranch = "develop" };

    public DependencyUpdateImageServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(Now));
        _git = new FakeGit(_repo) { DockerfileText = $"FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build\nFROM {Image}:10.0.14@{Old} AS final\n" };
        var repository = new DependencyUpdateRepository(_db);
        _service = new DependencyUpdateService(repository, _query, _git,
            new DependencyUpdateCiLaunch(repository, _pipelines, new FakeTimeProvider(Now)), new NoLockRefresh(), _digests,
            [new NoLicenses()], new FakeTimeProvider(Now), NullLogger<DependencyUpdateService>.Instance);
        _digests.Answers["10.0.15"] = ImageDigestAnswer.Found(New);
        Recommend(CveRecommendationKind.BumpBaseImage, "10.0.15");
        _db.Pipelines.AddRange(
            new Pipeline { Id = 11, ProjectId = 3, Name = "app-ci", TemplateReferenceName = "application-ci" },
            new Pipeline { Id = 12, ProjectId = 3, Name = "app-candidate", TemplateReferenceName = "application-candidate" },
            new Pipeline { Id = 13, ProjectId = 3, Name = "app-deploy", TemplateReferenceName = "application-deploy" },
            new Pipeline { Id = 14, ProjectId = 3, Name = "app-qa", TemplateReferenceName = "application-qa" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static DependencyUpdateCandidate Candidate(string version = $"10.0.14@{Old}") =>
        new(1, 2, 3, 7, "docker", Image, version, Dockerfile, "CVE-2026-111497", "10.0.15");

    private void Recommend(CveRecommendationKind kind, string? target) =>
        _query.GetDetailAsync(Arg.Any<long>(), null, null, Arg.Any<CancellationToken>())
            .Returns(new CveWatchMatchDto
            {
                MatchId = 1,
                Recommendation = new CveWatchRecommendationDto { Kind = kind, TargetVersion = target }
            });

    private DependencyUpdate Row() => Assert.Single(_db.DependencyUpdates.AsNoTracking());

    private void AssertRefusedWithoutAnyWrite(string reasonPart)
    {
        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains(reasonPart, row.Reason, StringComparison.Ordinal);
        Assert.Null(row.Branch);
        Assert.Empty(_git.Writes);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task Evaluate_DigestPinnedImage_MovesToTheTagAndDigestOfThePatch_WritesTheBranchAndLaunchesTheCiOnly()
    {
        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.BranchPushed, row.Status);
        Assert.Equal(("docker", Image, $"10.0.14@{Old}", "10.0.15", Branch), (row.Ecosystem, row.PackageName, row.FromVersion, row.TargetVersion, row.Branch));
        Assert.False(row.IsMajorUpgrade);
        Assert.Null(row.License);
        Assert.Equal(New, Assert.Single(_git.Requests).TargetDigest);
        var write = Assert.Single(_git.Writes);
        var file = Assert.Single(write.Files);
        Assert.Equal(Dockerfile, file.Path);
        Assert.Equal($"FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build\nFROM {Image}:10.0.15@{New} AS final\n", file.Content);
        Assert.Equal([(11, Branch, "abc123", row.Id)], _pipelines.Launches);
        Assert.Equal((11, 14), (row.CiPipelineId, row.QaPipelineId));
    }

    [Fact]
    public async Task Evaluate_TagOnlyImage_AsksNoRegistryAndWritesNoDigest()
    {
        _git.DockerfileText = $"FROM {Image}:10.0.14 AS final\n";

        Assert.True(await _service.EvaluateAsync(Candidate("10.0.14"), TestContext.Current.CancellationToken));

        Assert.Empty(_digests.Asked);
        Assert.Null(Assert.Single(_git.Requests).TargetDigest);
        Assert.Equal($"FROM {Image}:10.0.15 AS final\n", Assert.Single(Assert.Single(_git.Writes).Files).Content);
    }

    [Fact]
    public async Task Evaluate_DigestPinnedVariantImage_KeepsItsVariantInTheResolvedTag()
    {
        _git.DockerfileText = $"FROM {Image}:10.0.14-noble@{Old}\n";
        _digests.Answers["10.0.15-noble"] = ImageDigestAnswer.Found(New);

        Assert.True(await _service.EvaluateAsync(Candidate($"10.0.14-noble@{Old}"), TestContext.Current.CancellationToken));

        Assert.Equal([(Image, "10.0.15-noble")], _digests.Asked);
        Assert.Equal($"FROM {Image}:10.0.15-noble@{New}\n", Assert.Single(Assert.Single(_git.Writes).Files).Content);
        Assert.Equal("aetheus/deps/mcr.microsoft.com-dotnet-aspnet-10.0.15-noble", Row().Branch);
    }

    [Fact]
    public async Task Evaluate_DigestUnresolvable_IsRefusedWithTheRegistrysReason_AndNoTagOnlyLineIsWritten()
    {
        _digests.Answers["10.0.15"] =
            ImageDigestAnswer.Failed("The registry mcr.microsoft.com did not answer for mcr.microsoft.com/dotnet/aspnet:10.0.15 (HttpRequestException).");

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        AssertRefusedWithoutAnyWrite("did not answer");
        Assert.Contains("No tag-only line is written", Row().Reason, StringComparison.Ordinal);
        Assert.Empty(_git.Requests);
    }

    [Fact]
    public async Task Evaluate_DigestUnresolvable_IsEvaluatedAgainAndWrittenOnceTheRegistryAnswers()
    {
        _digests.Answers["10.0.15"] = ImageDigestAnswer.Failed("no network");
        await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken);
        _digests.Answers["10.0.15"] = ImageDigestAnswer.Found(New);

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        Assert.Equal(DependencyUpdateStatus.BranchPushed, Row().Status);
    }

    [Fact]
    public async Task Evaluate_ImageTheRepositoryDoesNotPin_IsRefusedAsNotDeclared()
    {
        _git.DockerfileText = $"FROM {Image}:10.0.13@{Old}\n";

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        AssertRefusedWithoutAnyWrite("NotDeclared");
    }

    [Theory]
    [InlineData(CveRecommendationKind.NoFixYet, "10.0.15")]
    [InlineData(CveRecommendationKind.EndOfLife, null)]
    public async Task Evaluate_NoFixedVersionKnown_IsRefusedAndNothingIsAsked(CveRecommendationKind kind, string? target)
    {
        Recommend(kind, target);

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        AssertRefusedWithoutAnyWrite("No release of the image");
        Assert.Empty(_digests.Asked);
    }

    [Theory]
    [InlineData("10.1.0")]
    [InlineData("11.0.0")]
    public async Task Evaluate_FixThatIsANewMinorOrMajor_IsRefusedAsNotAPatch_AndNothingIsAsked(string target)
    {
        Recommend(CveRecommendationKind.BumpBaseImage, target);

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        AssertRefusedWithoutAnyWrite("10.0 series");
        Assert.Empty(_digests.Asked);
    }

    [Fact]
    public async Task Evaluate_ImageInAMirror_IsStillRefused()
    {
        _git.IsMirror = true;

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        AssertRefusedWithoutAnyWrite("mirror");
        Assert.Empty(_digests.Asked);
    }

    [Fact]
    public async Task Evaluate_SecondPass_NeverWritesTheSameImageBranchAgain()
    {
        await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken);

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        Assert.Single(_git.Writes);
        Assert.Single(_pipelines.Launches);
    }

    private sealed class FakeDigests : IImageDigestResolver
    {
        public Dictionary<string, ImageDigestAnswer> Answers { get; } = [];

        public List<(string Image, string Tag)> Asked { get; } = [];

        public Task<ImageDigestAnswer> ResolveAsync(string image, string tag, CancellationToken ct)
        {
            Asked.Add((image, tag));
            return Task.FromResult(Answers.GetValueOrDefault(tag) ?? ImageDigestAnswer.Failed("no answer scripted"));
        }
    }

    /// <summary>The plan is the real <see cref="DependencyBumper"/> over the Dockerfile text; only the repository around it is a double.</summary>
    private sealed class FakeGit(GitInternalRepo repo) : IDependencyUpdateGit
    {
        public bool IsMirror { get; set; }

        public string DockerfileText { get; set; } = string.Empty;

        public List<DependencyBumpRequest> Requests { get; } = [];

        public List<(string Branch, string BaseCommit, IReadOnlyList<(string Path, string Content)> Files, string Message)> Writes { get; } = [];

        public Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct) =>
            Task.FromResult(repositoryId == repo.Id && projectId == repo.ProjectId ? new CveRemediationRepositoryRow(repo, IsMirror) : null);

        public Task<CveRemediationPlan> PlanAsync(int repositoryId, string baseBranch, DependencyBumpRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new CveRemediationPlan(
                DependencyBumper.Plan(request, new Dictionary<string, string> { [Dockerfile] = DockerfileText }), "base1", null));
        }

        public Task<DependencyUpdateWrite> WriteAsync(
            GitInternalRepo repository, string branch, string baseCommit, IReadOnlyList<(string Path, string Content)> files, string message,
            CancellationToken ct)
        {
            Writes.Add((branch, baseCommit, files, message));
            return Task.FromResult(new DependencyUpdateWrite("abc123", null));
        }
    }

    private sealed class NoLicenses : IRegistryLicenseReader
    {
        public string Ecosystem => "NuGet";

        public Task<RegistryLicenseAnswer> ReadAsync(string packageId, string version, CancellationToken ct) =>
            throw new NotSupportedException("A base image has no license to read.");
    }

    private sealed class NoLockRefresh : IDependencyUpdateLockRefresh
    {
        public Task<DependencyUpdateLockReadiness> CheckAsync(int projectId, string baseBranch, CancellationToken ct) =>
            throw new NotSupportedException("A base image bump has no lock file to refresh.");

        public Task<string?> RecheckAsync(int pipelineId, string owner, CancellationToken ct) =>
            throw new NotSupportedException("A base image bump has no lock file to refresh.");

        public Task<DependencyUpdateLaunch> LaunchAsync(DependencyUpdateLockLaunch launch, CancellationToken ct) =>
            throw new NotSupportedException("A base image bump has no lock file to refresh.");

        public Task<CveRemediationLocks> ReadLocksAsync(int runId, IReadOnlyList<string> paths, CancellationToken ct) =>
            throw new NotSupportedException("A base image bump has no lock file to refresh.");
    }

    private sealed class FakePipelines : IDependencyUpdatePipelines
    {
        public List<(int PipelineId, string Branch, string Commit, int UpdateId)> Launches { get; } = [];

        public Task<DependencyUpdateLaunch> LaunchAsync(int pipelineId, string branch, string commit, int updateId, CancellationToken ct)
        {
            Launches.Add((pipelineId, branch, commit, updateId));
            return Task.FromResult(new DependencyUpdateLaunch(pipelineId * 100, null));
        }
    }
}
