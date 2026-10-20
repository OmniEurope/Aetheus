// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AiTasks;

/// <summary>
/// A manual or scheduled project AI task sends repository content to the model the same way a
/// pipeline <c>type: ai</c> step does: its context paths and git range are validated by the
/// <c>context_paths</c> / <c>git_range</c> rule, stored, and handed to the agent through
/// <c>AETHEUS_AI_CONTEXT_PATHS_JSON</c> and <c>AETHEUS_AI_GIT_RANGE</c>.
/// </summary>
public sealed class AiTaskRepositoryContextTests
{
    private readonly IAiTaskRepository _repo = Substitute.For<IAiTaskRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly ISecretMaskingService _masking = Substitute.For<ISecretMaskingService>();
    private readonly AiTaskService _sut;

    public AiTaskRepositoryContextTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _masking.MaskAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));
        _repo.FindProfileAsync(3, Arg.Any<CancellationToken>()).Returns(Profile());
        _repo.GetProjectOrganizationIdAsync(7, Arg.Any<CancellationToken>()).Returns(1);
        _repo.GetServerOrganizationIdAsync(9, Arg.Any<CancellationToken>()).Returns(1);
        _sut = new AiTaskService(
            _repo,
            Substitute.For<IAuditService>(),
            _encryption,
            _masking,
            Substitute.For<ITaskQueueNotifier>(),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero)),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["GitLight:RunTokenKey"] = "ai-task-test-signing-key" }).Build(),
            NullLogger<AiTaskService>.Instance);
    }

    [Fact]
    public async Task RunNow_ProjectTaskWithRepositoryContext_SetsTheAgentVariables()
    {
        var definition = new AiTaskDefinition
        {
            Id = 11,
            Name = "Quick check",
            ProjectId = 7,
            Profile = Profile(),
            PromptTemplate = "Check the repository",
            ContextPathsJson = """["AGENTS.md","README.md"]""",
            GitRange = "HEAD~10..HEAD"
        };
        var environment = await RunAsync(definition);

        Assert.Equal(["AGENTS.md", "README.md"],
            JsonSerializer.Deserialize<List<string>>(environment["AETHEUS_AI_CONTEXT_PATHS_JSON"]));
        Assert.Equal("HEAD~10..HEAD", environment["AETHEUS_AI_GIT_RANGE"]);
    }

    [Fact]
    public async Task RunNow_ProjectTaskWithoutRepositoryContext_SetsNoContextVariable()
    {
        var environment = await RunAsync(new AiTaskDefinition
        {
            Id = 11,
            Name = "Review",
            ProjectId = 7,
            Profile = Profile(),
            PromptTemplate = "Review"
        });

        Assert.False(environment.ContainsKey("AETHEUS_AI_CONTEXT_PATHS_JSON"));
        Assert.False(environment.ContainsKey("AETHEUS_AI_GIT_RANGE"));
    }

    [Fact]
    public async Task Create_ValidContext_IsTrimmedStoredAndMapped()
    {
        AiTaskDefinition? stored = null;
        _repo.AddDefinitionAsync(Arg.Do<AiTaskDefinition>(definition => { stored = definition; definition.Id = 51; }),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.FindDefinitionAsync(51, Arg.Any<CancellationToken>()).Returns(_ => stored);

        var result = await _sut.CreateDefinitionAsync(Request() with
        {
            ContextPaths = [" AGENTS.md ", "", "README.md", "AGENTS.md", "docs/"],
            GitRange = " HEAD~10..HEAD "
        }, TestContext.Current.CancellationToken);

        Assert.Equal("""["AGENTS.md","README.md","docs/"]""", stored!.ContextPathsJson);
        Assert.Equal("HEAD~10..HEAD", stored.GitRange);
        Assert.Equal(["AGENTS.md", "README.md", "docs/"], result.ContextPaths);
        Assert.Equal("HEAD~10..HEAD", result.GitRange);
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("docs/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("./README.md")]
    public async Task Create_UnsafeContextPath_IsRejected(string path)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateDefinitionAsync(
            Request() with { ContextPaths = [path] }, TestContext.Current.CancellationToken));
        await _repo.DidNotReceiveWithAnyArgs().AddDefinitionAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Create_MoreThanThirtyTwoContextPaths_IsRejected()
    {
        var paths = Enumerable.Range(1, 33).Select(index => $"file{index}.md").ToList();

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateDefinitionAsync(
            Request() with { ContextPaths = paths }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("HEAD~10")]
    [InlineData("--output=/tmp/x..HEAD")]
    [InlineData("HEAD~10...HEAD")]
    [InlineData("HEAD;rm -rf ..HEAD")]
    [InlineData("a..b..c")]
    public async Task Create_InvalidGitRange_IsRejected(string range)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateDefinitionAsync(
            Request() with { GitRange = range }, TestContext.Current.CancellationToken));
        await _repo.DidNotReceiveWithAnyArgs().AddDefinitionAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Create_ServerTaskWithRepositoryContext_IsRejected()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateDefinitionAsync(
            Request() with { ProjectId = null, ServerId = 9, ContextPaths = ["AGENTS.md"] },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_BlankContext_ClearsTheStoredContext()
    {
        var definition = new AiTaskDefinition
        {
            Id = 11,
            Name = "Quick check",
            ProjectId = 7,
            ProfileId = 3,
            Profile = Profile(),
            ContextPathsJson = """["AGENTS.md"]""",
            GitRange = "HEAD~10..HEAD"
        };
        _repo.FindDefinitionAsync(11, Arg.Any<CancellationToken>()).Returns(definition);

        await _sut.UpdateDefinitionAsync(11, new UpdateAiTaskDefinitionRequest
        {
            Name = "Quick check",
            ProfileId = 3,
            PromptTemplate = "Check",
            ProjectId = 7,
            ContextPaths = [" "],
            GitRange = "  "
        }, TestContext.Current.CancellationToken);

        Assert.Null(definition.ContextPathsJson);
        Assert.Null(definition.GitRange);
    }

    private async Task<Dictionary<string, string>> RunAsync(AiTaskDefinition definition)
    {
        _repo.FindDefinitionAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        _repo.ResolveExecutionServerAsync(definition, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9, Name = "agent" });
        _repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(0);
        _repo.GetPrimaryProjectRepositoryAsync(7, Arg.Any<CancellationToken>())
            .Returns(new GitInternalRepo { Id = 13, ProjectId = 7, Slug = "core", DefaultBranch = "develop" });
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<ServerTask>());

        var task = await _sut.RunNowAsync(definition.Id, eventPayload: null, TestContext.Current.CancellationToken);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables)!;
    }

    private static CreateAiTaskDefinitionRequest Request() => new()
    {
        Name = "Quick check",
        ProfileId = 3,
        PromptTemplate = "Check the repository",
        ProjectId = 7
    };

    private static AiRunnerProfile Profile() => new()
    {
        Id = 3,
        OrganizationId = 1,
        Name = "reviewer",
        Provider = "anthropic",
        Model = "claude-sonnet-5-5",
        ApiKeyEncrypted = "sk-key",
        TimeoutSeconds = 90,
        MaxOutputBytes = 4096
    };
}
