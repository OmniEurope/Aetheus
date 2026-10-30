// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineServiceTests
{
    private readonly IPipelineRepository _repoMock = Substitute.For<IPipelineRepository>();
    private readonly IPipelineGitService _pipelineGitMock = Substitute.For<IPipelineGitService>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly ILogger<PipelineService> _loggerMock = Substitute.For<ILogger<PipelineService>>();
    private readonly IHttpContextAccessor _httpContextAccessorMock = Substitute.For<IHttpContextAccessor>();
    private readonly IOrganizationRepository _organizationRepository = Substitute.For<IOrganizationRepository>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly PipelineService _sut;

    public PipelineServiceTests()
    {
        _pipelineGitMock.WriteProjectPipelineYamlAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitWriteOutcome.Committed, (string?)null));
        _pipelineGitMock.ApplyProjectPipelineChangeAsync(
                Arg.Any<PipelineGitDefinitionLocation?>(), Arg.Any<PipelineGitDefinitionLocation?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitWriteOutcome.Committed, (string?)null));
        _organizationRepository.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _sut = new PipelineService(
            _repoMock, _pipelineGitMock, _auditMock, _loggerMock, _notifier,
            _httpContextAccessorMock, TimeProvider.System,
            new PipelineTemplateResolver(_repoMock), _organizationRepository);
    }

    // F-EXEC-1b: simulate the authenticated principal behind a pipeline create/update.
    private void SetCurrentUser(string username)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test"))
        };
        _httpContextAccessorMock.HttpContext.Returns(ctx);
    }

    [Fact]
    public async Task GetPipelinesAsync_ReturnsMappedResult()
    {
        var pipelineDtos = new List<PipelineDto>
        {
            new() { Id = 1, Name = "Deploy", Description = "Deploy pipeline" }
        };
        _repoMock.GetPipelinesPagedProjectedAsync(null, null, null, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((pipelineDtos, 1));

        var result = await _sut.GetPipelinesAsync(new PipelinePaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Deploy", result.Items[0].Name);
    }

    [Fact]
    public async Task GetPipelinesAsync_EmptyList_ReturnsEmptyResult()
    {
        _repoMock.GetPipelinesPagedProjectedAsync(null, null, null, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<PipelineDto>(), 0));

        var result = await _sut.GetPipelinesAsync(new PipelinePaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetDependencyGroupsAsync_SplitsTriggerParentsFromLeaves()
    {
        var recentRuns = new List<PipelineRunSummaryDto>
        {
            new() { Id = 21, Status = PipelineStatus.Success }
        };
        _repoMock.GetPipelinesForDependencyGraphAsync(Arg.Any<List<int>?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineDto { Id = 1, Name = "release", RecentRuns = recentRuns, YamlDefinition = "name: release\ntrigger: manual\non_success:\n  - pipeline: deploy\nstages: []" },
            new PipelineDto { Id = 2, Name = "deploy", YamlDefinition = "name: deploy\ntrigger: manual\nstages: []" }
        ]);

        var groups = await _sut.GetDependencyGroupsAsync(ct: TestContext.Current.CancellationToken);

        var parent = Assert.Single(groups.Parents);
        Assert.Equal("release", parent.Name);
        Assert.Same(recentRuns, parent.RecentRuns);
        var reference = Assert.Single(parent.References);
        Assert.Equal("deploy", reference.Name);
        Assert.Equal(2, reference.Id);
        var leaf = Assert.Single(groups.Leaves);
        Assert.Equal("deploy", leaf.Name);
        Assert.Equal("release", Assert.Single(leaf.Parents).Name);
    }

    [Fact]
    public async Task GetDependencyGroupsAsync_UsesTriggerFromYamlInsteadOfStaleStoredValue()
    {
        _repoMock.GetPipelinesForDependencyGraphAsync(Arg.Any<List<int>?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineDto { Id = 1, Name = "hook", TriggerType = PipelineTriggerType.Manual,
                YamlDefinition = "name: hook\ntrigger: webhook\nstages: []" }
        ]);

        var groups = await _sut.GetDependencyGroupsAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(PipelineTriggerType.Webhook, Assert.Single(groups.Leaves).TriggerType);
    }

    [Fact]
    public async Task GetDependencyPageAsync_ParsesOnlyPage_AndResolvesOffPageReference()
    {
        var recentRuns = new List<PipelineRunSummaryDto>
        {
            new() { Id = 31, Status = PipelineStatus.Failed }
        };
        var page = new List<PipelineDto>
        {
            new() { Id = 1, Name = "release", ProjectId = 4, RecentRuns = recentRuns, YamlDefinition = "name: release\ntrigger: manual\non_success:\n  - pipeline: deploy\nstages: []" }
        };
        var identities = new List<PipelineDto>
        {
            new() { Id = 1, Name = "release", ProjectId = 4 },
            new() { Id = 2, Name = "deploy", ProjectId = 4 }
        };
        _repoMock.GetPipelineDependencyPageAsync(Arg.Any<PipelinePaginationRequest>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((page, identities, 51));

        var result = await _sut.GetDependencyPageAsync(new PipelinePaginationRequest { Page = 2, PageSize = 25 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(51, result.TotalCount);
        Assert.Equal(2, result.Page);
        var item = Assert.Single(result.Items);
        Assert.Equal(2, Assert.Single(item.References).Id);
        Assert.Same(recentRuns, item.RecentRuns);
    }

    [Fact]
    public async Task GetPipelineAsync_Found_ReturnsDto()
    {
        _repoMock.GetPipelineWithRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "CI", Description = "CI pipeline", Runs = [] });

        var result = await _sut.GetPipelineAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("CI", result.Name);
    }

    [Fact]
    public async Task GetPipelineAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetPipelineWithRunsAsync(99, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var result = await _sut.GetPipelineAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreatePipelineAsync_ReturnsMappedDto()
    {
        var request = new CreatePipelineRequest
        {
            Name = "New Pipeline",
            Description = "Test",
            YamlDefinition = "name: test\ntrigger: manual\nstages: []"
        };

        _repoMock.AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.CreatePipelineAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("New Pipeline", result.Name);
        Assert.Equal("Test", result.Description);
        await _repoMock.Received(1).AddPipelineAsync(Arg.Is<Pipeline>(p => p.Name == "New Pipeline"), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Created", "Pipeline", Arg.Any<int?>(), "New Pipeline", Arg.Any<CancellationToken>());
    }

    // --- Pipeline AI runner profile: must belong to the pipeline's organization ---

    [Fact]
    public async Task CreatePipelineAsync_WithAProfileOfTheSameOrganization_StoresIt()
    {
        _repoMock.GetAiRunnerProfileOrganizationIdAsync(11, Arg.Any<CancellationToken>()).Returns(4);
        _repoMock.GetPipelineOwnerOrganizationIdAsync(7, null, null, Arg.Any<CancellationToken>()).Returns(4);

        var result = await _sut.CreatePipelineAsync(new CreatePipelineRequest
        {
            Name = "P",
            ProjectId = 7,
            AiRunnerProfileId = 11,
            YamlDefinition = "name: p\ntrigger: manual\nstages: []"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(11, result.AiRunnerProfileId);
        await _repoMock.Received(1).AddPipelineAsync(
            Arg.Is<Pipeline>(p => p.AiRunnerProfileId == 11), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePipelineAsync_WithAProfileOfAnotherOrganization_IsRefused_BeforeAnyWrite()
    {
        _repoMock.GetAiRunnerProfileOrganizationIdAsync(11, Arg.Any<CancellationToken>()).Returns(5);
        _repoMock.GetPipelineOwnerOrganizationIdAsync(7, null, null, Arg.Any<CancellationToken>()).Returns(4);

        var exception = await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreatePipelineAsync(new CreatePipelineRequest
        {
            Name = "P",
            ProjectId = 7,
            AiRunnerProfileId = 11,
            YamlDefinition = "name: p\ntrigger: manual\nstages: []"
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("same organization", exception.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipelineAsync_WithAMissingProfile_IsRefused_BeforeTheGitWrite()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Old", ProjectId = 7, Runs = [] });
        _repoMock.GetAiRunnerProfileOrganizationIdAsync(404, Arg.Any<CancellationToken>()).Returns((int?)null);

        var exception = await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "New",
            ProjectId = 7,
            AiRunnerProfileId = 404,
            YamlDefinition = "name: p\ntrigger: manual\nstages: []"
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("AI runner profile not found", exception.Message, StringComparison.Ordinal);
        await _pipelineGitMock.DidNotReceive().ApplyProjectPipelineChangeAsync(
            Arg.Any<PipelineGitDefinitionLocation?>(), Arg.Any<PipelineGitDefinitionLocation?>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipelineAsync_SetsAndClearsTheProfile()
    {
        var existing = new Pipeline { Id = 1, Name = "Old", Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.GetAiRunnerProfileOrganizationIdAsync(11, Arg.Any<CancellationToken>()).Returns(4);
        _repoMock.GetPipelineOwnerOrganizationIdAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(4);
        var yaml = "name: p\ntrigger: manual\nstages: []";

        await _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "Old",
            AiRunnerProfileId = 11,
            YamlDefinition = yaml
        }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(11, existing.AiRunnerProfileId);

        await _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "Old",
            AiRunnerProfileId = null,
            YamlDefinition = yaml
        }, ct: TestContext.Current.CancellationToken);
        Assert.Null(existing.AiRunnerProfileId);
    }

    // --- Phase 5 git-first: a failed commit to the project's internal repo aborts the write ---

    [Fact]
    public async Task CreatePipelineAsync_GitCommitFails_ThrowsConflictAndSkipsDbWrite()
    {
        _pipelineGitMock.WriteProjectPipelineYamlAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitWriteOutcome.Failed, "push rejected"));

        var request = new CreatePipelineRequest
        {
            Name = "P",
            Description = "d",
            ProjectId = 7,
            YamlDefinition = "name: p\ntrigger: manual\nstages: []"
        };

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CreatePipelineAsync(request, ct: TestContext.Current.CancellationToken));
        // Git is the source of truth: a failed commit must NOT leave a divergent DB-only definition.
        await _repoMock.DidNotReceive().AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipelineAsync_GitCommitFails_ThrowsConflictAndDoesNotPersist()
    {
        var existing = new Pipeline { Id = 1, Name = "Old", ProjectId = 7, Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _pipelineGitMock.ApplyProjectPipelineChangeAsync(
                Arg.Any<PipelineGitDefinitionLocation?>(), Arg.Any<PipelineGitDefinitionLocation?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitWriteOutcome.Failed, "push rejected"));

        await Assert.ThrowsAsync<ConflictException>(() => _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "New",
            Description = "d",
            ProjectId = 7,
            YamlDefinition = "name: p\ntrigger: manual\nstages: []"
        }, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipelineAsync_RenameMovesAuthoritativeYamlBeforeDbSave()
    {
        SetCurrentUser("alice");
        var existing = new Pipeline
        {
            Id = 1,
            Name = "Old Name",
            ProjectId = 7,
            SourceBranch = "main",
            YamlDefinition = "name: old",
            Runs = []
        };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);

        await _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "New Name",
            ProjectId = 7,
            SourceBranch = "main",
            YamlDefinition = "name: new"
        }, ct: TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _pipelineGitMock.ApplyProjectPipelineChangeAsync(
                new PipelineGitDefinitionLocation(7, "Old Name", "main"),
                new PipelineGitDefinitionLocation(7, "New Name", "main"),
                "name: new", "alice", Arg.Any<CancellationToken>());
            _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task UpdatePipelineAsync_DbFailure_RestoresEntityAndCompensatesGit()
    {
        var existing = new Pipeline
        {
            Id = 1,
            Name = "Old",
            Description = "old description",
            ProjectId = 7,
            SourceBranch = "main",
            YamlDefinition = "name: old",
            Runs = []
        };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ =>
            throw new InvalidOperationException("db failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdatePipelineAsync(1,
            new UpdatePipelineRequest
            {
                Name = "New",
                Description = "new description",
                ProjectId = 7,
                SourceBranch = "main",
                YamlDefinition = "name: new"
            }, ct: TestContext.Current.CancellationToken));

        Assert.Equal("Old", existing.Name);
        Assert.Equal("old description", existing.Description);
        Assert.Equal("name: old", existing.YamlDefinition);
        await _pipelineGitMock.Received(1).ApplyProjectPipelineChangeAsync(
            new PipelineGitDefinitionLocation(7, "New", "main"),
            new PipelineGitDefinitionLocation(7, "Old", "main"),
            "name: old", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- F-EXEC-1b: pipeline ownership capture ---

    [Fact]
    public async Task CreatePipelineAsync_SetsCreatedByUsernameFromCurrentUser()
    {
        SetCurrentUser("alice");
        Pipeline? captured = null;
        _repoMock.AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => { captured = ci.Arg<Pipeline>(); });

        await _sut.CreatePipelineAsync(new CreatePipelineRequest
        {
            Name = "P",
            Description = "d",
            YamlDefinition = "name: p\ntrigger: manual\nstages: []"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("alice", captured!.CreatedByUsername);
    }

    [Fact]
    public async Task UpdatePipelineAsync_UnownedPipeline_AdoptsCurrentUserAsOwner()
    {
        SetCurrentUser("bob");
        var existing = new Pipeline { Id = 1, Name = "Old", CreatedByUsername = null, Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "New",
            Description = "d",
            YamlDefinition = "name: p\ntrigger: manual\nstages: []"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("bob", existing.CreatedByUsername);
    }

    [Fact]
    public async Task UpdatePipelineAsync_OwnedPipeline_DoesNotReassignOwner()
    {
        // Renaming or describing a pipeline leaves its definition, so the owner stays (RBAC audit, finding 3:
        // only an edited definition moves ownership, see the test below).
        SetCurrentUser("bob");
        const string yaml = "name: p\ntrigger: manual\nstages: []";
        var existing = new Pipeline { Id = 1, Name = "Old", CreatedByUsername = "alice", YamlDefinition = yaml, Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "New",
            Description = "d",
            YamlDefinition = yaml
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("alice", existing.CreatedByUsername);
    }

    [Fact]
    public async Task UpdatePipelineAsync_EditedDefinition_MovesOwnershipToTheEditor()
    {
        // RBAC audit, finding 3: automated runs are authorized with the owner's Server.Admin grants, so the
        // user who writes new shell into the definition becomes the owner instead of borrowing alice's grants.
        SetCurrentUser("bob");
        var existing = new Pipeline
        {
            Id = 1,
            Name = "Old",
            CreatedByUsername = "alice",
            YamlDefinition = "name: p\ntrigger: manual\nstages: []",
            Runs = []
        };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "Old",
            Description = "d",
            YamlDefinition = "name: p\ntrigger: manual\nstages:\n  - name: Build\n    steps:\n      - name: Say\n        shell: echo two"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("bob", existing.CreatedByUsername);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_PushedDefinitionOffRequest_MovesOwnershipToThePusher()
    {
        // A git push is synchronised by a background subscriber (no HttpContext): the pusher travels with the
        // event and becomes the owner of the edited definition (RBAC audit, finding 3).
        var existing = new Pipeline
        {
            Id = 5,
            Name = "aetheus-ci",
            CreatedByUsername = "alice",
            YamlDefinition = "name: aetheus-ci\ntrigger: manual\nstages: []",
            Runs = []
        };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-ci", "name: aetheus-ci\ntrigger: webhook\nstages: []", 1, "webhook",
            ct: TestContext.Current.CancellationToken, actorUsername: "mallory");

        Assert.Equal("mallory", existing.CreatedByUsername);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_UnchangedDefinition_KeepsTheOwner()
    {
        // A push that does not touch the definition file gives its pusher nothing to own.
        const string yaml = "name: aetheus-ci\ntrigger: manual\nstages: []";
        var existing = new Pipeline { Id = 5, Name = "aetheus-ci", CreatedByUsername = "alice", YamlDefinition = yaml, Runs = [] };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-ci", yaml, 1, "manual", ct: TestContext.Current.CancellationToken, actorUsername: "mallory");

        Assert.Equal("alice", existing.CreatedByUsername);
    }

    [Fact]
    public async Task UpdatePipelineAsync_SelectedSourceBranch_CommitsAndPersistsThatBranch()
    {
        SetCurrentUser("alice");
        var existing = new Pipeline { Id = 1, Name = "Old", ProjectId = 7, Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpdatePipelineAsync(1, new UpdatePipelineRequest
        {
            Name = "Release",
            Description = "d",
            ProjectId = 7,
            SourceBranch = "release/2026.07",
            YamlDefinition = "name: release\ntrigger: manual\nstages: []"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("release/2026.07", existing.SourceBranch);
        await _pipelineGitMock.Received(1).ApplyProjectPipelineChangeAsync(
            new PipelineGitDefinitionLocation(7, "Old", null),
            new PipelineGitDefinitionLocation(7, "Release", "release/2026.07"),
            Arg.Any<string>(), "alice", Arg.Any<CancellationToken>());
    }

    // --- F-INF-02b: git-sync owner self-heal (the prod class where a legacy "system" owner stranded
    // every chained/automated trigger of an existing pipeline through any number of pushes) ---

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingSystemOwner_RepairedToCurrentPusher()
    {
        SetCurrentUser("carol");
        var existing = new Pipeline { Id = 5, Name = "aetheus-ci", CreatedByUsername = "system", Runs = [] };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync("aetheus-ci", "name: aetheus-ci\ntrigger: manual\nstages: []", 1, "manual", ct: TestContext.Current.CancellationToken);

        Assert.Equal("carol", existing.CreatedByUsername);
        // A real user check is unnecessary for the "system" sentinel - it is unconditionally unresolvable.
        await _repoMock.DidNotReceive().IsActiveUsernameAsync("system", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingPipeline_RefreshesItsAutomatedTriggerType()
    {
        var existing = new Pipeline
        {
            Id = 5,
            Name = "aetheus-release",
            TriggerType = PipelineTriggerType.Manual,
            CreatedByUsername = "alice",
            Runs = []
        };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-release", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync("aetheus-release", "name: aetheus-release\ntrigger: webhook\nstages: []", 1, "webhook", ct: TestContext.Current.CancellationToken);

        Assert.Equal(PipelineTriggerType.Webhook, existing.TriggerType);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_NewPipeline_BroadcastsCreatedToProjectOrganization()
    {
        _repoMock.GetProjectOwnerUsernameAsync(1, Arg.Any<CancellationToken>()).Returns("orgowner");
        _repoMock.GetPipelineOwnerOrganizationIdAsync(1, null, null, Arg.Any<CancellationToken>()).Returns(9);
        _repoMock.AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Pipeline>().Id = 41;
                return Task.CompletedTask;
            });

        await _sut.UpsertPipelineFromYamlAsync(
            "new-pipeline", "name: new-pipeline\ntrigger: manual\nstages: []", 1, "manual", ct: TestContext.Current.CancellationToken);

        await _notifier.Received(1).BroadcastAsync(
            ResourceType.Pipeline, 41, EntityChangeOps.Created, Arg.Any<CancellationToken>(), 9);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_NewPipelineWithoutResolvableOwner_FailsClosed()
    {
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpsertPipelineFromYamlAsync(
                "new-pipeline", "name: new-pipeline\ntrigger: manual\nstages: []", 1, "manual",
                ct: TestContext.Current.CancellationToken));

        Assert.Contains("no authenticated pusher or organization owner", exception.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>());
        await _notifier.DidNotReceiveWithAnyArgs().BroadcastAsync(
            default, default, default!, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingPipeline_BroadcastsUpdatedToProjectOrganization()
    {
        var existing = new Pipeline
        {
            Id = 5,
            ProjectId = 1,
            Name = "aetheus-ci",
            CreatedByUsername = "alice",
            Runs = []
        };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetPipelineOwnerOrganizationIdAsync(1, null, null, Arg.Any<CancellationToken>()).Returns(9);

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-ci", "name: aetheus-ci\ntrigger: manual\nstages: []", 1, "manual", ct: TestContext.Current.CancellationToken);

        await _notifier.Received(1).BroadcastAsync(
            ResourceType.Pipeline, 5, EntityChangeOps.Updated, Arg.Any<CancellationToken>(), 9);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingPipeline_UsesCanonicalYamlSourceBranch()
    {
        var existing = new Pipeline
        {
            Id = 5,
            Name = "aetheus-nightly",
            SourceBranch = null,
            TriggerType = PipelineTriggerType.Schedule,
            CreatedByUsername = "alice",
            Runs = []
        };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-nightly", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-nightly",
            "name: aetheus-nightly\ntrigger: schedule\nsource_branch: develop\nstages: []",
            1,
            "schedule",
            TestContext.Current.CancellationToken,
            "main",
            "main");

        Assert.Equal("develop", existing.SourceBranch);
    }

    /// <summary>A null source branch means "the repository default branch", not "unknown": a definition
    /// that declares <c>source_branch</c> equal to the default branch is stored as null (this is how
    /// the production demo pipeline, declaring main on a repository whose default branch is main,
    /// reads back with a null source branch). A different branch is stored explicitly.</summary>
    [Theory]
    [InlineData("main", "main", null)]
    [InlineData("main", "develop", "main")]
    public async Task UpsertPipelineFromYamlAsync_NewPipeline_NormalizesTheDeclaredBranchAgainstTheDefault(
        string declaredBranch, string defaultBranch, string? expectedStoredBranch)
    {
        _repoMock.GetProjectOwnerUsernameAsync(1, Arg.Any<CancellationToken>()).Returns("orgowner");
        Pipeline? added = null;
        _repoMock.AddPipelineAsync(Arg.Do<Pipeline>(pipeline => added = pipeline), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-demo",
            $"name: aetheus-demo\ntrigger: schedule\nsource_branch: {declaredBranch}\nstages: []",
            1, "schedule", TestContext.Current.CancellationToken, declaredBranch, defaultBranch);

        Assert.NotNull(added);
        Assert.Equal(expectedStoredBranch, added.SourceBranch);
    }

    /// <summary>The state production was in: a stored null source branch on a repository whose default
    /// branch is main, for a definition declaring <c>source_branch: main</c>. The main branch moving
    /// must refresh the stored definition (the scheduler reads its cron), while a push of another
    /// branch must not overwrite it with that branch's copy.</summary>
    [Theory]
    [InlineData("main", true)]
    [InlineData("develop", false)]
    public async Task UpsertPipelineFromYamlAsync_NullStoredBranchOnDefaultMain_FollowsOnlyMain(
        string movedBranch, bool expectedRefreshed)
    {
        var existing = new Pipeline
        {
            Id = 62,
            Name = "aetheus-demo",
            SourceBranch = null,
            YamlDefinition = "name: aetheus-demo\ntrigger: schedule\nschedule: \"0 0 3 * * *\"\nstages: []",
            TriggerType = PipelineTriggerType.Schedule,
            CreatedByUsername = "alice",
            Runs = []
        };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-demo", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);
        const string fresh = "name: aetheus-demo\ntrigger: schedule\nschedule: \"0 0 2 * * *\"\nsource_branch: main\nstages: []";

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-demo", fresh, 1, "schedule", TestContext.Current.CancellationToken, movedBranch, "main");

        Assert.Equal(expectedRefreshed, existing.YamlDefinition == fresh);
        Assert.Null(existing.SourceBranch);
        await _repoMock.Received(expectedRefreshed ? 1 : 0).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_OtherBranch_DoesNotOverwriteSelectedDefinition()
    {
        var existing = new Pipeline
        {
            Id = 5,
            Name = "aetheus-release",
            SourceBranch = "develop",
            YamlDefinition = "selected",
            TriggerType = PipelineTriggerType.Manual,
            Runs = []
        };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-release", 1, Arg.Any<CancellationToken>()).Returns(existing);

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-release", "other", 1, "webhook", TestContext.Current.CancellationToken, "feature/test", "main");

        Assert.Equal("selected", existing.YamlDefinition);
        Assert.Equal(PipelineTriggerType.Manual, existing.TriggerType);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_CanonicalYamlBranch_RepairsStaleBranchAndTrigger()
    {
        var existing = new Pipeline
        {
            Id = 29,
            Name = "aetheus-quality",
            SourceBranch = "codex/sync-11ad",
            YamlDefinition = "trigger: webhook",
            TriggerType = PipelineTriggerType.Webhook,
            CreatedByUsername = "alice",
            Runs = []
        };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-quality", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);

        await _sut.UpsertPipelineFromYamlAsync(
            "aetheus-quality",
            "name: aetheus-quality\ntrigger: manual\nsource_branch: develop\nstages: []",
            1,
            "manual",
            TestContext.Current.CancellationToken,
            "develop",
            "main");

        Assert.Equal("develop", existing.SourceBranch);
        Assert.Equal(PipelineTriggerType.Manual, existing.TriggerType);
        Assert.Contains("trigger: manual", existing.YamlDefinition, StringComparison.Ordinal);
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingSystemOwner_NoPusher_FallsBackToProjectOwner()
    {
        // No HttpContext (background git-push sync) - resolve the project's org Owner instead.
        var existing = new Pipeline { Id = 5, Name = "aetheus-ci", CreatedByUsername = "system", Runs = [] };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.GetProjectOwnerUsernameAsync(1, Arg.Any<CancellationToken>()).Returns("orgowner");
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync("aetheus-ci", "name: aetheus-ci\ntrigger: manual\nstages: []", 1, "manual", ct: TestContext.Current.CancellationToken);

        Assert.Equal("orgowner", existing.CreatedByUsername);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingUnresolvableOwnerWithoutReplacement_FailsClosed()
    {
        var existing = new Pipeline { Id = 5, Name = "aetheus-ci", CreatedByUsername = "system", Runs = [] };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpsertPipelineFromYamlAsync(
                "aetheus-ci", "name: aetheus-ci\ntrigger: manual\nstages: []", 1, "manual",
                ct: TestContext.Current.CancellationToken));

        Assert.Contains("no authenticated pusher or organization owner", exception.Message, StringComparison.Ordinal);
        Assert.Equal("system", existing.CreatedByUsername);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingOrphanedOwner_Repaired()
    {
        // Owner is a real-looking username whose account was since removed/deactivated: still unresolvable.
        SetCurrentUser("carol");
        var existing = new Pipeline { Id = 5, Name = "aetheus-ci", CreatedByUsername = "ghost", Runs = [] };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("ghost", Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync("aetheus-ci", "name: aetheus-ci\ntrigger: manual\nstages: []", 1, "manual", ct: TestContext.Current.CancellationToken);

        Assert.Equal("carol", existing.CreatedByUsername);
    }

    [Fact]
    public async Task UpsertPipelineFromYamlAsync_ExistingActiveOwner_ReassignedToRevisionAuthor()
    {
        // The pusher controls the new shell revision, so future automated runs must be authorized
        // with the pusher's Server.Admin grants rather than a previous owner's broader grants.
        SetCurrentUser("carol");
        var existing = new Pipeline { Id = 5, Name = "aetheus-ci", CreatedByUsername = "alice", Runs = [] };
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpsertPipelineFromYamlAsync("aetheus-ci", "name: aetheus-ci\ntrigger: manual\nstages: []", 1, "manual", ct: TestContext.Current.CancellationToken);

        Assert.Equal("carol", existing.CreatedByUsername);
        await _repoMock.DidNotReceive().IsActiveUsernameAsync("alice", Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().GetProjectOwnerUsernameAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipelineAsync_Found_UpdatesAndReturnsDto()
    {
        var existing = new Pipeline { Id = 1, Name = "Old", Description = "old", YamlDefinition = "", Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(existing);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new UpdatePipelineRequest
        {
            Name = "Updated",
            Description = "updated desc",
            YamlDefinition = "name: updated\ntrigger: webhook\nstages: []"
        };

        var result = await _sut.UpdatePipelineAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Updated", result.Name);
        Assert.Equal("updated desc", result.Description);
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Updated", "Pipeline", 1, "Updated", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipelineAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var result = await _sut.UpdatePipelineAsync(99, new UpdatePipelineRequest
        {
            Name = "X",
            Description = "",
            YamlDefinition = "name: x\nstages: []"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _auditMock.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePipelineAsync_Found_ReturnsTrue()
    {
        var existing = new Pipeline { Id = 1, Name = "ToDelete", Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(existing);
        _repoMock.RemovePipelineAsync(existing, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.DeletePipelineAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).RemovePipelineAsync(existing, Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Deleted", "Pipeline", 1, "ToDelete", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePipelineAsync_GitDeleteFails_DoesNotDeleteDbRow()
    {
        var existing = new Pipeline { Id = 1, Name = "ToDelete", ProjectId = 7, YamlDefinition = "name: delete", Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _pipelineGitMock.ApplyProjectPipelineChangeAsync(
                Arg.Any<PipelineGitDefinitionLocation?>(),
                Arg.Is<PipelineGitDefinitionLocation?>(location => location == null),
                Arg.Is<string?>(yaml => yaml == null),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitWriteOutcome.Failed, "push rejected"));

        await Assert.ThrowsAsync<ConflictException>(() => _sut.DeletePipelineAsync(1, ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().RemovePipelineAsync(
            Arg.Any<Pipeline>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePipelineAsync_DbFailure_RestoresAuthoritativeYaml()
    {
        var existing = new Pipeline
        {
            Id = 1,
            Name = "ToDelete",
            ProjectId = 7,
            SourceBranch = "main",
            YamlDefinition = "name: delete",
            Runs = []
        };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _repoMock.RemovePipelineAsync(existing, Arg.Any<CancellationToken>()).Returns<Task>(_ =>
            throw new InvalidOperationException("db failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.DeletePipelineAsync(1, ct: TestContext.Current.CancellationToken));

        await _pipelineGitMock.Received(1).ApplyProjectPipelineChangeAsync(
            null, new PipelineGitDefinitionLocation(7, "ToDelete", "main"),
            "name: delete", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Recette R-95: a failed delete puts back the repository's file, not Aetheus's older copy of it.
    [Fact]
    public async Task DeletePipelineAsync_DbFailure_RestoresTheRepositoryFileNotAetheusCopy()
    {
        var existing = new Pipeline
        {
            Id = 1,
            Name = "ToDelete",
            ProjectId = 7,
            SourceBranch = "main",
            YamlDefinition = "name: delete",
            Runs = []
        };
        const string repositoryYaml = "name: delete\nvariables:\n  COMPOSE_FILE: compose.yml";
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(existing);
        _pipelineGitMock.ReadProjectPipelineYamlAsync(
                7, "ToDelete", Arg.Any<CancellationToken>(), "main", Arg.Any<int?>())
            .Returns(repositoryYaml);
        _repoMock.RemovePipelineAsync(existing, Arg.Any<CancellationToken>()).Returns<Task>(_ =>
            throw new InvalidOperationException("db failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.DeletePipelineAsync(1, ct: TestContext.Current.CancellationToken));

        await _pipelineGitMock.Received(1).ApplyProjectPipelineChangeAsync(
            null, new PipelineGitDefinitionLocation(7, "ToDelete", "main"),
            repositoryYaml, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePipelineAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var result = await _sut.DeletePipelineAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public void ValidateYaml_ValidYaml_ReturnsDefinition()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYaml(yaml);

        Assert.NotNull(result);
        Assert.Equal("deploy", result.Name);
        Assert.Single(result.Stages);
        Assert.Equal("build", result.Stages[0].Name);
    }

    [Fact]
    public void ValidateYaml_InvalidYaml_ReturnsNull()
    {
        var result = _sut.ValidateYaml("{{{{not valid yaml at all");

        Assert.Null(result);
    }

    [Fact]
    public void ValidateYaml_EmptyVariableLibrary_ReturnsNull()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variable_libraries:
              - ""
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        Assert.Null(_sut.ValidateYaml(yaml));
    }

    [Fact]
    public void ValidateYaml_EmptyVaultName_ReturnsNull()
    {
        var yaml = """
            name: deploy
            trigger: manual
            vaults:
              - ""
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        Assert.Null(_sut.ValidateYaml(yaml));
    }

    [Fact]
    public void ValidateYaml_EmptyDefinition_ReturnsNull()
    {
        Assert.Null(_sut.ValidateYaml("---"));
    }

    // --- MapToDto coverage ---

    [Fact]
    public async Task GetPipelineAsync_WithProjectAndRuns_MapsAllFields()
    {
        var pipeline = new Pipeline
        {
            Id = 1,
            Name = "CI",
            Description = "CI pipeline",
            YamlDefinition = "name: ci",
            TriggerType = PipelineTriggerType.Webhook,
            ProjectId = 5,
            Project = new Project { Id = 5, Name = "MyProject" },
            Runs = [
                new PipelineRun { Id = 10, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow }
            ]
        };
        _repoMock.GetPipelineWithRunsAsync(1, Arg.Any<CancellationToken>()).Returns(pipeline);

        var result = await _sut.GetPipelineAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(PipelineTriggerType.Webhook, result.TriggerType);
        Assert.Equal(5, result.ProjectId);
        Assert.Equal("MyProject", result.ProjectName);
        Assert.Equal(PipelineStatus.Success, result.LastRunStatus);
        Assert.NotNull(result.LastRunAt);
        Assert.Single(result.RecentRuns);
    }

    // --- ValidateYamlStrict ---

    [Fact]
    public async Task ValidateYamlStrictAsync_ExtendingPipelineValidatesResolvedTemplateStages()
    {
        _repoMock.GetPipelineOwnerOrganizationIdAsync(5, null, null, Arg.Any<CancellationToken>())
            .Returns(4);
        _repoMock.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Id = 7,
            Name = "ci",
            OrganizationId = 4,
            LatestVersion = 1,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1,
                    YamlContent = "name: ci\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: compile\n        shell: echo ok"
                }
            ]
        });

        var result = await _sut.ValidateYamlStrictAsync(
            "name: toto\nextends: ci@1\nstages: []", 5, null, null, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, string.Join(' ', result.Errors));
        Assert.Single(result.Definition!.Stages);
        Assert.Equal("build", result.Definition.Stages[0].Name);
    }

    [Fact]
    public void ValidateYamlStrict_ValidYaml_ReturnsValid()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ValidateYamlStrict_StageNamesWithSpaces_ReturnValid()
    {
        // A stage name is a label a human reads in the run view, and it may contain spaces. The
        // reason it could not, in practice, was that the artifact bundle was named after it; that
        // coupling is now broken by artifact_name.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: Test backend
                agent: linux-01
                steps:
                  - name: run
                    shell: make test
              - name: Build images
                agent: linux-01
                depends_on: [Test backend]
                steps:
                  - name: build
                    shell: make images
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ValidateYamlStrict_TwoStagesPublishingTheSameArtifact_ReturnsError()
    {
        // The second publication replaces the first and the consumer restores whichever ran last,
        // which is a silent wrong answer rather than a failure.
        var yaml = """
            name: ci
            trigger: manual
            stages:
              - name: Package one
                agent: linux-01
                artifact_name: Payload-artifacts
                artifacts:
                  - "out/**"
                steps:
                  - name: one
                    shell: make one
              - name: Package two
                agent: linux-01
                artifact_name: Payload-artifacts
                artifacts:
                  - "out/**"
                steps:
                  - name: two
                    shell: make two
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("both publish artifact 'Payload-artifacts'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_ARenamedProducerKeepingItsArtifactName_ReturnsValid()
    {
        // The point of the field: rename the stage, keep the name every consumer and every retained
        // release already restores.
        var yaml = """
            name: nightly
            trigger: manual
            stages:
              - name: Publish evidence
                agent: linux-01
                artifact_name: PublishNightlyEvidence-artifacts
                artifacts:
                  - ".nightly-evidence"
                steps:
                  - name: verify
                    shell: test -s .nightly-evidence/x.json
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ValidateYamlStrict_UnknownArtifactSourceSelector_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-evidence
                    type: restore-artifacts
                    artifact: Evidence-artifacts
                    artifact_source_pipeline: nightly
                    artifact_source_selector: newest-anywhere
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("artifact_source_selector", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_ArtifactSourceSelectorBesideRelease_ReturnsError()
    {
        // The selector widens an artifact_source_pipeline lookup and means nothing beside a release
        // selector. Refused rather than ignored, so a definition cannot read as something it is not.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-evidence
                    type: restore-artifacts
                    release: previous-deployed
                    artifact: Evidence-artifacts
                    artifact_source_selector: latest-successful
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("cannot combine 'release' with 'artifact_source_selector'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_LatestSuccessfulArtifactSourceSelector_ReturnsValid()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-evidence
                    type: restore-artifacts
                    artifact: Evidence-artifacts
                    artifact_source_pipeline: nightly
                    artifact_source_selector: latest-successful
                    allow_missing: true
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        // The key is declared, not merely tolerated: an unknown-property warning would tell the
        // author the selector is ignored, which it is not.
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ValidateYamlStrict_ShellShapedMatrixValue_ReturnsError()
    {
        // S-UX-MXVL: a matrix value carrying a shell metacharacter (backslash) must be rejected at SAVE
        // time, not only at run-creation, so the editor flags it immediately.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                matrix:
                  target:
                    - "C:\\evil"
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("matrix value", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_MalformedContainerLimit_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            isolation:
              mode: container
              image: alpine:3.22@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
              memory: definitely-not-a-limit
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("memory limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateYamlStrict_SyntaxError_ReturnsInvalid()
    {
        var result = _sut.ValidateYamlStrict("{{{{invalid yaml");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateYamlStrict_EmptyYaml_ReturnsInvalid()
    {
        var result = _sut.ValidateYamlStrict("---");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Empty"));
    }

    [Fact]
    public void ValidateYamlStrict_NoStages_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages: []
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("at least one stage"));
    }

    [Fact]
    public void ValidateYamlStrict_InvalidCanonicalSourceBranch_ReturnsError()
    {
        var yaml = """
            name: nightly
            trigger: schedule
            source_branch: ../develop
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("source_branch", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_TriggerMaySelectIndependentConformanceBranch()
    {
        const string yaml = """
            name: conformance
            trigger: manual
            stages:
              - name: baseline
                steps:
                  - name: launch-v1
                    type: trigger
                    pipeline: candidate
                    inherit_source: false
                    source_branch: conformance/v1
                    source_commit: "$(CANDIDATE_COMMIT)"
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(System.Environment.NewLine, result.Errors));
        var step = Assert.Single(Assert.Single(result.Definition!.Stages).Steps);
        Assert.False(step.InheritSource);
        Assert.Equal("conformance/v1", step.SourceBranch);
        Assert.Equal("$(CANDIDATE_COMMIT)", step.SourceCommit);
    }

    [Fact]
    public void ValidateYamlStrict_TriggerMaySupplyChildParameters()
    {
        const string yaml = """
            name: conformance
            trigger: manual
            stages:
              - name: promote
                steps:
                  - name: launch-promotion
                    type: trigger
                    pipeline: promotion
                    parameters:
                      candidateVersion: "$(CANDIDATE_VERSION)"
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(System.Environment.NewLine, result.Errors));
        var step = Assert.Single(Assert.Single(result.Definition!.Stages).Steps);
        Assert.Equal("$(CANDIDATE_VERSION)", step.Parameters["candidateVersion"]);
    }

    [Theory]
    [InlineData("""
        name: invalid
        trigger: manual
        stages:
          - name: baseline
            steps:
              - name: launch-v1
                type: trigger
                pipeline: candidate
                inherit_source: false
        """)]
    [InlineData("""
        name: invalid
        trigger: manual
        stages:
          - name: baseline
            steps:
              - name: launch-v1
                type: trigger
                pipeline: candidate
                source_branch: conformance/v1
        """)]
    [InlineData("""
        name: invalid
        trigger: manual
        stages:
          - name: baseline
            steps:
              - name: launch-v1
                type: trigger
                pipeline: candidate
                source_commit: "$(CANDIDATE_COMMIT)"
        """)]
    public void ValidateYamlStrict_TriggerIndependentSourceContractIsFailClosed(string yaml)
    {
        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("source_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateYamlStrict_StageWithoutAgent_ReturnsWarning()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("affinity"));
    }

    [Fact]
    public void ValidateYamlStrict_LegacyCoverageThresholdReturnsMigrationWarning()
    {
        const string yaml = """
            name: quality
            stages:
              - name: Analysis
                agent: build
                steps:
                  - name: Coverage
                    type: coverage
                    min_coverage: 75
                    target_files: [coverage.xml]
              # BACK-016: a gate never shares the stage of its producer (steps of a stage run concurrently).
              - name: Grade
                agent: build
                depends_on: [Analysis]
                steps:
                  - name: Gate
                    type: analysis-gate
                    analysis_scope: quality
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(' ', result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("legacy min_coverage", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_StepWithoutShell_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("shell") || e.Contains("checkout"));
    }

    // Unsupported keys are accepted with explicit warnings for forward compatibility.
    [Fact]
    public void ValidateYamlStrict_UnknownTopLevelProperty_IsAcceptedWithAWarning()
    {
        var yaml = """
            name: deploy
            trigger: manual
            custom_field: hello
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning =>
            warning == "Unknown top-level property 'custom_field' will be ignored.");
    }

    [Fact]
    public void ValidateYamlStrict_AMisspelledGuardKey_IsRefused_NotSkipped()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: confirm
                agent: linux-01
                approval_timeout_minuts: 10
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("'approval_timeout_minuts'", StringComparison.Ordinal)
            && error.Contains("'approval_timeout_minutes'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_AStepTypeOfANewerBackend_IsAcceptedWithAWarning()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: release
                agent: linux-01
                steps:
                  - name: future step
                    type: publish-to-the-moon
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("unknown type 'publish-to-the-moon'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_AStepWithNeitherTypeNorShell_IsStillRefused()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: empty
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("must have a shell command", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_UnknownStageProperty_IsAcceptedWithAWarning()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                timeout: 300
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Contains(result.Warnings, warning =>
            warning == "Unknown stage 'build' property 'timeout' will be ignored.");
    }

    [Fact]
    public void ValidateYamlStrict_UnknownStepProperty_IsAcceptedWithAWarning()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
                    unknown_prop: value
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Contains(result.Warnings, warning =>
            warning == "Unknown step property 'unknown_prop' will be ignored.");
    }

    [Fact]
    public void ValidateYamlStrict_KnownKeyWithTheWrongType_IsStillRejected()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
                    timeout_seconds: soon
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("$(TARGET_BRANCH)", true)]
    [InlineData("", false)]
    [InlineData("bad..name", false)]
    public void ValidateYamlStrict_AdvanceBranchStep_RequiresAValidBranch(string branch, bool valid)
    {
        var yaml = $$"""
            name: deploy
            trigger: manual
            stages:
              - name: Advance main
                environment: prod
                steps:
                  - name: Advance main
                    type: advance-branch
                    branch: "{{branch}}"
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.Equal(valid, result.IsValid);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("'branch'", StringComparison.Ordinal));
        if (!valid)
            Assert.Contains(result.Errors, error => error.Contains("branch", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateYamlStrict_ReleaseTag_IsAKeyOfTheAdvanceStepOnly(bool valid)
    {
        var body = valid ? "type: advance-branch" : "shell: echo deployed";
        var branch = valid ? "branch: main" : "timeout_seconds: 60";
        var yaml = $$"""
            name: deploy
            trigger: manual
            stages:
              - name: Advance main
                environment: prod
                steps:
                  - name: Advance main
                    {{body}}
                    {{branch}}
                    tag: "v$(APP_VERSION)"
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.Equal(valid, result.IsValid);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("'tag'", StringComparison.Ordinal));
        if (!valid)
            Assert.Contains(result.Errors, error => error.Contains("'tag'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_AScalarWhereAListIsExpected_IsStillRejected()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages: build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateYamlStrict_EmptyVaultName_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            vaults:
              - ""
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Vault"));
    }

    [Fact]
    public void ValidateYamlStrict_EmptyVariableLibraryName_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variable_libraries:
              - ""
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Variable library"));
    }

    [Fact]
    public void ValidateYamlStrict_NegativeRetryCount_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
                    retry_count: -1
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("retry_count") || e.Contains("negative"));
    }

    [Fact]
    public void ValidateYamlStrict_RestoreArtifactsReleaseWithSafeTarget_IsValid()
    {
        var yaml = """
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux
                steps:
                  - name: restore previous
                    type: restore-artifacts
                    release: previous-deployed
                    artifact: BuildArtifacts-artifacts
                    target_directory: .nminus1
                    allow_missing: true
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(System.Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateYamlStrict_RestoreArtifactsAllowMissing_AllowsOptionalProducerArtifact()
    {
        var yaml = """
            name: candidate
            trigger: manual
            stages:
              - name: assurance
                agent: linux
                steps:
                  - name: restore advisory summary
                    type: restore-artifacts
                    artifact: analysis-summary-json
                    artifact_source_pipeline: quality
                    target_directory: .assurance-input/quality
                    allow_missing: true
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(System.Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("$(RELEASE)")]
    public void ValidateYamlStrict_RestoreArtifactsAllowMissing_RejectsNonBootstrapSelector(string selector)
    {
        var yaml = $$"""
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux
                steps:
                  - name: restore previous
                    type: restore-artifacts
                    release: {{selector}}
                    allow_missing: true
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("allow_missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("previous-deployed", "none")]
    [InlineData("current-deployed", "none")]
    [InlineData("previous-deployed", "required")]
    [InlineData("current-deployed", "NONE")]
    public void ValidateYamlStrict_RestoreArtifactsDeployedBaseline_AcceptsKnownValueOnDeployedSelector(
        string selector, string value)
    {
        var yaml = $$"""
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux
                steps:
                  - name: restore previous agent contract
                    type: restore-artifacts
                    release: {{selector}}
                    artifact: AgentRelease-artifacts
                    target_directory: .nminus1
                    allow_missing: true
                    deployed_baseline: {{value}}
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid, string.Join(System.Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateYamlStrict_RestoreArtifactsDeployedBaseline_RejectsUnknownValue()
    {
        var yaml = """
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux
                steps:
                  - name: restore previous agent contract
                    type: restore-artifacts
                    release: previous-deployed
                    artifact: AgentRelease-artifacts
                    deployed_baseline: optional
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("deployed_baseline must be 'required' or 'none'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("release: latest-published")]
    [InlineData("release: previous-published")]
    [InlineData("release: 1.0.42")]
    [InlineData("release: $(RELEASE)")]
    [InlineData("artifact_source_pipeline: aetheus-ci")]
    public void ValidateYamlStrict_RestoreArtifactsDeployedBaseline_RejectsNonDeployedSource(string sourceLine)
    {
        var yaml = $$"""
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux
                steps:
                  - name: restore agent contract
                    type: restore-artifacts
                    {{sourceLine}}
                    artifact: AgentRelease-artifacts
                    deployed_baseline: none
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("deployed_baseline requires release: previous-deployed or current-deployed", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_DeployedBaselineOnAnotherStepType_IsRejected()
    {
        var yaml = """
            name: build
            trigger: manual
            stages:
              - name: build
                agent: linux
                steps:
                  - name: compile
                    shell: echo build
                    deployed_baseline: none
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("deployed_baseline is only valid on a restore-artifacts step", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_LatestCandidateTemplate_IsValid()
    {
        var yaml = DeliveryTemplateCatalog.ReadResource("application-candidate-v1.yaml");

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.True(result.IsValid,
            string.Join(System.Environment.NewLine, result.Errors.Concat(result.Warnings)));
    }

    [Theory]
    [InlineData("toto-ci-light.yaml")]
    [InlineData("toto-ci-light-adapter.yaml")]
    [InlineData("toto-ci.yaml")]
    [InlineData("toto-quality.yaml")]
    [InlineData("toto-security.yaml")]
    [InlineData("toto-qa.yaml")]
    [InlineData("toto-candidate.yaml")]
    [InlineData("toto-baseline-candidate.yaml")]
    [InlineData("toto-promote-accept.yaml")]
    [InlineData("toto-deploy-accept-adapter.yaml")]
    [InlineData("toto-verify-accept.yaml")]
    [InlineData("toto-conformance.yaml")]
    [InlineData("toto-conformance-cancellable.yaml")]
    [InlineData("toto-conformance-injection.yaml")]
    [InlineData("toto-conformance-negative.yaml")]
    [InlineData("toto-conformance-stale.yaml")]
    [InlineData("toto-vulnerable-conformance.yaml")]
    public void ValidateYamlStrict_TotoConformanceDefinitions_AreValid(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var yaml = File.ReadAllText(Path.Combine(directory.FullName, "deploy", "pipelines", fileName));

        var result = _sut.ValidateYamlStrict(ResolveSeededDeliveryTemplate(yaml));

        Assert.True(result.IsValid,
            string.Join(System.Environment.NewLine, result.Errors.Concat(result.Warnings)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    public void ValidateYamlStrict_RestoreArtifactsUnsafeTarget_IsRejected(string targetDirectory)
    {
        var yaml = $$"""
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux
                steps:
                  - name: restore previous
                    type: restore-artifacts
                    release: latest-published
                    target_directory: {{targetDirectory}}
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("safe relative path", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYamlStrict_EmptyStageName_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: ""
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Stage name"));
    }

    [Fact]
    public void ValidateYamlStrict_EmptyStepName_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: ""
                    shell: dotnet build
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Step name"));
    }

    [Fact]
    public void ValidateYamlStrict_StageWithNoSteps_ReturnsError()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps: []
            """;

        var result = _sut.ValidateYamlStrict(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("at least one step"));
    }

    private string ResolveSeededDeliveryTemplate(string yaml)
    {
        var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
        if (string.IsNullOrWhiteSpace(definition.Extends))
            return yaml;

        var reference = definition.Extends.Split('@', 2, StringSplitOptions.TrimEntries);
        Assert.Equal(2, reference.Length);
        Assert.True(int.TryParse(reference[1], out var version));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var templateYaml = File.ReadAllText(Path.Combine(
            directory.FullName,
            "deploy",
            "pipeline-templates",
            $"{reference[0]}-v{version}.yaml"));
        _repoMock.FindTemplateByNameAsync(reference[0], 0, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplate
            {
                Id = 999,
                Name = reference[0],
                OrganizationId = 0,
                LatestVersion = version,
                Versions =
                [
                    new PipelineTemplateVersion
                    {
                        Version = version,
                        YamlContent = templateYaml
                    }
                ]
            });
        return new PipelineTemplateResolver(_repoMock)
            .ResolveAsync(yaml, 0)
            .GetAwaiter()
            .GetResult()
            .Yaml;
    }
}
