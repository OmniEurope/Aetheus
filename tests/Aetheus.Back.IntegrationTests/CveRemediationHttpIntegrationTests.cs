// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.Pipelines;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-004, lot 4, points 10 to 13, through the real HTTP pipeline, JWT and PostgreSQL included, on real bare Git repositories in the
/// host's temporary folder. The match comes from the real inventory read and the real matching pass, over files really committed.
/// Two doubles, named here: the network (the "package-feeds" client answers the Packagist reply recorded for monolog/monolog), and
/// the agent, which does not exist in this host: where a run would refresh a lock file, the test publishes the artifact through the real
/// artifact service and raises the real completion event. No package manager runs. The advisories are synthetic ranges over real versions.
/// The tests of a failure add <see cref="ServiceFaultProxy"/>: the real Git services, which throw or cancel the client at one exact call.
/// </summary>
public sealed class CveRemediationHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Package = "monolog/monolog";
    private const string Target = "1.27.1";
    private const string Branch = "cve/CVE-2099-0042-monolog-monolog";
    private const string FixedInMajor = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"1.27.1"}]}]""";
    private const string Manifest = "{\n    \"require\": {\n        \"monolog/monolog\": \"1.27.0\"\n    }\n}\n";
    private const string OldLock = """{"packages":[{"name":"monolog/monolog","version":"1.27.0"}],"packages-dev":[]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(int OrganizationId, int ProjectId, int RepositoryId, string Slug, string Runner, long MatchId);

    private static string Url(long matchId) => $"/api/analysis/cve-watch/{matchId}/remediation";

    private static string RecordedPackagist() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recorded", "packagist-p2-monolog-monolog.json"));

    private static WebApplicationFactory<Program> Host(AetheusWebApplicationFactory factory, Faults? faults = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("package-feeds").ConfigurePrimaryHttpMessageHandler(() => new RecordedRegistry(RecordedPackagist()));
            if (faults is null) return;
            ServiceFaultProxy.Intercept<IGitSmartHttpService>(services, nameof(IGitSmartHttpService.MarkPushedAsync), () => faults.OnPushAnnounced?.Invoke());
            ServiceFaultProxy.Intercept<IGitLightService>(services, nameof(IGitLightService.CreatePullRequestAsync), () => faults.OnPullRequest?.Invoke());
        }));

    /// <summary>What the Git services do first at the two points these tests break: they stay real, and nothing happens while these are null.</summary>
    private sealed class Faults
    {
        public Action? OnPushAnnounced { get; set; }

        public Action? OnPullRequest { get; set; }
    }

    [Fact]
    public async Task EveryEntryPoint_ChecksTheCallerFirst_AndARefusedCallerLeavesNoTraceInTheRepository()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);
        var otherOrganization = await NewOrganizationAsync(host);
        var reader = await IntegrationUsers.CreateAsync(host.Services, "fix-reader",
            [new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Read)]);
        var stranger = await IntegrationUsers.CreateAsync(host.Services, "fix-none", []);
        // A grant that names the project from another organization opens nothing: the role reaches its own organization only.
        var outsider = await IntegrationUsers.CreateAsync(host.Services, "fix-outsider",
            [new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Admin)], otherOrganization);
        var writer = await IntegrationUsers.CreateAsync(host.Services, "fix-writer",
            [new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Write)]);
        using var anonymous = host.CreateClient();
        using var readerClient = await IntegrationUsers.LoginClientAsync(host, reader.Username);
        using var strangerClient = await IntegrationUsers.LoginClientAsync(host, stranger.Username);
        using var outsiderClient = await IntegrationUsers.LoginClientAsync(host, outsider.Username);
        using var writerClient = await IntegrationUsers.LoginClientAsync(host, writer.Username);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Url(seed.MatchId), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(seed.MatchId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.PostAsync(Url(seed.MatchId), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.GetAsync(Url(seed.MatchId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.PostAsync(Url(seed.MatchId), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.GetAsync(Url(seed.MatchId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsync(Url(seed.MatchId), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(Url(seed.MatchId + 100000), null, Ct)).StatusCode);
        Assert.False(await BranchExistsAsync(host, seed));
        Assert.Equal(0, await CountRemediationsAsync(host, seed));

        // The positive controls: the reader reads, the writer of that very project writes.
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync(Url(seed.MatchId), Ct)).StatusCode);
        var created = await writerClient.PostAsync(Url(seed.MatchId), null, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(writer.Username, (await ReadAsync(created)).RequestedBy);
    }

    [Fact]
    public async Task ABumpWithoutLockFile_PushesTheBranch_LeavesTheBaseAlone_AndOpensThePullRequestInTheRequestersName()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);
        var writer = await IntegrationUsers.CreateAsync(host.Services, "fix-owner",
            [new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Write)]);
        using var client = await IntegrationUsers.LoginClientAsync(host, writer.Username);
        var baseTip = await TipAsync(host, seed, "main");

        var response = await client.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var remediation = await ReadAsync(response);
        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, remediation.Status);
        Assert.Equal(Branch, remediation.Branch);
        Assert.Equal("main", remediation.BaseBranch);
        Assert.Equal("1.27.0", remediation.FromVersion);
        Assert.Equal(Target, remediation.TargetVersion);

        // Read back from the bare repository: the branch holds the change, the base does not.
        Assert.Equal(Manifest.Replace("1.27.0", Target, StringComparison.Ordinal), await ReadFileAsync(host, seed, Branch, "composer.json"));
        Assert.Equal(Manifest, await ReadFileAsync(host, seed, "main", "composer.json"));
        Assert.Equal(baseTip, await TipAsync(host, seed, "main"));
        var commit = await TipCommitAsync(host, seed, Branch);
        Assert.Equal(writer.Username, commit.AuthorName);
        Assert.Contains("CVE-2099-0042", commit.Message, StringComparison.Ordinal);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pullRequest = await db.PullRequests.AsNoTracking().SingleAsync(request => request.Id == remediation.PullRequestId, Ct);
        Assert.Equal(writer.Username, pullRequest.AuthorLogin);
        Assert.Equal((Branch, "main", PullRequestStatus.Open), (pullRequest.SourceBranch, pullRequest.TargetBranch, pullRequest.Status));
        Assert.Equal(pullRequest.ExternalId, remediation.PullRequestNumber);

        var audited = await db.AuditLogs.AsNoTracking().Where(line => line.EntityType == nameof(VulnerabilityRemediation)).Select(line => line.Action).ToListAsync(Ct);
        Assert.Contains("RemediationRequested", audited);
        Assert.Contains("RemediationBranchPushed", audited);
        Assert.Contains("RemediationPullRequestOpen", audited);
    }

    [Fact]
    public async Task AskingTwice_FindsTheFirstAttempt_OneBranchOnePullRequestOneCommit_EvenAtTheSameInstant()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);

        var answers = await Task.WhenAll(admin.PostAsync(Url(seed.MatchId), null, Ct), admin.PostAsync(Url(seed.MatchId), null, Ct));
        var again = await admin.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(1, answers.Count(answer => answer.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, answers.Count(answer => answer.StatusCode == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, (await ReadAsync(again)).Status);
        Assert.Equal(1, await CountRemediationsAsync(host, seed));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PullRequests.CountAsync(request => request.SourceBranch == Branch, Ct));
        var cli = scope.ServiceProvider.GetRequiredService<IGitLightCliService>();
        var commits = await cli.GetCommitsAsync(DiskPath(scope, seed), Branch, 0, 20, ct: Ct);
        Assert.Single(commits, commit => commit.Message.Contains("bump", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProtectedBranch_IsNeverWritten_AndTheAnswerIsTheCommandWithItsReason()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);
        await PipelineLaunchScenario.PostAsync<BranchProtectionRuleDto>(admin, $"/api/git/repos/{seed.RepositoryId}/branch-protection",
            new CreateBranchProtectionRuleRequest { Pattern = "cve/*", ProtectionLevel = BranchProtectionLevel.ProtectedExceptPipelines });

        var response = await admin.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var answer = await ReadAsync(response);
        Assert.Null(answer.Status);
        Assert.Equal(CveRemediationFallbackReason.BranchProtected, answer.Fallback!.Reason);
        Assert.Equal($"composer require {Package}:{Target} --no-scripts --no-plugins", answer.Fallback.Command);
        Assert.False(await BranchExistsAsync(host, seed));
        Assert.Equal(0, await CountRemediationsAsync(host, seed));
        await AssertRefusalAuditedAsync(host, seed, nameof(CveRemediationFallbackReason.BranchProtected));
    }

    [Fact]
    public async Task ALockFileWithNoPipelineToRefreshIt_AnswersTheCommand_AndLeavesNoHalfDoneBranch()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);

        var response = await admin.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fallback = (await ReadAsync(response)).Fallback!;
        Assert.Equal(CveRemediationFallbackReason.NoRefreshPipeline, fallback.Reason);
        Assert.NotNull(fallback.Command);
        Assert.False(await BranchExistsAsync(host, seed));
        Assert.Equal(0, await CountRemediationsAsync(host, seed));
    }

    [Fact]
    public async Task AWriterWhoMayNotRunOnTheServers_AnswersTheCommand_AndLaunchesNothing()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var writer = await IntegrationUsers.CreateAsync(host.Services, "fix-no-runner",
            [new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Write)], permissionKeys: [PermissionKeys.PipelineRun]);
        using var client = await IntegrationUsers.LoginClientAsync(host, writer.Username);

        var response = await client.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(CveRemediationFallbackReason.RunRightsMissing, (await ReadAsync(response)).Fallback!.Reason);
        Assert.False(await BranchExistsAsync(host, seed));
    }

    [Fact]
    public async Task AWriterWithoutTheRightToRunPipelines_AnswersTheCommand_EvenWithAdminOnTheServer()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        await using var scope = host.Services.CreateAsyncScope();
        var serverId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Servers.Where(server => server.Name == seed.Runner).Select(server => server.Id).SingleAsync(Ct);
        var writer = await IntegrationUsers.CreateAsync(host.Services, "fix-no-run-right",
        [
            new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Write),
            new IntegrationUsers.Grant(ResourceType.Server, serverId, Permission.Admin)
        ]);
        using var client = await IntegrationUsers.LoginClientAsync(host, writer.Username);

        var response = await client.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(CveRemediationFallbackReason.RunRightsMissing, (await ReadAsync(response)).Fallback!.Reason);
        Assert.False(await BranchExistsAsync(host, seed));
    }

    [Fact]
    public async Task ALockRefresh_RunsOnTheBranch_ThenTheBackCommitsTheLock_ReadsTheVersionObtained_AndTheMergeClosesTheMatch()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);

        var response = await admin.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var running = await ReadAsync(response);
        Assert.Equal(VulnerabilityRemediationStatus.Running, running.Status);
        Assert.Null(running.PullRequestId);
        Assert.Equal(OldLock, await ReadFileAsync(host, seed, Branch, "composer.lock"));
        var run = await ReadRunAsync(host, running.PipelineRunId!.Value);
        Assert.Equal(Branch, run.BranchName);
        Assert.Contains($"composer update {Package} --no-install --no-scripts --no-plugins --no-interaction", run.ParametersJson, StringComparison.Ordinal);

        // Composer resolved above the target: the agent's part, played by the test through the real artifact service and event.
        var refreshed = OldLock.Replace("1.27.0", "1.27.2", StringComparison.Ordinal);
        await PublishLocksAsync(host, running.PipelineRunId!.Value, ("composer.lock", refreshed));
        await CompleteRunAsync(host, running.PipelineRunId!.Value, PipelineStatus.Success);

        var open = await GetAsync(admin, seed);
        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, open.Status);
        Assert.Equal("1.27.2", open.ResolvedVersion);
        Assert.Equal(refreshed, await ReadFileAsync(host, seed, Branch, "composer.lock"));
        Assert.Equal(OldLock, await ReadFileAsync(host, seed, "main", "composer.lock"));

        var merged = await admin.PostAsync($"/api/git/repos/{seed.RepositoryId}/pull-requests/{open.PullRequestNumber}/merge", null, Ct);
        Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync(Ct));
        var done = await GetAsync(admin, seed);
        Assert.Equal(VulnerabilityRemediationStatus.Merged, done.Status);
        Assert.Equal("1.27.2", done.ResolvedVersion);

        // The match closes on what the lock file holds, by the inventory read the merge asks for: it stayed open until then.
        Assert.False(await IsMatchClosedAsync(host, seed));
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IProjectInventoryService>().RefreshProjectAsync(seed.ProjectId, Ct);
        Assert.True(await IsMatchClosedAsync(host, seed));
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    public async Task ARunThatDidNotSucceed_FailsTheAttempt_WhateverItPublished(PipelineStatus status)
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var running = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        await PublishLocksAsync(host, running.PipelineRunId!.Value, ("composer.lock", OldLock.Replace("1.27.0", "1.27.2", StringComparison.Ordinal)));

        await CompleteRunAsync(host, running.PipelineRunId!.Value, status);

        var failed = await GetAsync(admin, seed);
        Assert.Equal(VulnerabilityRemediationStatus.Failed, failed.Status);
        Assert.Contains(status.ToString(), failed.FailureReason!, StringComparison.Ordinal);
        Assert.Null(failed.PullRequestId);
    }

    [Fact]
    public async Task ARunWithoutTheLockFile_FailsTheAttempt_ItIsNeverASuccessThatChangedNothing()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var running = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        await PublishLocksAsync(host, running.PipelineRunId!.Value, ("other/composer.lock", OldLock));

        await CompleteRunAsync(host, running.PipelineRunId!.Value, PipelineStatus.Success);

        var failed = await GetAsync(admin, seed);
        Assert.Equal(VulnerabilityRemediationStatus.Failed, failed.Status);
        Assert.Contains("composer.lock", failed.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequesterWhoLostWriteAccessDuringTheRun_GetsNoCommit()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var running = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        await PublishLocksAsync(host, running.PipelineRunId!.Value, ("composer.lock", OldLock.Replace("1.27.0", "1.27.2", StringComparison.Ordinal)));
        var former = await IntegrationUsers.CreateAsync(host.Services, "fix-former", []);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.VulnerabilityRemediations.SingleAsync(remediation => remediation.ProjectId == seed.ProjectId, Ct);
            row.RequestedByUsername = former.Username;
            await db.SaveChangesAsync(Ct);
        }

        await CompleteRunAsync(host, running.PipelineRunId!.Value, PipelineStatus.Success);

        var failed = await GetAsync(admin, seed);
        Assert.Equal(VulnerabilityRemediationStatus.Failed, failed.Status);
        Assert.Contains("write access", failed.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(OldLock, await ReadFileAsync(host, seed, Branch, "composer.lock"));
    }

    [Fact]
    public async Task APullRequestClosedWithoutMerge_AbandonsTheAttempt_AndAFreshRequestReusesItsBranch()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);
        var first = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));

        var closed = await admin.PostAsync($"/api/git/repos/{seed.RepositoryId}/pull-requests/{first.PullRequestNumber}/close", null, Ct);
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync(Ct));

        Assert.Equal(VulnerabilityRemediationStatus.Abandoned, (await GetAsync(admin, seed)).Status);
        var again = await admin.PostAsync(Url(seed.MatchId), null, Ct);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var second = await ReadAsync(again);
        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, second.Status);
        Assert.Equal(first.Branch, second.Branch);
        Assert.NotEqual(first.PullRequestId, second.PullRequestId);
    }

    [Fact]
    public async Task ARetryAfterAFailedRun_LaunchesItsOwnRun_AndThatRunOpensThePullRequest()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var first = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        await CompleteRunAsync(host, first.PipelineRunId!.Value, PipelineStatus.Failed);
        Assert.Equal(VulnerabilityRemediationStatus.Failed, (await GetAsync(admin, seed)).Status);

        var retried = await admin.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var second = await ReadAsync(retried);
        Assert.Equal(VulnerabilityRemediationStatus.Running, second.Status);
        Assert.NotEqual(first.PipelineRunId, second.PipelineRunId);
        var run = await ReadRunAsync(host, second.PipelineRunId!.Value);
        Assert.Equal($"cve-remediation-{await LatestRemediationIdAsync(host, seed)}", run.IdempotencyKey);

        await PublishLocksAsync(host, second.PipelineRunId!.Value, ("composer.lock", OldLock.Replace("1.27.0", "1.27.2", StringComparison.Ordinal)));
        await CompleteRunAsync(host, second.PipelineRunId!.Value, PipelineStatus.Success);
        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, (await GetAsync(admin, seed)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGitFailureThePlatformDoesNotName_FailsTheAttemptWithoutItsInnerText_AndARetryIsPossible(bool atThePush)
    {
        var faults = new Faults();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory, faults);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);
        Action fail = () => throw new IOException(@"C:\secret\disk\path");
        if (atThePush) faults.OnPushAnnounced = fail;
        else faults.OnPullRequest = fail;

        var response = await admin.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var failed = await ReadAsync(response);
        Assert.Equal(VulnerabilityRemediationStatus.Failed, failed.Status);
        Assert.Contains(nameof(IOException), failed.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failed.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(CveRemediationFallbackReason.PreparationFailed, failed.Fallback!.Reason);
        Assert.Equal(1, await CountAuditAsync(host, seed, "RemediationFailed"));

        faults.OnPushAnnounced = null;
        faults.OnPullRequest = null;
        var retried = await admin.PostAsync(Url(seed.MatchId), null, Ct);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, (await ReadAsync(retried)).Status);
    }

    [Fact]
    public async Task AFailureAfterTheRun_FailsTheAttempt_TheDispatcherThatOnlyLogsLeavesNothingStuck()
    {
        var faults = new Faults();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory, faults);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var running = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        await PublishLocksAsync(host, running.PipelineRunId!.Value, ("composer.lock", OldLock.Replace("1.27.0", "1.27.2", StringComparison.Ordinal)));
        faults.OnPullRequest = () => throw new IOException("the disk is gone");

        await CompleteRunAsync(host, running.PipelineRunId!.Value, PipelineStatus.Success);

        var failed = await GetAsync(admin, seed);
        Assert.Equal(VulnerabilityRemediationStatus.Failed, failed.Status);
        Assert.Contains(nameof(IOException), failed.FailureReason!, StringComparison.Ordinal);
        Assert.Equal("1.27.2", failed.ResolvedVersion);
        faults.OnPullRequest = null;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(Url(seed.MatchId), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task AClientThatLeavesDuringTheRequest_DoesNotStopTheAttempt_ItEndsWithItsPullRequest()
    {
        var faults = new Faults();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory, faults);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        faults.OnPushAnnounced = leaving.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admin.PostAsync(Url(seed.MatchId), null, leaving.Token));

        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, await WaitForEndOfWritesAsync(host, seed));
    }

    [Fact]
    public async Task AClientThatLeavesDuringTheReadThatEndsARun_DoesNotStopTheCompletion()
    {
        var faults = new Faults();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory, faults);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var running = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        await PublishLocksAsync(host, running.PipelineRunId!.Value, ("composer.lock", OldLock.Replace("1.27.0", "1.27.2", StringComparison.Ordinal)));
        await MarkRunEndedAsync(host, running.PipelineRunId!.Value, PipelineStatus.Success);
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        faults.OnPushAnnounced = leaving.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admin.GetAsync(Url(seed.MatchId), leaving.Token));

        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, await WaitForEndOfWritesAsync(host, seed));
    }

    [Fact]
    public async Task TheEventAndReadsAtTheSameInstant_PushTheLockOnce_AndOpenOnePullRequest()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: true);
        await SaveRefreshPipelineAsync(admin, seed);
        var running = await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        var runId = running.PipelineRunId!.Value;
        await PublishLocksAsync(host, runId, ("composer.lock", OldLock.Replace("1.27.0", "1.27.2", StringComparison.Ordinal)));
        await MarkRunEndedAsync(host, runId, PipelineStatus.Success);

        await Task.WhenAll(
            CompleteRunAsync(host, runId, PipelineStatus.Success), GetAsync(admin, seed), GetAsync(admin, seed), CompleteRunAsync(host, runId, PipelineStatus.Success));
        await CompleteRunAsync(host, runId, PipelineStatus.Success);

        Assert.Equal(VulnerabilityRemediationStatus.PullRequestOpen, (await GetAsync(admin, seed)).Status);
        Assert.Equal(1, await CountAuditAsync(host, seed, "RemediationRunEnded"));
        Assert.Equal(1, await CountAuditAsync(host, seed, "RemediationPullRequestOpen"));
        // One line for the manifest, one for the lock.
        Assert.Equal(2, await CountAuditAsync(host, seed, "RemediationBranchPushed"));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.GitInternalRepos.Where(row => row.Id == seed.RepositoryId).Select(row => row.GitConnectionId).SingleAsync(Ct);
        Assert.Equal(1, await db.PullRequests.CountAsync(request => request.GitConnectionId == connection && request.SourceBranch == Branch, Ct));
    }

    [Theory]
    [InlineData(VulnerabilityRemediationStatus.Requested)]
    [InlineData(VulnerabilityRemediationStatus.BranchPushed)]
    public async Task AnAttemptLeftSilentByARestart_FailsAtTheNextRead_AndNoLongerBlocksTheNext(VulnerabilityRemediationStatus status)
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false);
        await ReadAsync(await admin.PostAsync(Url(seed.MatchId), null, Ct));
        await RewindRemediationAsync(host, seed, status, TimeSpan.FromMinutes(5));
        Assert.Equal(status, (await GetAsync(admin, seed)).Status);

        await RewindRemediationAsync(host, seed, status, TimeSpan.FromHours(2));

        var failed = await GetAsync(admin, seed);
        Assert.Equal(VulnerabilityRemediationStatus.Failed, failed.Status);
        Assert.Contains("restarted", failed.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(Url(seed.MatchId), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task AManifestThatStartsWithAByteOrderMark_KeepsItOnTheBranch()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(host);
        var seed = await SeedAsync(host, admin, withLock: false, manifest: "\uFEFF" + Manifest);

        var response = await admin.PostAsync(Url(seed.MatchId), null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = host.Services.CreateAsyncScope();
        var repository = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GitInternalRepos.AsNoTracking().SingleAsync(row => row.Id == seed.RepositoryId, Ct);
        var reader = scope.ServiceProvider.GetRequiredService<IProjectSourceReader>();
        await using var file = await reader.OpenFileAsync(repository.Id, await TipAsync(host, seed, Branch), "composer.json", Ct);
        using var bytes = new MemoryStream();
        await file!.CopyToAsync(bytes, Ct);
        var expected = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Manifest.Replace("1.27.0", Target, StringComparison.Ordinal)));
        Assert.Equal(expected, bytes.ToArray());
    }

    private static async Task<CveRemediationDto> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<CveRemediationDto>(IntegrationJsonOptions.Default, Ct)
        ?? throw new InvalidOperationException("The remediation came back empty.");

    private static async Task<CveRemediationDto> GetAsync(HttpClient client, Seed seed)
    {
        var response = await client.GetAsync(Url(seed.MatchId), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await ReadAsync(response);
    }

    /// <summary>A project on the seeded organization, a real repository, an advisory, then the real read and the real pass that make the match.</summary>
    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> host, HttpClient admin, bool withLock, string manifest = Manifest)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var runner = $"fix-runner-{suffix}";
        int organizationId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            organizationId = await db.Organizations.Where(row => row.Slug == IntegrationUsers.DefaultOrganizationSlug).Select(row => row.Id).FirstAsync(Ct);
            db.Servers.Add(new Server
            {
                Name = runner,
                Hostname = runner,
                Status = ServerStatus.Online,
                PipelineRunnerEnabled = true,
                LastHeartbeat = DateTime.UtcNow,
                OrganizationId = organizationId,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
            });
            db.VulnerabilityAdvisories.Add(new VulnerabilityAdvisory
            {
                Source = "GHSA",
                ExternalId = $"GHSA-{suffix}",
                PrimaryCveId = "CVE-2099-0042",
                Severity = AnalysisSeverity.High,
                SeverityPublished = true,
                ModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                AffectedPackages = [new VulnerabilityAffectedPackage { Ecosystem = "Packagist", PackageName = Package, RangesJson = FixedInMajor, FixedVersion = Target }]
            });
            await db.SaveChangesAsync(Ct);
        }

        var project = await PipelineLaunchScenario.PostAsync<ProjectDto>(admin, "/api/projects", new CreateProjectRequest
        {
            Name = $"fix-{suffix}",
            Description = "a project with a vulnerable package",
            OrganizationId = organizationId
        });
        var repository = await PipelineLaunchScenario.PostAsync<GitLightRepoDto>(admin, "/api/git/repos", new CreateGitLightRepoRequest
        {
            ProjectId = project.Id,
            Name = $"fix-repo-{suffix}",
            DefaultBranch = "main"
        });
        var seed = new Seed(organizationId, project.Id, repository.Id, repository.Slug, runner, 0);
        List<(string, string)> files = [("composer.json", manifest)];
        if (withLock) files.Add(("composer.lock", OldLock));
        await CommitAsync(host, seed, "main", [.. files]);
        await using var read = host.Services.CreateAsyncScope();
        await read.ServiceProvider.GetRequiredService<IProjectInventoryService>().RefreshProjectAsync(project.Id, Ct);
        var db2 = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var matchId = await db2.VulnerabilityMatches.Where(match => match.ProjectId == project.Id && match.Status == VulnerabilityMatchStatus.Affected)
            .Select(match => match.Id).SingleAsync(Ct);
        return seed with { MatchId = matchId };
    }

    private static async Task<int> NewOrganizationAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"other-{Guid.NewGuid():N}", Slug = $"o{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        return organization.Id;
    }

    private static string DiskPath(AsyncServiceScope scope, Seed seed) =>
        scope.ServiceProvider.GetRequiredService<IGitLightService>().ResolveDiskPath(seed.ProjectId, seed.Slug);

    private static async Task CommitAsync(WebApplicationFactory<Program> host, Seed seed, string branch, params (string Path, string Content)[] files)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var cli = scope.ServiceProvider.GetRequiredService<IGitLightCliService>();
        var (success, _, error) = await cli.CommitFilesAsync(DiskPath(scope, seed), branch, files, "chore: seed", "seed", "seed@example.com", Ct);
        Assert.True(success, error);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = await db.GitInternalRepos.SingleAsync(row => row.Id == seed.RepositoryId, Ct);
        repository.IsEmpty = false;
        await db.SaveChangesAsync(Ct);
    }

    private static Task SaveRefreshPipelineAsync(HttpClient admin, Seed seed) =>
        PipelineLaunchScenario.PostAsync<PipelineDto>(admin, "/api/pipelines", new CreatePipelineRequest
        {
            Name = "cve-lock-refresh",
            Description = "runs the package manager the plan names and hands the lock files back",
            ProjectId = seed.ProjectId,
            SourceBranch = "main",
            YamlDefinition = $$"""
                name: cve-lock-refresh
                trigger: manual
                parameters:
                  - name: LOCK_COMMAND
                    type: string
                    required: true
                stages:
                  - name: Refresh
                    agent: {{seed.Runner}}
                    steps:
                      - name: Run the package manager
                        shell: $(LOCK_COMMAND)
                  # Steps of one stage run in parallel (ADR-012): the artifact is collected in its own stage, after
                  # the package manager has written the lock file.
                  - name: Hand back
                    agent: {{seed.Runner}}
                    depends_on: [Refresh]
                    steps:
                      - name: Hand the lock files back
                        type: artifacts
                        artifact_name: dependency-locks
                        target_files:
                          - "composer.lock"
                """
        });

    private static async Task<string?> ReadFileAsync(WebApplicationFactory<Program> host, Seed seed, string branch, string path)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var cli = scope.ServiceProvider.GetRequiredService<IGitLightCliService>();
        return (await cli.GetBlobAsync(DiskPath(scope, seed), branch, path, Ct))?.Content;
    }

    private static async Task<GitLightCommitDto> TipCommitAsync(WebApplicationFactory<Program> host, Seed seed, string branch)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var cli = scope.ServiceProvider.GetRequiredService<IGitLightCliService>();
        return (await cli.GetCommitsAsync(DiskPath(scope, seed), branch, 0, 1, ct: Ct))[0];
    }

    private static async Task<string> TipAsync(WebApplicationFactory<Program> host, Seed seed, string branch) =>
        (await TipCommitAsync(host, seed, branch)).Sha;

    private static async Task<bool> BranchExistsAsync(WebApplicationFactory<Program> host, Seed seed)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var cli = scope.ServiceProvider.GetRequiredService<IGitLightCliService>();
        return (await cli.GetBranchesAsync(DiskPath(scope, seed), "main", Ct)).Exists(branch => branch.Name == Branch);
    }

    private static async Task<int> CountRemediationsAsync(WebApplicationFactory<Program> host, Seed seed)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().VulnerabilityRemediations.CountAsync(row => row.ProjectId == seed.ProjectId, Ct);
    }

    private static async Task<bool> IsMatchClosedAsync(WebApplicationFactory<Program> host, Seed seed)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.VulnerabilityMatches.AsNoTracking().Where(match => match.Id == seed.MatchId).Select(match => match.ClosedAt != null).SingleAsync(Ct);
    }

    private static async Task<PipelineRun> ReadRunAsync(WebApplicationFactory<Program> host, int runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PipelineRuns.AsNoTracking().SingleAsync(run => run.Id == runId, Ct);
    }

    private static async Task<int> LatestRemediationIdAsync(WebApplicationFactory<Program> host, Seed seed)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().VulnerabilityRemediations.AsNoTracking()
            .Where(row => row.ProjectId == seed.ProjectId).MaxAsync(row => row.Id, Ct);
    }

    private static async Task<int> CountAuditAsync(WebApplicationFactory<Program> host, Seed seed, string action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await db.VulnerabilityRemediations.AsNoTracking().Where(row => row.ProjectId == seed.ProjectId).Select(row => (int?)row.Id).ToListAsync(Ct);
        return await db.AuditLogs.AsNoTracking().CountAsync(
            line => line.EntityType == nameof(VulnerabilityRemediation) && line.Action == action && attempts.Contains(line.EntityId), Ct);
    }

    /// <summary>The end of a run with no event: what a lost event, or a run ended before it was saved on the row, leaves in the database.</summary>
    private static async Task MarkRunEndedAsync(WebApplicationFactory<Program> host, int runId, PipelineStatus status)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().PipelineRuns.Where(run => run.Id == runId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, status), Ct);
    }

    /// <summary>The row a back that restarted mid-write leaves: active, and last written <paramref name="age"/> ago.</summary>
    private static async Task RewindRemediationAsync(WebApplicationFactory<Program> host, Seed seed, VulnerabilityRemediationStatus status, TimeSpan age)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var updatedAt = DateTime.UtcNow - age;
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().VulnerabilityRemediations.Where(row => row.ProjectId == seed.ProjectId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, status).SetProperty(row => row.UpdatedAt, updatedAt), Ct);
    }

    /// <summary>The client left, so nothing answers when the back is done: the row says it, and a row still writing after the wait is the failure.</summary>
    private static async Task<VulnerabilityRemediationStatus> WaitForEndOfWritesAsync(WebApplicationFactory<Program> host, Seed seed)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (true)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var status = await scope.ServiceProvider.GetRequiredService<AppDbContext>().VulnerabilityRemediations.AsNoTracking()
                .Where(row => row.ProjectId == seed.ProjectId).OrderByDescending(row => row.Id).Select(row => row.Status).FirstAsync(Ct);
            if (status is VulnerabilityRemediationStatus.PullRequestOpen or VulnerabilityRemediationStatus.Failed
                || TimeProvider.System.GetElapsedTime(deadline) > TimeSpan.FromSeconds(20))
                return status;
            await Task.Delay(100, Ct);
        }
    }

    private static async Task AssertRefusalAuditedAsync(WebApplicationFactory<Program> host, Seed seed, string reason)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lines = await db.AuditLogs.AsNoTracking().Where(line => line.Action == "RemediationRefused").Select(line => line.Details).ToListAsync(Ct);
        Assert.Contains(lines, line => line!.Contains($"match {seed.MatchId}: {reason}", StringComparison.Ordinal));
    }

    /// <summary>What the agent would upload at the end of the stage: a zip of the lock files, through the real artifact service.</summary>
    private static async Task PublishLocksAsync(WebApplicationFactory<Program> host, int runId, params (string Path, string Content)[] locks)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in locks)
            {
                await using var entry = await archive.CreateEntry(path).OpenAsync(Ct);
                await entry.WriteAsync(Encoding.UTF8.GetBytes(content), Ct);
            }
        }

        zip.Position = 0;
        await using var scope = host.Services.CreateAsyncScope();
        var artifact = await scope.ServiceProvider.GetRequiredService<IArtifactService>()
            .PublishArtifactAsync(runId, "dependency-locks", "Hand back", zip.Length, zip, Ct);
        Assert.NotNull(artifact);
    }

    private static async Task CompleteRunAsync(WebApplicationFactory<Program> host, int runId, PipelineStatus status)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>().DispatchAsync(new PipelineRunCompletedEvent(runId, status), Ct);
    }

    /// <summary>The network, replaced: one recorded answer for every request.</summary>
    private sealed class RecordedRegistry(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
