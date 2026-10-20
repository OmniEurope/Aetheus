// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineWebhookServiceTests
{
    private readonly IPipelineRepository _pipelineRepoMock = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunService _runServiceMock = Substitute.For<IPipelineRunService>();
    private readonly ISettingsService _settingsMock = Substitute.For<ISettingsService>();
    private readonly IPipelineGitService _pipelineGitMock = Substitute.For<IPipelineGitService>();
    private readonly ILogger<PipelineWebhookService> _loggerMock = Substitute.For<ILogger<PipelineWebhookService>>();
    private readonly PipelineWebhookService _sut;

    public PipelineWebhookServiceTests()
    {
        _sut = new PipelineWebhookService(
            _pipelineRepoMock, _runServiceMock, _settingsMock, _loggerMock, _pipelineGitMock);
    }

    // BTEST-007: the negative cases below run with a configured secret and a valid signature. With no
    // secret the service refuses before parsing the payload or reading the catalogue, so a negative
    // assertion there would hold whatever the branch, repository or latest-wins logic does.
    private const string NegativeCaseSecret = "negative-case-secret";

    private static string SignGitHub(string body) => "sha256=" + Convert.ToHexStringLower(
        System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(NegativeCaseSecret),
            System.Text.Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task HandleWebhookAsync_InvalidSignature_ReturnsFalse()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns("my-secret");

        var result = await _sut.HandleWebhookAsync("{}", "wrong-sig", ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task HandleWebhookAsync_NoSecret_IsRefusedBeforeTheCatalogueIsRead()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var result = await _sut.HandleWebhookAsync(body, null, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _pipelineRepoMock.DidNotReceive().GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_GitHubFormat_TriggersPipeline()
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/org/repo" };
        var pipeline = new Pipeline { Id = 10, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = 1, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));
        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_ConcurrentDeliveriesForSamePipeline_AreSerialized()
    {
        const string secret = "concurrent-webhook-secret";
        var project = new Project
        {
            Id = 1,
            Name = "P",
            RepositoryUrl = "https://github.com/org/repo"
        };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project
        };
        var firstEnteredCriticalSection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var webhookLookupCount = 0;
        var criticalEntryCount = 0;
        var activeCalls = 0;
        var maximumConcurrentCalls = 0;

        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);
        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref webhookLookupCount) == 2)
                    secondReachedLock.TrySetResult();
                return new List<Pipeline> { pipeline };
            });
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var concurrent = Interlocked.Increment(ref activeCalls);
                if (concurrent > Volatile.Read(ref maximumConcurrentCalls))
                    Interlocked.Exchange(ref maximumConcurrentCalls, concurrent);
                var entry = Interlocked.Increment(ref criticalEntryCount);
                if (entry == 1)
                    firstEnteredCriticalSection.TrySetResult();
                if (entry == 1)
                    await releaseFirst.Task;
                Interlocked.Decrement(ref activeCalls);
                return false;
            });
        _runServiceMock.TriggerAutomatedRunAsync(
                10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunDto { Id = 42, PipelineId = 10 });
        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));

        var first = _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);
        await firstEnteredCriticalSection.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);
        await secondReachedLock.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref activeCalls));
        Assert.Equal(1, maximumConcurrentCalls);

        releaseFirst.TrySetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result));
        Assert.Equal(1, maximumConcurrentCalls);
    }

    [Fact]
    public async Task HandleWebhookAsync_GitLabFormat_TriggersPipeline()
    {
        const string secret = "my-gitlab-token";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://gitlab.com/org/repo" };
        var pipeline = new Pipeline { Id = 20, Name = "Deploy", TriggerType = PipelineTriggerType.Webhook, ProjectId = 1, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(20, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"project\":{\"web_url\":\"https://gitlab.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var result = await _sut.HandleWebhookAsync(body, secret, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        // The secret-configured "no pipelines" case also returns true (see WithWebhookSecretSet_ReturnsTrue),
        // so Assert.True(result) alone doesn't prove a run actually started - assert the real dispatch.
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(20, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_ActiveRun_SkipsPipeline()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(NegativeCaseSecret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/org/repo" };
        var pipeline = new Pipeline { Id = 10, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = 1, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(true);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        await _sut.HandleWebhookAsync(body, SignGitHub(body), ct: TestContext.Current.CancellationToken);

        // The active-run gate was reached, and without supersede_running nothing was launched or prepared.
        await _pipelineRepoMock.Received(1).HasActiveRunAsync(10, Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().TriggerAutomatedRunAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().PrepareAutomatedRunAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_LatestWins_CancelsActiveRunsAndLaunchesNewRevision()
    {
        const string secret = "latest-wins-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);
        var project = new Project
        {
            Id = 1,
            Name = "P",
            RepositoryUrl = "https://github.com/org/repo"
        };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            YamlDefinition = """
                name: Build
                trigger: webhook
                supersede_running: true
                stages:
                  - name: Build
                    steps:
                      - name: compile
                        shell: dotnet build
                """
        };
        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>()).Returns(true);
        _pipelineRepoMock.GetActiveRunIdsAsync(10, Arg.Any<CancellationToken>()).Returns([40, 41]);
        _runServiceMock.CancelRunAsync(Arg.Any<int>(), Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>()).Returns(true);
        var preparation = new PipelineRunPreparation
        {
            PipelineId = 10,
            YamlSnapshot = pipeline.YamlDefinition,
            TargetServerIds = []
        };
        _runServiceMock.PrepareAutomatedRunAsync(
                10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(preparation);
        _runServiceMock.TriggerPreparedAutomatedRunAsync(
                preparation, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunDto { Id = 42, PipelineId = 10 });
        var body =
            "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/develop\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));

        var result = await _sut.HandleWebhookAsync(
            body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _runServiceMock.Received(1).CancelRunAsync(40, Arg.Is<PipelineRunCancellation>(c => c.By == PipelineRunCancellation.SystemAuthor), Arg.Any<CancellationToken>());
        await _runServiceMock.Received(1).CancelRunAsync(41, Arg.Is<PipelineRunCancellation>(c => c.By == PipelineRunCancellation.SystemAuthor), Arg.Any<CancellationToken>());
        await _runServiceMock.Received(1).TriggerPreparedAutomatedRunAsync(
            preparation, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_ARefusalRolledBackWithItsTransaction_IsRecordedAgainOutsideIt()
    {
        // The launcher records a refused automated launch as a failed run, but the webhook runs in
        // a transaction the refusal rolls back, and that record with it.
        const string secret = "webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>()).Returns(secret);
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "deploy",
            YamlDefinition = "name: deploy\ntrigger: webhook\nstages: []",
            Project = new Project { RepositoryUrl = "https://github.com/org/repo" }
        };
        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>()).Returns([pipeline]);
        _pipelineRepoMock.LockPipelineForWebhookAsync(10, Arg.Any<CancellationToken>()).Returns(true);
        _runServiceMock.TriggerAutomatedRunAsync(
                10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns<PipelineRunDto?>(_ => throw new BadRequestException("Source repository 'public' differs."));
        var transaction = Substitute.For<IDbTransactionScope>();
        transaction.IsRelational.Returns(true);
        var sut = new PipelineWebhookService(
            _pipelineRepoMock, _runServiceMock, _settingsMock, _loggerMock, _pipelineGitMock, transaction);
        var body =
            "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/develop\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));

        await Assert.ThrowsAsync<BadRequestException>(() =>
            sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken));

        Received.InOrder(() =>
        {
            transaction.RollbackAsync(Arg.Any<CancellationToken>());
            _runServiceMock.RecordRefusedAutomatedLaunchAsync(
                10, "Webhook", "Source repository 'public' differs.",
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        });
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_LatestWinsAuthorizationFailure_DoesNotCancelCurrentRun()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(NegativeCaseSecret);
        var project = new Project
        {
            Id = 1,
            Name = "P",
            RepositoryUrl = "https://github.com/org/repo"
        };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            YamlDefinition = "name: Build\ntrigger: webhook\nsupersede_running: true\nstages: []"
        };
        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>()).Returns(true);
        _runServiceMock.PrepareAutomatedRunAsync(
                10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns((PipelineRunPreparation?)null);

        var body =
            "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        await _sut.HandleWebhookAsync(
            body, SignGitHub(body), ct: TestContext.Current.CancellationToken);

        // The refused preparation was really attempted, and nothing was cancelled or launched after it.
        await _runServiceMock.Received(1).PrepareAutomatedRunAsync(
            10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().CancelRunAsync(
            Arg.Any<int>(), Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().TriggerPreparedAutomatedRunAsync(
            Arg.Any<PipelineRunPreparation>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_UnparsablePayload_ReturnsFalse()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(NegativeCaseSecret);

        const string body = "not json at all";
        var result = await _sut.HandleWebhookAsync(body, SignGitHub(body), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _pipelineRepoMock.DidNotReceive().GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_NoMatchingProject_LaunchesNothing()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(NegativeCaseSecret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/other/repo" };
        var pipeline = new Pipeline { Id = 10, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = 1, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/different-repo\"},\"ref\":\"refs/heads/main\"}";
        await _sut.HandleWebhookAsync(body, SignGitHub(body), ct: TestContext.Current.CancellationToken);

        // The catalogue was read, the repository comparison stopped the pipeline before its YAML was read.
        await _pipelineRepoMock.Received(1).GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>());
        await _pipelineGitMock.DidNotReceive().ReadProjectPipelineYamlAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>());
        await _runServiceMock.DidNotReceive().TriggerAutomatedRunAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_NullProjectUrl_SkipsPipeline()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(NegativeCaseSecret);

        var pipeline = new Pipeline { Id = 10, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = null, Project = null };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        await _sut.HandleWebhookAsync(body, SignGitHub(body), ct: TestContext.Current.CancellationToken);

        await _pipelineRepoMock.Received(1).GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>());
        await _pipelineRepoMock.DidNotReceive().HasActiveRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().TriggerAutomatedRunAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_GitLabToken_ValidatesCorrectly()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns("my-gitlab-token");

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://gitlab.com/org/repo" };
        var pipeline = new Pipeline { Id = 10, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = 1, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"project\":{\"web_url\":\"https://gitlab.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var result = await _sut.HandleWebhookAsync(body, "my-gitlab-token", ct: TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task HandleWebhookAsync_UrlWithGitSuffix_MatchesNormalized()
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/org/repo.git" };
        var pipeline = new Pipeline { Id = 10, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = 1, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));
        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        // A signature-valid webhook with no triggered pipelines also returns true (see
        // WithWebhookSecretSet_ReturnsTrue), so Assert.True(result) alone doesn't prove a run actually
        // started - assert the real dispatch to prove the normalized URL matched the pipeline's project.
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_WithWebhookSecretSet_ReturnsTrue_WhenNoTriggeredPipelines()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns("my-secret");

        // Need proper HMAC signature
        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("my-secret"));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body));
        var signature = "sha256=" + Convert.ToHexStringLower(hash);

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        // Returns true because secret is configured (even though no pipelines triggered)
        Assert.True(result);
    }

    [Fact]
    public async Task HandleWebhookAsync_ExternalRepo_MatchesViaGitConnection()
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        // External-Git project: RepositoryUrl is the internal mirror URL (never equals the external
        // webhook URL); the GitConnection reconstructs the external remote that the webhook reports.
        var project = new Project
        {
            Id = 1,
            Name = "P",
            Description = "d",
            RepositoryUrl = "https://aetheus.local/git/1/repo.git",
            GitConnectionId = 99,
            GitConnection = new GitConnection
            {
                Id = 99,
                ProviderType = GitProviderType.GitHub,
                OwnerOrGroup = "org",
                RepositoryName = "repo"
            }
        };
        var pipeline = new Pipeline { Id = 10, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = 1, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));
        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_ExternalRepo_SelfHosted_MatchesViaBaseUrl()
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project
        {
            Id = 2,
            Name = "P2",
            Description = "d",
            RepositoryUrl = "https://aetheus.local/git/2/repo.git",
            GitConnectionId = 77,
            GitConnection = new GitConnection
            {
                Id = 77,
                ProviderType = GitProviderType.Gitea,
                BaseUrl = "https://gitea.example.com",
                OwnerOrGroup = "team",
                RepositoryName = "service"
            }
        };
        var pipeline = new Pipeline { Id = 20, Name = "Build", TriggerType = PipelineTriggerType.Webhook, ProjectId = 2, Project = project };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(20, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"repository\":{\"html_url\":\"https://gitea.example.com/team/service\"},\"ref\":\"refs/heads/main\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));
        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(20, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_BranchFilter_SkipsNonMatchingBranch()
    {
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(NegativeCaseSecret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/org/repo" };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            YamlDefinition = "trigger: webhook\nbranches:\n  - main\n"
        };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);

        // Push to develop but the pipeline only listens on main.
        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/develop\"}";
        await _sut.HandleWebhookAsync(body, SignGitHub(body), ct: TestContext.Current.CancellationToken);

        // The repository matched (its YAML was read), the branch filter stopped it before the run gate.
        await _pipelineGitMock.Received(1).ReadProjectPipelineYamlAsync(
            1, "Build", Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>());
        await _pipelineRepoMock.DidNotReceive().HasActiveRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().TriggerAutomatedRunAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_BranchFilter_TriggersMatchingBranch()
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/org/repo" };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            YamlDefinition = "trigger: webhook\nbranches:\n  - main\n  - develop\n"
        };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/develop\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));
        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_BranchFilter_UsesFreshAuthoritativeGitYaml()
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project
        {
            Id = 1,
            Name = "P",
            Description = "d",
            RepositoryUrl = "https://github.com/org/repo"
        };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            SourceBranch = "main",
            // The DB mirror is deliberately stale and would reject a develop push.
            YamlDefinition = "trigger: webhook\nbranches:\n  - main\n"
        };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(10, Arg.Any<CancellationToken>())
            .Returns(false);
        _pipelineGitMock.ReadProjectPipelineYamlAsync(
                1, "Build", Arg.Any<CancellationToken>(), "main")
            .Returns("trigger: webhook\nbranches:\n  - develop\n");

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/develop\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));

        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _pipelineGitMock.Received(1).ReadProjectPipelineYamlAsync(
            1, "Build", Arg.Any<CancellationToken>(), "main");
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(
            10, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_BranchFilter_GlobMatchesReleaseBranch()
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/org/repo" };
        var pipeline = new Pipeline
        {
            Id = 30,
            Name = "Release",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            YamlDefinition = "trigger: webhook\nbranches:\n  - release/*\n"
        };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);
        _pipelineRepoMock.HasActiveRunAsync(30, Arg.Any<CancellationToken>())
            .Returns(false);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/release/2.0\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));
        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _runServiceMock.Received(1).TriggerAutomatedRunAsync(30, "Webhook", Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_BranchFilter_UnparseableYamlWithDeclaredFilter_FailsClosed()
    {
        // A real secret + valid HMAC signature so we actually reach the branch-matching path (an empty/null
        // secret would short-circuit to false at the mandatory-secret guard, never exercising fail-closed).
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project { Id = 1, Name = "P", Description = "d", RepositoryUrl = "https://github.com/org/repo" };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            // Genuinely unparseable YAML that STILL textually declares a branches: filter: a TAB used for
            // indentation is a hard YAML error (tabs are forbidden for indentation), so ParseAndValidate
            // returns null. The webhook must NOT fall back to "no filter" (which would match every branch -
            // the opposite of the author's intent) - it must fail closed and skip the pipeline instead.
            YamlDefinition = "branches:\n\t- main"
        };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);

        // Push to a branch that would not have matched the declared filter anyway, proving the skip
        // happens for the fail-closed reason rather than by accidental match.
        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/develop\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));
        var result = await _sut.HandleWebhookAsync(body, signature, ct: TestContext.Current.CancellationToken);

        // A configured secret makes HandleWebhookAsync report success ("valid webhook, processed") even when
        // no pipeline is triggered, so `result` is TRUE here - it is NOT the fail-closed signal. The real
        // proof of fail-closed is that the pipeline was NOT dispatched despite matching the repository.
        Assert.True(result);
        await _runServiceMock.DidNotReceive().TriggerAutomatedRunAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("trigger: webhook\nbranches: []\n")]
    [InlineData("trigger: webhook\nbranches:\n")]
    [InlineData("trigger: webhook\nbranches:\n  - \"\"\n")]
    public async Task HandleWebhookAsync_BranchFilter_ExplicitlyEmptyFilter_FailsClosed(string yaml)
    {
        const string secret = "test-webhook-secret";
        _settingsMock.GetSettingValueAsync("WebhookSecret", Arg.Any<CancellationToken>())
            .Returns(secret);

        var project = new Project
        {
            Id = 1,
            Name = "P",
            Description = "d",
            RepositoryUrl = "https://github.com/org/repo"
        };
        var pipeline = new Pipeline
        {
            Id = 10,
            Name = "Build",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 1,
            Project = project,
            YamlDefinition = yaml
        };

        _pipelineRepoMock.GetWebhookTriggeredPipelinesWithProjectAsync(Arg.Any<CancellationToken>())
            .Returns([pipeline]);

        var body = "{\"repository\":{\"html_url\":\"https://github.com/org/repo\"},\"ref\":\"refs/heads/main\"}";
        var signature = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(body)));

        var result = await _sut.HandleWebhookAsync(
            body, signature, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _runServiceMock.DidNotReceive().TriggerAutomatedRunAsync(
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }
}
