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
/// PLAN-007 (recette R-9): the orchestration of one automatic dependency update, with the git and pipeline sides replaced by
/// doubles (the real write goes through <c>CveRemediationGit</c>; nothing here proves a real push nor a real run). The rows are
/// real EF rows (in memory).
/// </summary>
public sealed class DependencyUpdateServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 4, 30, 0, TimeSpan.Zero);
    private const string Props = "Directory.Packages.props";
    private const string Branch = "aetheus/deps/Newtonsoft.Json-13.0.3";

    private readonly AppDbContext _db;
    private readonly ICveWatchQueryService _query = Substitute.For<ICveWatchQueryService>();
    private readonly FakeGit _git;
    private readonly FakePipelines _pipelines = new();
    private readonly FakeLockRefresh _lockRefresh = new();
    private readonly FakeRegistryLicenses _licenses = new("NuGet");
    private readonly FakeRegistryLicenses _npmLicenses = new("npm");
    private readonly FakeRegistryLicenses _packagistLicenses = new("Packagist");
    private readonly DependencyUpdateService _service;
    private readonly GitInternalRepo _repo = new() { Id = 7, ProjectId = 3, Name = "app", Slug = "app", DefaultBranch = "main" };

    public DependencyUpdateServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(Now));
        _git = new FakeGit(_repo);
        var repository = new DependencyUpdateRepository(_db);
        _service = new DependencyUpdateService(repository, _query, _git,
            new DependencyUpdateCiLaunch(repository, _pipelines, new FakeTimeProvider(Now)), _lockRefresh, new NoRegistry(),
            [_licenses, _npmLicenses, _packagistLicenses],
            new FakeTimeProvider(Now),
            NullLogger<DependencyUpdateService>.Instance);
        Recommend(CveRecommendationKind.BumpDependency, "13.0.3");
        _db.Pipelines.AddRange(
            new Pipeline { Id = 11, ProjectId = 3, Name = "app-ci", TemplateReferenceName = "application-ci" },
            new Pipeline { Id = 12, ProjectId = 3, Name = "app-candidate", TemplateReferenceName = "application-candidate" },
            new Pipeline { Id = 13, ProjectId = 3, Name = "app-deploy", TemplateReferenceName = "application-deploy" },
            new Pipeline { Id = 14, ProjectId = 3, Name = "app-qa", TemplateReferenceName = "application-qa" },
            new Pipeline { Id = 15, ProjectId = 3, Name = "app-qa-extended", TemplateReferenceName = "application-qa-extended" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static DependencyUpdateCandidate Candidate() =>
        new(1, 2, 3, 7, "NuGet", "Newtonsoft.Json", "13.0.1", Props, "GHSA-xxxx", "13.0.3");

    private void Recommend(CveRecommendationKind kind, string target) =>
        _query.GetDetailAsync(Arg.Any<long>(), null, null, Arg.Any<CancellationToken>())
            .Returns(new CveWatchMatchDto
            {
                MatchId = 1,
                Recommendation = new CveWatchRecommendationDto { Kind = kind, TargetVersion = target, VerifiedAgainstRegistry = true }
            });

    private void SeedLicense(string license, string version) =>
        _db.AnalysisComponents.Add(new AnalysisComponent
        {
            OrganizationId = 2,
            ProjectId = 3,
            Name = "Newtonsoft.Json",
            Version = version,
            LicensesJson = $"[\"{license}\"]",
            CreatedAt = Now.UtcDateTime
        });

    private DependencyUpdate Row() => Assert.Single(_db.DependencyUpdates.AsNoTracking());

    private void AssertNothingWritten()
    {
        Assert.Empty(_git.Writes);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task Evaluate_FixInTheMajor_WritesTheBranchAndLaunchesTheCiOnly()
    {
        var written = await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken);

        Assert.True(written);
        var row = Row();
        Assert.Equal(DependencyUpdateStatus.BranchPushed, row.Status);
        Assert.Equal(Branch, row.Branch);
        Assert.Equal("abc123", row.CommitSha);
        Assert.Equal("MIT", row.License);
        Assert.True(row.LicenseOfTargetVersion);
        // The QA is found and kept, but launched later, when the CI run succeeds (DependencyUpdateQaFollowUp).
        Assert.Equal((11, 1100, 14, (int?)null), (row.CiPipelineId, row.CiPipelineRunId, row.QaPipelineId, row.QaPipelineRunId));
        Assert.Null(row.Reason);
        var write = Assert.Single(_git.Writes);
        Assert.Equal((Branch, "base1"), (write.Branch, write.BaseCommit));
        Assert.Equal(Props, Assert.Single(write.Files).Path);
        Assert.StartsWith("chore(deps): bump Newtonsoft.Json from 13.0.1 to 13.0.3", write.Message, StringComparison.Ordinal);
        Assert.Equal([(11, Branch, "abc123", row.Id)], _pipelines.Launches);
    }

    [Fact]
    public async Task Evaluate_ScopedNpmPackage_ReadsItsLicenseFromTheNpmRegistryAndWritesTheBranch()
    {
        _npmLicenses.Answer = RegistryLicenseAnswer.Found("MIT");
        Recommend(CveRecommendationKind.BumpDependency, "1.2.4");
        _git.Plan = new CveRemediationPlan(DependencyBumpResult.Done([new DependencyBumpFileEdit("package.json", "{}", [])], []), "base1", null);
        var candidate = new DependencyUpdateCandidate(1, 2, 3, 7, "npm", "@scope/pkg", "1.2.1", "package.json", "GHSA-xxxx", "1.2.4");

        Assert.True(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        Assert.Equal([("@scope/pkg", "1.2.4")], _npmLicenses.Asked);
        Assert.Empty(_licenses.Asked);
        var row = Row();
        Assert.Equal(("npm", "aetheus/deps/scope-pkg-1.2.4", "MIT"), (row.Ecosystem, row.Branch, row.License));
        Assert.True(row.LicenseOfTargetVersion);
        Assert.Equal("package.json", Assert.Single(Assert.Single(_git.Writes).Files).Path);
    }

    /// <summary>R-8: an SBOM that says MIT for the npm target version does not count; the npm registry's manifest of that version does.</summary>
    [Fact]
    public async Task Evaluate_NpmLicense_IsReadFromTheRegistryForTheTargetVersion_NeverFromAnSbom()
    {
        var candidate = await NpmLockCandidateAsync();
        _db.AnalysisComponents.Add(new AnalysisComponent
        {
            OrganizationId = 2,
            ProjectId = 3,
            Name = "lodash",
            Version = "4.17.21",
            PackageUrl = "pkg:npm/lodash@4.17.21",
            LicensesJson = "[\"MIT\"]",
            CreatedAt = Now.UtcDateTime
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _npmLicenses.Answer = RegistryLicenseAnswer.Found("GPL-3.0-only");

        Assert.False(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        Assert.Equal([("lodash", "4.17.21")], _npmLicenses.Asked);
        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("GPL-3.0-only", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
        Assert.Empty(_lockRefresh.Launches);
    }

    /// <summary>R-8: a Packagist registry that does not say (a version it does not list, a timeout) refuses with its own reason, even
    /// when an SBOM names an allowed license for that version.</summary>
    [Fact]
    public async Task Evaluate_PackagistRegistrySilent_IsRefusedWithTheRegistrysReason_NeverTheSbomsLicense()
    {
        var candidate = await PackagistLockCandidateAsync();
        _db.AnalysisComponents.Add(new AnalysisComponent
        {
            OrganizationId = 2,
            ProjectId = 3,
            Name = "monolog",
            Version = "3.5.1",
            PackageUrl = "pkg:composer/monolog/monolog@3.5.1",
            LicensesJson = "[\"MIT\"]",
            CreatedAt = Now.UtcDateTime
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _packagistLicenses.Answer = RegistryLicenseAnswer.Failed("The Packagist registry did not answer for monolog/monolog 3.5.1 (TaskCanceledException).");

        Assert.False(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        Assert.Equal([("monolog/monolog", "3.5.1")], _packagistLicenses.Asked);
        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Equal("The Packagist registry did not answer for monolog/monolog 3.5.1 (TaskCanceledException). The update is not written.", row.Reason);
        AssertNothingWritten();
    }

    /// <summary>R-28: a Packagist package under the same rules as npm: a patch or minor fix in the same major, a license read from the
    /// Packagist registry (R-8), the composer.lock refreshed by the package manager.</summary>
    private Task<DependencyUpdateCandidate> PackagistLockCandidateAsync(string license = "MIT")
    {
        _packagistLicenses.Answer = RegistryLicenseAnswer.Found(license);
        Recommend(CveRecommendationKind.BumpDependency, "3.5.1");
        _git.Plan = new CveRemediationPlan(DependencyBumpResult.Done([new DependencyBumpFileEdit("composer.json", "{}", [])],
            [new DependencyBumpLockRefresh("composer.lock", string.Empty, "composer update monolog/monolog --no-install --no-scripts --no-plugins --no-interaction")]),
            "base1", null);
        return Task.FromResult(new DependencyUpdateCandidate(1, 2, 3, 7, "Packagist", "monolog/monolog", "3.5.0", "composer.lock", "GHSA-xxxx", "3.5.1"));
    }

    [Fact]
    public async Task Evaluate_PackagistPackage_IsWrittenAndItsComposerLockRefreshedAsTheOwnerButNoCiYet()
    {
        var candidate = await PackagistLockCandidateAsync();

        Assert.True(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(("Packagist", DependencyUpdateStatus.LockRefreshing, "aetheus/deps/monolog-monolog-3.5.1", "MIT"),
            (row.Ecosystem, row.Status, row.Branch, row.License));
        Assert.Equal("composer.json", Assert.Single(Assert.Single(_git.Writes).Files).Path);
        var launch = Assert.Single(_lockRefresh.Launches);
        Assert.Equal("( composer update monolog/monolog --no-install --no-scripts --no-plugins --no-interaction )", launch.Command);
        Assert.Equal("[\"composer.lock\"]", row.LockFilesJson);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task Evaluate_PackagistPackageWhoseLicenseForbidsResale_IsRefusedAndWritesNothing()
    {
        var candidate = await PackagistLockCandidateAsync("GPL-3.0-only");

        Assert.False(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("GPL-3.0-only", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
        Assert.Empty(_lockRefresh.Launches);
    }

    [Fact]
    public async Task Evaluate_PackagistFixAcrossAMajor_IsRefusedAsForNpm()
    {
        var candidate = await PackagistLockCandidateAsync();
        Recommend(CveRecommendationKind.BumpDependency, "4.0.0");

        Assert.False(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("not in the major", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public void Selection_PackagistIsAnEcosystemTheUpdatesWrite()
    {
        Assert.Contains("Packagist", DependencyUpdateSelection.Ecosystems);
    }

    [Fact]
    public async Task Evaluate_NpmLockFileWhoseRefreshTheOwnerCannotRun_IsRefusedWithTheReasonAndWritesNothing()
    {
        _lockRefresh.Readiness = DependencyUpdateLockReadiness.Refused("The owner 'owner' of the pipeline 'cve-lock-refresh' is disabled.");
        var candidate = await NpmLockCandidateAsync();

        Assert.False(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("package-lock.json", row.Reason, StringComparison.Ordinal);
        Assert.Contains("is disabled", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
        Assert.Empty(_lockRefresh.Launches);
    }

    [Fact]
    public async Task Evaluate_NpmLockFileRefreshable_WritesTheManifestAndLaunchesTheRefreshAsTheOwnerButNoCiYet()
    {
        var candidate = await NpmLockCandidateAsync();

        Assert.True(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.LockRefreshing, row.Status);
        Assert.Equal(("aetheus/deps/lodash-4.17.21", "abc123"), (row.Branch, row.CommitSha));
        Assert.Equal((40, 4000, "owner", "[\"package-lock.json\"]"), (row.LockPipelineId, row.LockPipelineRunId, row.LockRefreshOwner, row.LockFilesJson));
        Assert.Equal("package.json", Assert.Single(Assert.Single(_git.Writes).Files).Path);
        var launch = Assert.Single(_lockRefresh.Launches);
        Assert.Equal(new DependencyUpdateLockLaunch(40, "owner", "aetheus/deps/lodash-4.17.21", "abc123",
            "( npm install --package-lock-only --ignore-scripts )", row.Id), launch);
        // The CI waits for the lock files: it would test a manifest its lock file contradicts.
        Assert.Empty(_pipelines.Launches);
        Assert.Null(row.CiPipelineRunId);
    }

    [Fact]
    public async Task Evaluate_NpmLockRefreshNotStarted_FailsWithTheReasonKeepsTheBranchAndIsNeverWrittenAgain()
    {
        _lockRefresh.Launch = new DependencyUpdateLaunch(null, "The run was not authorized for the pipeline's owner; its runs list says why.");
        var candidate = await NpmLockCandidateAsync();

        Assert.True(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));
        Assert.False(await _service.EvaluateAsync(candidate, TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Failed, row.Status);
        Assert.Equal("aetheus/deps/lodash-4.17.21", row.Branch);
        Assert.Contains("did not start as 'owner'", row.Reason, StringComparison.Ordinal);
        Assert.Single(_git.Writes);
        Assert.Single(_lockRefresh.Launches);
        Assert.Empty(_pipelines.Launches);
    }

    private Task<DependencyUpdateCandidate> NpmLockCandidateAsync()
    {
        Recommend(CveRecommendationKind.BumpDependency, "4.17.21");
        _git.Plan = new CveRemediationPlan(DependencyBumpResult.Done([new DependencyBumpFileEdit("package.json", "{}", [])],
            [new DependencyBumpLockRefresh("package-lock.json", string.Empty, "npm install --package-lock-only --ignore-scripts")]), "base1", null);
        return Task.FromResult(new DependencyUpdateCandidate(1, 2, 3, 7, "npm", "lodash", "4.17.1", "package-lock.json", "GHSA-xxxx", "4.17.21"));
    }

    [Fact]
    public async Task Evaluate_ProjectWithACandidatePipeline_NeverLaunchesIt()
    {
        // Pipeline 12 extends application-candidate: it would allocate and publish a release, which a deps branch must never do.
        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(_pipelines.Launches, launch => launch.PipelineId == 12);
        Assert.DoesNotContain(_pipelines.Launches, launch => launch.PipelineId is 14 or 15 or 13);
        var row = Row();
        Assert.NotEqual(12, row.QaPipelineId);
        Assert.NotEqual(12, row.CiPipelineId);
    }

    [Fact]
    public async Task Evaluate_ProjectWithoutQaPipeline_SaysSoOnTheRow()
    {
        _db.Pipelines.RemoveRange(_db.Pipelines.Where(pipeline => pipeline.Id == 14 || pipeline.Id == 15));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Null(row.QaPipelineId);
        Assert.Contains("no QA pipeline", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Roles_QaIsFoundByTemplateElseByTheDashQaSuffixAndNeverTheExtendedOne()
    {
        DependencyUpdatePipeline Of(int id, string name, string? template) =>
            new(id, name, template, null, PipelineTriggerType.Manual, string.Empty);

        Assert.Equal(14, DependencyUpdateRoles.Qa([Of(15, "x-qa-extended", "application-qa-extended"), Of(14, "x", "application-qa")])!.Id);
        Assert.Equal(20, DependencyUpdateRoles.Qa([Of(19, "x-qa-extended", null), Of(20, "x-QA", null)])!.Id);
        Assert.Null(DependencyUpdateRoles.Qa([Of(19, "x-qa-extended", "application-qa-extended"), Of(21, "x-candidate", "application-candidate")]));
    }

    [Fact]
    public async Task Evaluate_SecondPass_NeverWritesTheSameBranchAgain()
    {
        await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken);
        var again = await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken);

        Assert.False(again);
        Row();
        Assert.Single(_git.Writes);
        Assert.Single(_pipelines.Launches);
    }

    [Fact]
    public async Task Evaluate_LicenseThatForbidsResale_RecordsTheRefusalAndWritesNothing()
    {
        _licenses.Answer = RegistryLicenseAnswer.Found("GPL-3.0-only");

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("GPL-3.0-only", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_LicenseUnknown_RecordsTheRefusalAndWritesNothing()
    {
        _licenses.Answer = RegistryLicenseAnswer.Failed("The NuGet registry declares no license expression for Newtonsoft.Json 13.0.3.");

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("declares no license expression", row.Reason, StringComparison.Ordinal);
        Assert.Contains("The update is not written.", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_NuGetLicense_IsReadFromTheRegistryForTheTargetVersion_NeverFromAnSbom()
    {
        // R-31: an SBOM that says MIT for the target version does not count; the registry's metadata for that exact version does.
        SeedLicense("MIT", "13.0.3");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _licenses.Answer = RegistryLicenseAnswer.Found("GPL-3.0-only");

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        Assert.Equal([("Newtonsoft.Json", "13.0.3")], _licenses.Asked);
        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("GPL-3.0-only", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_NuGetLicenseFromTheRegistry_IsRecordedAsThatOfTheTargetVersion()
    {
        _licenses.Answer = RegistryLicenseAnswer.Found("MIT OR Apache-2.0");

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal("MIT OR Apache-2.0", row.License);
        Assert.True(row.LicenseOfTargetVersion);
    }

    private const string MajorBranch = "aetheus/deps/Newtonsoft.Json-14.0.0";

    /// <summary>R-2: the only fix is a major version (the watch says MajorUpgrade) and its license is known.</summary>
    private void GivenOnlyAMajorFixes()
    {
        Recommend(CveRecommendationKind.MajorUpgrade, "14.0.0");
        _db.SaveChanges();
    }

    [Fact]
    public async Task Evaluate_CveWhoseOnlyFixIsAMajor_WritesItsOwnMajorBranchFlaggedForReviewAndLaunchesTheCiOnly()
    {
        GivenOnlyAMajorFixes();

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.True(row.IsMajorUpgrade);
        Assert.Equal(DependencyUpdateStatus.BranchPushed, row.Status);
        Assert.Equal(("13.0.1", "14.0.0", MajorBranch), (row.FromVersion, row.TargetVersion, row.Branch));
        var write = Assert.Single(_git.Writes);
        Assert.Equal(MajorBranch, write.Branch);
        Assert.Contains("Major upgrade: it needs a review", write.Message, StringComparison.Ordinal);
        // CI now; the QA is found and follows the CI (DependencyUpdateQaFollowUp). Never the candidate (12) nor the deployment (13).
        Assert.Equal([(11, MajorBranch, "abc123", row.Id)], _pipelines.Launches);
        Assert.Equal((11, 14), (row.CiPipelineId, row.QaPipelineId));
    }

    [Fact]
    public async Task Evaluate_MajorUpgrade_IsGivenTheSameCheckAsAPatchBumpBeforeAnythingIsWritten()
    {
        Recommend(CveRecommendationKind.MajorUpgrade, "14.0.0");
        _licenses.Answer = RegistryLicenseAnswer.Failed("The NuGet registry declares no license expression for Newtonsoft.Json 14.0.0.");

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.True(row.IsMajorUpgrade);
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("license", row.Reason, StringComparison.OrdinalIgnoreCase);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_MajorUpgradeInAMirror_IsStillRefused()
    {
        GivenOnlyAMajorFixes();
        _git.IsMirror = true;

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        Assert.Equal(DependencyUpdateStatus.Refused, Row().Status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_MajorUpgradeWithAnUnusableLockOwner_IsRefusedAndWritesNothing()
    {
        GivenOnlyAMajorFixes();
        _lockRefresh.Readiness = DependencyUpdateLockReadiness.Refused("The owner 'owner' is disabled.");
        _git.Plan = new CveRemediationPlan(DependencyBumpResult.Done([new DependencyBumpFileEdit(Props, "<Project />", [])],
            [new DependencyBumpLockRefresh("src/App/packages.lock.json", "src/App", "dotnet restore --force-evaluate")]), "base1", null);

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.True(row.IsMajorUpgrade);
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("is disabled", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
        Assert.Empty(_lockRefresh.Launches);
    }

    [Fact]
    public async Task Evaluate_MajorUpgradeWithARefreshableLock_RunsTheRefreshAsThePipelineOwnerOnTheMajorBranchAndNoCiYet()
    {
        GivenOnlyAMajorFixes();
        _git.Plan = new CveRemediationPlan(DependencyBumpResult.Done([new DependencyBumpFileEdit(Props, "<Project />", [])],
            [new DependencyBumpLockRefresh("src/App/packages.lock.json", "src/App", "dotnet restore --force-evaluate")]), "base1", null);

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.True(row.IsMajorUpgrade);
        Assert.Equal((DependencyUpdateStatus.LockRefreshing, MajorBranch, "owner"), (row.Status, row.Branch, row.LockRefreshOwner));
        Assert.Equal(MajorBranch, Assert.Single(_lockRefresh.Launches).Branch);
        Assert.Equal("owner", Assert.Single(_lockRefresh.Launches).Owner);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task Evaluate_MajorBranchThenAPatchFixAppears_GivesThePatchItsOwnSeparateBranch()
    {
        GivenOnlyAMajorFixes();
        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));
        Recommend(CveRecommendationKind.BumpDependency, "13.0.3");

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var rows = _db.DependencyUpdates.AsNoTracking().OrderBy(update => update.Id).ToList();
        Assert.Equal([(MajorBranch, true), (Branch, false)], rows.Select(update => (update.Branch!, update.IsMajorUpgrade)));
        Assert.Equal([MajorBranch, Branch], _git.Writes.Select(write => write.Branch));
    }

    [Fact]
    public async Task Evaluate_PatchBranchThenTheWatchSaysMajor_AddsNothingAndAMajorBranchIsNeverWrittenTwice()
    {
        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));
        GivenOnlyAMajorFixes();

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));
        Assert.Single(_git.Writes);

        // A major branch, once written, is not written again by the next pass.
        _db.DependencyUpdates.RemoveRange(_db.DependencyUpdates);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _git.Writes.Clear();
        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));
        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));
        Assert.Single(_git.Writes);
    }

    [Fact]
    public async Task Evaluate_BumpThatCrossesAMajor_IsStillRefusedWhenTheWatchDoesNotCallItAMajorUpgrade()
    {
        Recommend(CveRecommendationKind.BumpDependency, "14.0.0");

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.False(row.IsMajorUpgrade);
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_Mirror_IsRefusedSoTheExternalRepositoryIsNeverWritten()
    {
        _git.IsMirror = true;

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        Assert.Contains("mirror", Row().Reason, StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_LockFileToRefreshWithoutARefreshPipeline_IsRefusedRatherThanCommittingAnInconsistentRepository()
    {
        _lockRefresh.Readiness = DependencyUpdateLockReadiness.Refused("The project has no pipeline named 'cve-lock-refresh' to run the package manager.");
        _git.Plan = new CveRemediationPlan(DependencyBumpResult.Done([new DependencyBumpFileEdit(Props, "<Project />", [])],
            [new DependencyBumpLockRefresh("src/App/packages.lock.json", "src/App", "dotnet restore --force-evaluate")]), "base1", null);

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Refused, row.Status);
        Assert.Contains("packages.lock.json", row.Reason, StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Evaluate_PushRefused_IsRecordedAsFailedAndRunsNothing()
    {
        _git.Answer = new DependencyUpdateWrite(null, "The branch already exists.");

        Assert.False(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Failed, row.Status);
        Assert.Contains("already exists", row.Reason, StringComparison.Ordinal);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task Evaluate_PipelineLaunchRefused_KeepsTheBranchAndSaysWhatWasNotRun()
    {
        _pipelines.Refuse = 11;

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.BranchPushed, row.Status);
        Assert.Null(row.CiPipelineRunId);
        Assert.Null(row.QaPipelineRunId);
        Assert.Contains("the pipeline is disabled", row.Reason, StringComparison.Ordinal);
        Assert.Contains("QA not started", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_RefusedBefore_IsEvaluatedAgainAndWrittenOnceTheCauseIsGone()
    {
        _git.IsMirror = true;
        await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken);
        _git.IsMirror = false;

        Assert.True(await _service.EvaluateAsync(Candidate(), TestContext.Current.CancellationToken));

        Assert.Equal(DependencyUpdateStatus.BranchPushed, Row().Status);
    }

    private sealed class FakeGit(GitInternalRepo repo) : IDependencyUpdateGit
    {
        public bool IsMirror { get; set; }

        public CveRemediationPlan Plan { get; set; } =
            new(DependencyBumpResult.Done([new DependencyBumpFileEdit(Props, "<Project />", [])], []), "base1", null);

        public DependencyUpdateWrite Answer { get; set; } = new("abc123", null);

        public List<(string Branch, string BaseCommit, IReadOnlyList<(string Path, string Content)> Files, string Message)> Writes { get; } = [];

        public Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct) =>
            Task.FromResult(repositoryId == repo.Id && projectId == repo.ProjectId ? new CveRemediationRepositoryRow(repo, IsMirror) : null);

        public Task<CveRemediationPlan> PlanAsync(int repositoryId, string baseBranch, DependencyBumpRequest request, CancellationToken ct) =>
            Task.FromResult(Plan);

        public Task<DependencyUpdateWrite> WriteAsync(
            GitInternalRepo repository, string branch, string baseCommit, IReadOnlyList<(string Path, string Content)> files, string message,
            CancellationToken ct)
        {
            Writes.Add((branch, baseCommit, files, message));
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeLockRefresh : IDependencyUpdateLockRefresh
    {
        public DependencyUpdateLockReadiness Readiness { get; set; } = new(40, "owner", null);

        public DependencyUpdateLaunch Launch { get; set; } = new(4000, null);

        public List<DependencyUpdateLockLaunch> Launches { get; } = [];

        public Task<DependencyUpdateLockReadiness> CheckAsync(int projectId, string baseBranch, CancellationToken ct) => Task.FromResult(Readiness);

        public Task<string?> RecheckAsync(int pipelineId, string owner, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<DependencyUpdateLaunch> LaunchAsync(DependencyUpdateLockLaunch launch, CancellationToken ct)
        {
            Launches.Add(launch);
            return Task.FromResult(Launch);
        }

        public Task<CveRemediationLocks> ReadLocksAsync(int runId, IReadOnlyList<string> paths, CancellationToken ct) =>
            throw new NotSupportedException("The evaluation never reads a run's lock files.");
    }

    /// <summary>The registry of one ecosystem, scripted: the answer to any package and version, and what was asked.</summary>
    private sealed class FakeRegistryLicenses(string ecosystem) : IRegistryLicenseReader
    {
        public string Ecosystem => ecosystem;

        public RegistryLicenseAnswer Answer { get; set; } = RegistryLicenseAnswer.Found("MIT");

        public List<(string Package, string Version)> Asked { get; } = [];

        public Task<RegistryLicenseAnswer> ReadAsync(string packageId, string version, CancellationToken ct)
        {
            Asked.Add((packageId, version));
            return Task.FromResult(Answer);
        }
    }

    /// <summary>The NuGet and npm paths never ask a registry for a digest; one that does fails the test.</summary>
    private sealed class NoRegistry : IImageDigestResolver
    {
        public Task<ImageDigestAnswer> ResolveAsync(string image, string tag, CancellationToken ct) =>
            throw new NotSupportedException("Only a digest-pinned base image needs a registry.");
    }

    private sealed class FakePipelines : IDependencyUpdatePipelines
    {
        public int? Refuse { get; set; }

        public List<(int PipelineId, string Branch, string Commit, int UpdateId)> Launches { get; } = [];

        public Task<DependencyUpdateLaunch> LaunchAsync(int pipelineId, string branch, string commit, int updateId, CancellationToken ct)
        {
            Launches.Add((pipelineId, branch, commit, updateId));
            return Task.FromResult(pipelineId == Refuse
                ? new DependencyUpdateLaunch(null, "the pipeline is disabled")
                : new DependencyUpdateLaunch(pipelineId * 100, null));
        }
    }
}
