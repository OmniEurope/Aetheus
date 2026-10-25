// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AiTasks;

/// <summary>
/// Hands an emitted event payload to the real <see cref="AiTaskService.DispatchEventAsync"/>, with the
/// real <see cref="AiTaskRepository"/> query choosing the definitions, and reports which definitions
/// started a run. Two enabled definitions listen to the event: "own" belongs to
/// <c>ownProjectId</c>, "foreign" to another project. Only the run creation itself (server, workspace
/// repository, task row) is substituted, since it does not take part in choosing the definitions.
/// </summary>
internal static class AiEventDispatchProbe
{
    public static async Task<List<string>> StartedDefinitionsAsync(
        string eventType, object payload, int ownProjectId, int foreignProjectId, CancellationToken ct)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var profile = new AiRunnerProfile
        {
            OrganizationId = 7,
            Name = "reviewer",
            Provider = "anthropic",
            Model = "claude-sonnet-5-5",
            ApiKeyEncrypted = "sk-probe-key",
            TimeoutSeconds = 90,
            MaxOutputBytes = 4096
        };
        db.AiRunnerProfiles.Add(profile);
        db.Projects.AddRange(
            new Project { Id = ownProjectId, Name = "own-project", OrganizationId = 7 },
            new Project { Id = foreignProjectId, Name = "foreign-project", OrganizationId = 7 });
        db.AiTaskDefinitions.AddRange(
            Definition("own", ownProjectId, profile, eventType),
            Definition("foreign", foreignProjectId, profile, eventType));
        await db.SaveChangesAsync(ct);

        var real = new AiTaskRepository(db);
        var repo = Substitute.For<IAiTaskRepository>();
        repo.GetDefinitionsForEventAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetDefinitionsForEventAsync(
                call.ArgAt<string>(0), call.ArgAt<int?>(1), call.ArgAt<int?>(2), call.ArgAt<CancellationToken>(3)));
        repo.FindDefinitionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.FindDefinitionAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)));
        repo.ResolveExecutionServerAsync(Arg.Any<AiTaskDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9 });
        repo.GetActiveRunCountAsync(9, Arg.Any<CancellationToken>()).Returns(0);
        repo.GetPrimaryProjectRepositoryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GitInternalRepo { Id = 13, Slug = "repo", DefaultBranch = "main" });
        var started = new List<string>();
        repo.AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var task = call.Arg<ServerTask>();
                started.Add(task.Name);
                return task;
            });

        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var masking = Substitute.For<ISecretMaskingService>();
        masking.MaskAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));
        var service = new AiTaskService(
            repo,
            Substitute.For<IAuditService>(),
            encryption,
            masking,
            Substitute.For<ITaskQueueNotifier>(),
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["GitLight:RunTokenKey"] = "ai-event-probe-signing-key" }).Build(),
            NullLogger<AiTaskService>.Instance);

        await service.DispatchEventAsync(eventType, payload, ct);
        return started;
    }

    private static AiTaskDefinition Definition(string name, int projectId, AiRunnerProfile profile, string eventType) => new()
    {
        Name = name,
        Profile = profile,
        PromptTemplate = "Explain the event",
        ProjectId = projectId,
        Enabled = true,
        Triggers = [new AiTaskTrigger { EventType = eventType }]
    };
}
