// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AiTasks;

public sealed class AiTaskServiceTests
{
    private readonly IAiTaskRepository _repo = Substitute.For<IAiTaskRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly ISecretMaskingService _masking = Substitute.For<ISecretMaskingService>();
    private readonly ITaskQueueNotifier _notifier = Substitute.For<ITaskQueueNotifier>();
    private readonly FakeTimeProvider _time = new(
        new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly AiTaskService _sut;

    public AiTaskServiceTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _masking.MaskAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));
        _sut = new AiTaskService(
            _repo,
            _audit,
            _encryption,
            _masking,
            _notifier,
            _time,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["GitLight:RunTokenKey"] = "ai-task-test-signing-key"
                }).Build(),
            NullLogger<AiTaskService>.Instance);
    }

    [Fact]
    public async Task GetDefinitionsAsync_PropagatesRequestedPageAndTotalBeyondOneHundred()
    {
        _repo.GetDefinitionsPageAsync(
                "security", 5, 25, 7, null, Arg.Any<List<int>?>(), Arg.Any<List<int>?>(),
                Arg.Any<CancellationToken>())
            .Returns(([], 101));

        var result = await _sut.GetDefinitionsAsync(
            new PaginationRequest { Page = 5, PageSize = 25, Search = "security" },
            projectId: 7,
            serverId: null,
            accessibleProjectIds: [7],
            accessibleServerIds: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Page);
        Assert.Equal(25, result.PageSize);
        Assert.Equal(101, result.TotalCount);
    }

    [Fact]
    public async Task CreateDefinitionAsync_ValidOwner_NormalizesScheduleAndTriggers()
    {
        var profile = Profile();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);
        _repo.GetProjectOrganizationIdAsync(12, Arg.Any<CancellationToken>())
            .Returns(profile.OrganizationId);
        _repo.AddDefinitionAsync(
                Arg.Any<AiTaskDefinition>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<AiTaskDefinition>().Id = 51;
                return Task.CompletedTask;
            });
        _repo.FindDefinitionAsync(51, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                return _repo.ReceivedCalls()
                    .Where(candidate =>
                        candidate.GetMethodInfo().Name == nameof(IAiTaskRepository.AddDefinitionAsync))
                    .Select(candidate => (AiTaskDefinition)candidate.GetArguments()[0]!)
                    .Single();
            });

        var result = await _sut.CreateDefinitionAsync(
            new CreateAiTaskDefinitionRequest
            {
                Name = "  Security review  ",
                ProfileId = profile.Id,
                PromptTemplate = "Review this repository",
                ProjectId = 12,
                Schedule = " 0 0 6 * * * ",
                EventTypes = ["git.push", "git.push", "pipeline.failed"],
                Enabled = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(51, result.Id);
        Assert.Equal("Security review", result.Name);
        Assert.Equal("0 0 6 * * *", result.Schedule);
        Assert.Equal(["git.push", "pipeline.failed"], result.EventTypes);
        await _audit.Received(1).LogAsync(
            "Created",
            "AiTaskDefinition",
            51,
            "Security review",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DefinitionQueriesAndUpdate_MapOwnersAndReplaceTriggers()
    {
        var profile = Profile();
        var definition = Definition(profile);
        definition.Project = new Project { Id = 12, Name = "Aetheus" };
        definition.Triggers =
        [
            new AiTaskTrigger { EventType = "git.push" }
        ];
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>())
            .Returns(definition);
        _repo.GetDefinitionsPageAsync(
                null,
                Arg.Any<int>(),
                Arg.Any<int>(),
                12,
                null,
                Arg.Any<List<int>?>(),
                Arg.Any<List<int>?>(),
                Arg.Any<CancellationToken>())
            .Returns(([definition], 1));
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);
        _repo.GetServerOrganizationIdAsync(8, Arg.Any<CancellationToken>())
            .Returns(profile.OrganizationId);

        var single = await _sut.GetDefinitionAsync(
            definition.Id,
            TestContext.Current.CancellationToken);
        var page = await _sut.GetDefinitionsAsync(
            new PaginationRequest(),
            12,
            null,
            [12],
            [8],
            TestContext.Current.CancellationToken);
        var updated = await _sut.UpdateDefinitionAsync(
            definition.Id,
            new UpdateAiTaskDefinitionRequest
            {
                Name = "Server review",
                ProfileId = profile.Id,
                PromptTemplate = "Inspect the server",
                ServerId = 8,
                Schedule = null,
                EventTypes = ["server.alert", "server.alert"]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("Aetheus", single!.ProjectName);
        Assert.Single(page.Items);
        Assert.NotNull(updated);
        Assert.Equal(8, updated.ServerId);
        Assert.Null(updated.ProjectId);
        Assert.Equal(["server.alert"], updated.EventTypes);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateDefinitionAsync_MissingDefinition_ReturnsNull()
    {
        var profile = Profile();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);
        _repo.GetProjectOrganizationIdAsync(12, Arg.Any<CancellationToken>())
            .Returns(profile.OrganizationId);
        _repo.FindDefinitionAsync(404, Arg.Any<CancellationToken>())
            .Returns((AiTaskDefinition?)null);

        Assert.Null(await _sut.UpdateDefinitionAsync(
            404,
            ValidUpdateDefinition(profile.Id),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, null, "Exactly one owner")]
    [InlineData(12, 8, "Exactly one owner")]
    [InlineData(12, null, "same organization")]
    public async Task CreateDefinitionAsync_InvalidOwnership_IsRejected(
        int? projectId,
        int? serverId,
        string expected)
    {
        var profile = Profile();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);
        _repo.GetProjectOrganizationIdAsync(12, Arg.Any<CancellationToken>())
            .Returns(999);

        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateDefinitionAsync(
                new CreateAiTaskDefinitionRequest
                {
                    Name = "review",
                    ProfileId = profile.Id,
                    PromptTemplate = "prompt",
                    ProjectId = projectId,
                    ServerId = serverId
                },
                TestContext.Current.CancellationToken));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateDefinitionAsync_MissingProfileInvalidScheduleAndEvent_AreRejected()
    {
        var request = new CreateAiTaskDefinitionRequest
        {
            Name = "review",
            ProfileId = 3,
            PromptTemplate = "prompt",
            ProjectId = 12
        };
        _repo.FindProfileAsync(3, Arg.Any<CancellationToken>())
            .Returns((AiRunnerProfile?)null);
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateDefinitionAsync(
            request,
            TestContext.Current.CancellationToken));

        var profile = Profile();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);
        _repo.GetProjectOrganizationIdAsync(12, Arg.Any<CancellationToken>())
            .Returns(profile.OrganizationId);
        Assert.Throws<Cronos.CronFormatException>(() =>
            AiTaskService.ParseSchedule("not a cron"));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateDefinitionAsync(
            request with
            {
                ProfileId = profile.Id,
                EventTypes = ["INVALID EVENT!"]
            },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteDefinitionAsync_RemovesAndAuditsExistingOnly()
    {
        var definition = Definition(Profile());
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>())
            .Returns(definition);
        _repo.FindDefinitionAsync(404, Arg.Any<CancellationToken>())
            .Returns((AiTaskDefinition?)null);

        Assert.True(await _sut.DeleteDefinitionAsync(
            definition.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await _sut.DeleteDefinitionAsync(
            404,
            TestContext.Current.CancellationToken));

        await _repo.Received(1).RemoveDefinitionAsync(
            definition,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunNowAsync_ProjectOwnedTask_QueuesAiRunWithImmutableSourceProvenance()
    {
        var profile = new AiRunnerProfile
        {
            Id = 3,
            Name = "reviewer",
            Provider = "anthropic",
            Model = "claude-sonnet-5-5",
            ApiKeyEncrypted = "sk-key",
            TimeoutSeconds = 90,
            MaxOutputBytes = 4096
        };
        var definition = new AiTaskDefinition
        {
            Id = 11,
            Name = "Review",
            ProjectId = 7,
            Profile = profile,
            PromptTemplate = "Review the repository"
        };
        _repo.FindDefinitionAsync(11, Arg.Any<CancellationToken>()).Returns(definition);
        _repo.ResolveExecutionServerAsync(definition, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9, Name = "agent" });
        _repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(0);
        _repo.GetPrimaryProjectRepositoryAsync(7, Arg.Any<CancellationToken>())
            .Returns(new GitInternalRepo
            {
                Id = 13,
                ProjectId = 7,
                Slug = "core",
                DefaultBranch = "develop",
                IsEmpty = false
            });
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var task = call.Arg<ServerTask>();
                task.Id = 17;
                return task;
            });

        var task = await _sut.RunNowAsync(
            11,
            eventPayload: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationKind.AiRun, task.Operation);
        Assert.Equal(9, task.ServerId);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
            task.EnvironmentVariables)!;
        Assert.Equal("13", environment["AETHEUS_AI_SOURCE_REPOSITORY_ID"]);
        Assert.Equal("/git/7/core.git", environment["AETHEUS_AI_SOURCE_REPOSITORY_PATH"]);
        Assert.Equal("develop", environment["AETHEUS_AI_SOURCE_REF"]);
        Assert.StartsWith("aetheus-ai-11", environment["GIT_USERNAME"], StringComparison.Ordinal);
        Assert.Equal(
            11,
            GitAiCloneToken.Validate(
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["GitLight:RunTokenKey"] = "ai-task-test-signing-key"
                    }).Build(),
                environment["GIT_USERNAME"],
                environment["GIT_PASSWORD"],
                7,
                _time.GetUtcNow().UtcDateTime));
        await _notifier.Received(1).NotifyTaskQueuedAsync(
            task,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunNowAsync_EventPayloadAndServerOwner_QueuesMaskedExecutionSpec()
    {
        var profile = Profile();
        var definition = Definition(profile);
        definition.ProjectId = null;
        definition.ServerId = 9;
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>())
            .Returns(definition);
        _repo.ResolveExecutionServerAsync(definition, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9, Name = "agent" });
        _repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(1);
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<ServerTask>());
        _masking.MaskAsync(
                Arg.Any<string>(),
                null,
                Arg.Any<CancellationToken>())
            .Returns("masked prompt");

        var task = await _sut.RunNowAsync(
            definition.Id,
            """{"severity":"high"}""",
            TestContext.Current.CancellationToken);

        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
            task.EnvironmentVariables)!;
        Assert.Equal("masked prompt", environment["AETHEUS_AI_PROMPT"]);
        Assert.Equal(definition.Id.ToString(), environment["AETHEUS_AI_TASK_DEFINITION_ID"]);
        Assert.Equal("false", environment["AETHEUS_AI_GATE"]);
        Assert.False(environment.ContainsKey("AETHEUS_AI_SOURCE_REPOSITORY_ID"));
    }

    [Fact]
    public async Task RunNowAsync_MissingDefinitionServerAndCapacity_AreRejected()
    {
        _repo.FindDefinitionAsync(404, Arg.Any<CancellationToken>())
            .Returns((AiTaskDefinition?)null);
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.RunNowAsync(
            404,
            null,
            TestContext.Current.CancellationToken));

        var definition = Definition(Profile());
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>())
            .Returns(definition);
        _repo.ResolveExecutionServerAsync(definition, Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.RunNowAsync(
            definition.Id,
            null,
            TestContext.Current.CancellationToken));

        _repo.ResolveExecutionServerAsync(definition, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9 });
        _repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(2);
        await Assert.ThrowsAsync<ConflictException>(() => _sut.RunNowAsync(
            definition.Id,
            null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunNowAsync_ProjectWithoutRepository_IsRejected()
    {
        var definition = Definition(Profile());
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>())
            .Returns(definition);
        _repo.ResolveExecutionServerAsync(definition, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9 });
        _repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(0);
        _repo.GetPrimaryProjectRepositoryAsync(
                definition.ProjectId!.Value,
                Arg.Any<CancellationToken>())
            .Returns((GitInternalRepo?)null);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.RunNowAsync(
            definition.Id,
            null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildPipelineExecutionAsync_MapsProfileAndPipelineContext()
    {
        var profile = Profile();
        OrganizationProfiles(profile.OrganizationId, profile);

        var execution = await _sut.BuildPipelineExecutionAsync(
            profile.Name,
            null,
            profile.OrganizationId,
            "review",
            gate: true,
            pipelineRunId: 71,
            workingDirectory: "/workspace",
            ct: TestContext.Current.CancellationToken);

        var spec = execution.Spec;
        Assert.NotNull(spec);
        Assert.Null(execution.Problem);
        // The step named it, so there is nothing to flag; the agent still writes why at the top of the log.
        Assert.False(execution.IsFallback);
        Assert.Equal("Using AI runner profile 'reviewer' named by the step.", execution.Resolution);
        Assert.Equal(execution.Resolution, spec.EnvironmentVariables["AETHEUS_AI_PROFILE_RESOLUTION"]);
        Assert.Equal("ai-run", spec.Target);
        Assert.Equal(profile.TimeoutSeconds, spec.TimeoutSeconds);
        Assert.Equal("true", spec.EnvironmentVariables["AETHEUS_AI_GATE"]);
        Assert.Equal("/workspace", spec.EnvironmentVariables["AETHEUS_AI_WORKING_DIR"]);
        Assert.Equal("anthropic", spec.EnvironmentVariables["AETHEUS_AI_PROVIDER"]);
        Assert.Equal("https://api.anthropic.com", spec.EnvironmentVariables["AETHEUS_AI_BASE_URL"]);
        Assert.Equal("claude-sonnet-5-5", spec.EnvironmentVariables["AETHEUS_AI_MODEL"]);
        Assert.Equal("sk-key", spec.EnvironmentVariables["AETHEUS_AI_API_KEY"]);
        Assert.Equal("low", spec.EnvironmentVariables["AETHEUS_AI_EFFORT"]);
        Assert.Equal("8000", spec.EnvironmentVariables["AETHEUS_AI_MAX_OUTPUT_TOKENS"]);
        Assert.False(spec.EnvironmentVariables.ContainsKey("AETHEUS_AI_TASK_DEFINITION_ID"));
        Assert.False(spec.EnvironmentVariables.ContainsKey("AETHEUS_AI_BINARY"));
        // The key is a secret of the run: everything the run shows a person is masked.
        _masking.Received(1).RegisterRuntimeSecret(71, "sk-key");
    }

    [Fact]
    public async Task BuildPipelineExecutionAsync_CliProfile_IsRefusedAsToReconfigure()
    {
        var profile = Profile();
        profile.Provider = string.Empty;
        profile.Binary = "claude";
        OrganizationProfiles(profile.OrganizationId, profile);

        var execution = await _sut.BuildPipelineExecutionAsync(
            profile.Name, null, profile.OrganizationId, "review", gate: false, pipelineRunId: 71,
            workingDirectory: "/workspace", ct: TestContext.Current.CancellationToken);

        Assert.Null(execution.Spec);
        Assert.Contains("must be reconfigured", execution.Problem, StringComparison.Ordinal);
        _masking.DidNotReceiveWithAnyArgs().RegisterRuntimeSecret(default, default!);
    }

    [Fact]
    public async Task RunNowAsync_CliProfile_IsRefusedBeforeAnythingIsQueued()
    {
        var profile = Profile();
        profile.Model = string.Empty;
        var definition = Definition(profile);
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.RunNowAsync(
            definition.Id, null, TestContext.Current.CancellationToken));

        Assert.Contains("must be reconfigured", error.Message, StringComparison.Ordinal);
        await _repo.DidNotReceiveWithAnyArgs().AddTaskAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BuildPipelineExecutionAsync_NoProfileInTheOrganization_ReturnsNoSpec_AndSaysSo()
    {
        OrganizationProfiles(7);

        var execution = await _sut.BuildPipelineExecutionAsync(
            "missing",
            null,
            7,
            "prompt",
            gate: false,
            pipelineRunId: 71,
            workingDirectory: "/workspace",
            ct: TestContext.Current.CancellationToken);

        Assert.Null(execution.Spec);
        Assert.Contains("Profile 'missing' named by the step was not found", execution.Problem, StringComparison.Ordinal);
        Assert.Contains("The pipeline's organization has no AI runner profile.", execution.Problem, StringComparison.Ordinal);
        _masking.DidNotReceiveWithAnyArgs().RegisterRuntimeSecret(default, default!);
    }

    [Fact]
    public async Task BuildPipelineExecutionAsync_AMissingName_FallsBackOnThePipelineProfile_AndSaysWhy()
    {
        var other = Profile();
        other.Id = 4;
        other.Name = "Claude";
        other.Priority = 50;
        var preferred = Profile();
        preferred.Priority = 10;
        OrganizationProfiles(7, preferred, other);

        var execution = await _sut.BuildPipelineExecutionAsync(
            "claude-sonnet", other.Id, 7, "prompt", gate: false, pipelineRunId: 71,
            workingDirectory: "/workspace", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(execution.Spec);
        Assert.True(execution.IsFallback);
        Assert.Equal(
            "Profile 'claude-sonnet' named by the step was not found in the organization. Using the pipeline's AI runner profile 'Claude'.",
            execution.Resolution);
        Assert.Equal("Claude", execution.Spec.EnvironmentVariables["AETHEUS_AI_PROFILE_NAME"]);
    }

    [Fact]
    public async Task FindPipelineProfileProblemAsync_IsNullWhenAnotherProfileCanRun_AndNamesTheReasonsOtherwise()
    {
        var complete = Profile();
        var keyless = Profile();
        keyless.Id = 4;
        keyless.Name = "keyless";
        keyless.ApiKeyEncrypted = string.Empty;
        OrganizationProfiles(7, complete, keyless);
        OrganizationProfiles(8, keyless);

        var fallbackProblem = await _sut.FindPipelineProfileProblemAsync("claude-sonnet", 7, TestContext.Current.CancellationToken);
        var noneProblem = await _sut.FindPipelineProfileProblemAsync("keyless", 8, TestContext.Current.CancellationToken);

        Assert.Null(fallbackProblem);
        Assert.Contains("'keyless' must be reconfigured", noneProblem, StringComparison.Ordinal);
        // A read for the launch: the key is never decrypted nor registered as a run secret here.
        _masking.DidNotReceiveWithAnyArgs().RegisterRuntimeSecret(default, default!);
    }

    [Fact]
    public async Task PublishResultAsync_RejectsRepositoryDifferentFromClaimedWorkspace()
    {
        _repo.FindTaskAsync(17, Arg.Any<CancellationToken>()).Returns(new ServerTask
        {
            Id = 17,
            Operation = OperationKind.AiRun,
            EnvironmentVariables = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["AETHEUS_AI_SOURCE_REPOSITORY_ID"] = "13"
            })
        });

        var action = () => _sut.PublishResultAsync(
            new PublishAiRunResultRequest
            {
                ServerTaskId = 17,
                ProfileName = "reviewer",
                ReportMarkdown = "report",
                SourceRepositoryId = 14
            },
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<BadRequestException>(action);
        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
        await _repo.DidNotReceive().AddResultAsync(
            Arg.Any<AiRunResult>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishResultAsync_OfAProjectTask_NotifiesItsProject_WithTheRunToOpen()
    {
        var notifications = Substitute.For<Aetheus.Back.Components.Notifications.IUserNotificationService>();
        var sut = new AiTaskService(_repo, _audit, _encryption, _masking, _notifier, _time,
            new ConfigurationBuilder().Build(), NullLogger<AiTaskService>.Instance, notifications);
        _repo.FindTaskAsync(17, Arg.Any<CancellationToken>()).Returns(new ServerTask
        {
            Id = 17,
            Operation = OperationKind.AiRun,
            EnvironmentVariables = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["AETHEUS_AI_TASK_DEFINITION_ID"] = "5"
            })
        });
        _repo.FindDefinitionAsync(5, Arg.Any<CancellationToken>())
            .Returns(new AiTaskDefinition { Id = 5, Name = "nightly review", ProjectId = 12 });
        _repo.AddResultAsync(Arg.Do<AiRunResult>(stored => stored.Id = 300), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await sut.PublishResultAsync(
            new PublishAiRunResultRequest { ServerTaskId = 17, ProfileName = "reviewer", ReportMarkdown = "report", Succeeded = true },
            TestContext.Current.CancellationToken);

        await notifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.AiTaskCompleted,
            Arg.Is<string>(payload => payload.Contains("\"ProjectId\":12", StringComparison.Ordinal)
                && payload.Contains("\"AiTaskDefinitionId\":5", StringComparison.Ordinal)
                && payload.Contains("\"AiRunResultId\":300", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishResultAsync_IdempotentExistingResult_ReturnsMappedDto()
    {
        var existing = Result();
        _repo.FindResultByTaskAsync(
                existing.ServerTaskId,
                Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _sut.PublishResultAsync(
            PublishRequest(existing.ServerTaskId),
            TestContext.Current.CancellationToken);

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal(existing.ReportMarkdown, result.ReportMarkdown);
        await _repo.DidNotReceive().AddResultAsync(
            Arg.Any<AiRunResult>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishResultAsync_ValidTask_MasksTheReportAndTheTaskApiKey()
    {
        var task = new ServerTask
        {
            Id = 17,
            PipelineRunId = 71,
            Operation = OperationKind.AiRun,
            EnvironmentVariables = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["AETHEUS_AI_TASK_DEFINITION_ID"] = "11",
                ["AETHEUS_AI_SOURCE_REPOSITORY_ID"] = "13",
                ["AETHEUS_AI_API_KEY"] = "sk-task-key"
            })
        };
        _repo.FindTaskAsync(task.Id, Arg.Any<CancellationToken>()).Returns(task);
        _masking.MaskAsync("report secret sk-task-key", 71, Arg.Any<CancellationToken>())
            .Returns("report *** sk-task-key");
        _repo.AddResultAsync(Arg.Any<AiRunResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<AiRunResult>().Id = 88;
                return Task.CompletedTask;
            });

        var result = await _sut.PublishResultAsync(
            PublishRequest(task.Id) with
            {
                ReportMarkdown = "report secret sk-task-key",
                SourceRepositoryId = 13,
                BaseCommitSha = new string('a', 40),
                Verdict = AiVerdict.Pass,
                DurationMs = 1234,
                Succeeded = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(88, result.Id);
        Assert.Equal(11, result.AiTaskDefinitionId);
        Assert.Equal("report *** ***", result.ReportMarkdown);
        Assert.Null(result.DiffPatch);
        Assert.Equal(new string('a', 40), result.BaseCommitSha);
    }

    [Fact]
    public async Task PublishResultAsync_MissingWrongTaskAndInvalidCommit_AreRejected()
    {
        _repo.FindTaskAsync(17, Arg.Any<CancellationToken>())
            .Returns((ServerTask?)null);
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.PublishResultAsync(
            PublishRequest(17),
            TestContext.Current.CancellationToken));

        _repo.FindTaskAsync(17, Arg.Any<CancellationToken>()).Returns(new ServerTask
        {
            Id = 17,
            Operation = OperationKind.None,
            EnvironmentVariables = "{}"
        });
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PublishResultAsync(
            PublishRequest(17),
            TestContext.Current.CancellationToken));

        _repo.FindTaskAsync(17, Arg.Any<CancellationToken>()).Returns(new ServerTask
        {
            Id = 17,
            Operation = OperationKind.AiRun,
            EnvironmentVariables = "{}"
        });
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PublishResultAsync(
            PublishRequest(17) with { BaseCommitSha = new string('z', 40) },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResultQueries_MapPaginationAndProjectFallback()
    {
        var result = Result();
        result.AiTaskDefinition = Definition(Profile());
        _repo.GetResultsPageAsync(
                11,
                null,
                3,
                15,
                Arg.Any<CancellationToken>())
            .Returns(([result], 31));
        _repo.FindResultAsync(result.Id, Arg.Any<CancellationToken>()).Returns(result);
        _repo.FindResultAsync(404, Arg.Any<CancellationToken>())
            .Returns((AiRunResult?)null);

        var page = await _sut.GetResultsAsync(
            11,
            null,
            new PaginationRequest { Page = 3, PageSize = 15 },
            TestContext.Current.CancellationToken);
        var single = await _sut.GetResultAsync(
            result.Id,
            TestContext.Current.CancellationToken);
        var missing = await _sut.GetResultAsync(
            404,
            TestContext.Current.CancellationToken);

        Assert.Equal(31, page.TotalCount);
        Assert.Equal(result.AiTaskDefinition.ProjectId, single!.ProjectId);
        Assert.Null(missing);
    }

    [Fact]
    public async Task GetConsumptionAsync_AggregatesDatabaseProjectionWithoutLoadingReports()
    {
        _repo.GetConsumptionByProfileAsync(
                _time.GetUtcNow().UtcDateTime.AddDays(-7),
                7,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new AiProfileConsumptionDto
                {
                    ProfileName = "reviewer",
                    RunCount = 3,
                    FailedCount = 1,
                    DurationMs = 1200
                },
                new AiProfileConsumptionDto
                {
                    ProfileName = "planner",
                    RunCount = 2,
                    FailedCount = 0,
                    DurationMs = 800
                }
            ]);

        var result = await _sut.GetConsumptionAsync(
            7,
            TestContext.Current.CancellationToken);

        Assert.Equal(5, result.RunCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(2000, result.DurationMs);
        Assert.Equal(2, result.Profiles.Count);
    }

    [Fact]
    public async Task DispatchEventAsync_QueuesEligibleDefinitionAndAdvancesCooldown()
    {
        var profile = Profile();
        var definition = Definition(profile);
        definition.ProjectId = null;
        definition.ServerId = 9;
        var trigger = new AiTaskTrigger { EventType = "server.alert" };
        definition.Triggers = [trigger];
        _repo.GetDefinitionsForEventAsync(
                "server.alert",
                null,
                9,
                Arg.Any<CancellationToken>())
            .Returns([definition]);
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>())
            .Returns(definition);
        _repo.ResolveExecutionServerAsync(definition, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9 });
        _repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(0);
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<ServerTask>());

        await _sut.DispatchEventAsync(
            "server.alert",
            new { severity = "high", ServerId = 9 },
            TestContext.Current.CancellationToken);

        Assert.Equal(_time.GetUtcNow().UtcDateTime, trigger.LastTriggeredAt);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchEventAsync_SelectsDefinitionsByTheEventOwner()
    {
        _repo.GetDefinitionsForEventAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.DispatchEventAsync(
            "alert.triggered",
            new { RuleName = "cpu", ServerId = 41, Threshold = 90 },
            TestContext.Current.CancellationToken);

        await _repo.Received(1).GetDefinitionsForEventAsync(
            "alert.triggered", null, 41, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchEventAsync_EventWithoutOwner_StartsNothing()
    {
        await _sut.DispatchEventAsync(
            "SecretExpiring",
            new { SecretId = 3, DaysRemaining = 2 },
            TestContext.Current.CancellationToken);

        await _repo.DidNotReceiveWithAnyArgs().GetDefinitionsForEventAsync(default!, default, default, TestContext.Current.CancellationToken);
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchEventAsync_CooldownAndConcurrency_AreNonFatal()
    {
        var profile = Profile();
        var cooling = Definition(profile);
        cooling.Id = 21;
        cooling.ProjectId = null;
        cooling.ServerId = 9;
        cooling.Triggers =
        [
            new AiTaskTrigger
            {
                EventType = "server.alert",
                LastTriggeredAt = _time.GetUtcNow().UtcDateTime.AddMinutes(-1)
            }
        ];
        var saturated = Definition(profile);
        saturated.Id = 22;
        saturated.ProjectId = null;
        saturated.ServerId = 9;
        saturated.Triggers = [new AiTaskTrigger { EventType = "server.alert" }];
        _repo.GetDefinitionsForEventAsync(
                "server.alert",
                null,
                9,
                Arg.Any<CancellationToken>())
            .Returns([cooling, saturated]);
        _repo.FindDefinitionAsync(saturated.Id, Arg.Any<CancellationToken>())
            .Returns(saturated);
        _repo.ResolveExecutionServerAsync(saturated, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9 });
        _repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(2);

        await _sut.DispatchEventAsync(
            "server.alert",
            new { severity = "high", ServerId = 9 },
            TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddTaskAsync(
            Arg.Any<ServerTask>(),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PublishRequest_EmptyProfileName_IsInvalid()
    {
        var request = new PublishAiRunResultRequest
        {
            ServerTaskId = 17,
            ProfileName = string.Empty,
            ReportMarkdown = "report"
        };
        var validation = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            validation,
            validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(validation, item =>
            item.MemberNames.Contains(nameof(PublishAiRunResultRequest.ProfileName)));
    }

    // Recette R3-4: the connection test was refused (400) whenever the name was still empty, and the
    // dialog showed nothing. A test needs no name, a create without one generates it (R3-5); saving an
    // existing profile still needs one.
    [Fact]
    public void ProfileRequests_NameIsRequiredToSaveAnExistingProfile_Only()
    {
        static bool NameInvalid(AiRunnerProfileRequest request)
        {
            var validation = new List<ValidationResult>();
            Validator.TryValidateObject(request, new ValidationContext(request), validation, validateAllProperties: true);
            return validation.Any(item => item.MemberNames.Contains(nameof(AiRunnerProfileRequest.Name)));
        }

        Assert.False(NameInvalid(new TestAiRunnerProfileRequest { Provider = AiProvider.Anthropic, Model = "m" }));
        Assert.False(NameInvalid(new CreateAiRunnerProfileRequest { Provider = AiProvider.Anthropic, Model = "m" }));
        Assert.True(NameInvalid(new UpdateAiRunnerProfileRequest { Provider = AiProvider.Anthropic, Model = "m" }));
    }

    private void OrganizationProfiles(int organizationId, params AiRunnerProfile[] profiles) =>
        _repo.GetOrganizationProfilesByPriorityAsync(organizationId, Arg.Any<CancellationToken>())
            .Returns([.. profiles]);

    private static AiRunnerProfile Profile() => new()
    {
        Id = 3,
        OrganizationId = 7,
        Name = "reviewer",
        Description = "Reviews code",
        Provider = "anthropic",
        Model = "claude-sonnet-5-5",
        ApiKeyEncrypted = "sk-key",
        ReasoningEffort = "low",
        MaxOutputTokens = 8000,
        TimeoutSeconds = 90,
        MaxOutputBytes = 4096,
        SendsDataExternally = true,
        CreatedAt = new DateTime(2026, 1, 1),
        UpdatedAt = new DateTime(2026, 1, 2)
    };

    private static AiTaskDefinition Definition(AiRunnerProfile profile) => new()
    {
        Id = 11,
        Name = "Review",
        ProfileId = profile.Id,
        Profile = profile,
        PromptTemplate = "Review the repository",
        ProjectId = 12,
        Enabled = true,
        CreatedAt = new DateTime(2026, 1, 1),
        UpdatedAt = new DateTime(2026, 1, 2)
    };

    private static AiRunResult Result() => new()
    {
        Id = 88,
        ServerTaskId = 17,
        PipelineRunId = 71,
        AiTaskDefinitionId = 11,
        ProfileName = "reviewer",
        SendsDataExternally = true,
        ReportMarkdown = "report",
        Verdict = AiVerdict.Pass,
        DiffPatch = "diff --git a/file b/file",
        DurationMs = 1234,
        Succeeded = true,
        SourceRepositoryId = 13,
        BaseCommitSha = new string('a', 40),
        CreatedAt = new DateTime(2026, 1, 1)
    };

    private static UpdateAiTaskDefinitionRequest ValidUpdateDefinition(int profileId) => new()
    {
        Name = "review",
        ProfileId = profileId,
        PromptTemplate = "prompt",
        ProjectId = 12
    };

    private static PublishAiRunResultRequest PublishRequest(int taskId) => new()
    {
        ServerTaskId = taskId,
        ProfileName = "reviewer",
        ReportMarkdown = "report"
    };
}
