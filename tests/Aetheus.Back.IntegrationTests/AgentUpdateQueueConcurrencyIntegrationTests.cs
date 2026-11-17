// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// <c>TryQueueAsync</c> runs in a Serializable transaction and is reached by several callers at
/// once: the waiting-requests coordinator, a user request, another replica. Two of them on the same
/// request used to end with one of them failing with SQLSTATE 40001 (serialization failure), which
/// the coordinator loop swallowed for the whole sweep and an HTTP caller saw as a failure. This is the
/// real PostgreSQL proof that the invariant holds under that contention.
/// </summary>
public sealed class AgentUpdateQueueConcurrencyIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private const int Rounds = 25;
    private const int Contenders = 3;

    [Fact]
    public async Task ConcurrentQueueing_OfOneRequest_CreatesExactlyOneUpdateTaskAndNeverFails()
    {
        await ResetAndMigrateAsync();
        var requestIds = new List<int>(Rounds);
        for (var round = 0; round < Rounds; round++)
            requestIds.Add(await ReserveNewRequestAsync(round));

        var failures = new List<string>();
        var createdPerRequest = new Dictionary<int, int>();
        foreach (var requestId in requestIds)
        {
            var outcomes = await QueueConcurrentlyAsync(requestId);
            foreach (var outcome in outcomes.Where(item => item.Failure is not null))
                failures.Add($"request {requestId}: {outcome.Failure}");
            createdPerRequest[requestId] = outcomes.Count(item => item.Created);
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {Rounds * Contenders} concurrent calls failed:{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, failures.Take(5)));
        await using var verify = NewContext();
        foreach (var requestId in requestIds)
        {
            var request = await verify.AgentUpdateRequests.AsNoTracking()
                .SingleAsync(item => item.Id == requestId, TestContext.Current.CancellationToken);
            var updateTasks = await verify.Tasks.AsNoTracking()
                .Where(task => task.ServerId == request.ServerId
                    && task.Operation == OperationKind.AgentSelfUpdate)
                .ToListAsync(TestContext.Current.CancellationToken);

            var task = Assert.Single(updateTasks);
            Assert.Equal(AgentUpdateRequestStatus.Queued, request.Status);
            Assert.Equal(task.Id, request.TaskId);
            Assert.Equal(1, createdPerRequest[requestId]);
        }
    }

    private async Task<int> ReserveNewRequestAsync(int round)
    {
        await using var db = NewContext();
        var server = new Server
        {
            Name = $"agent-{round}",
            Hostname = $"agent-{round}",
            Organization = new Organization
            {
                Name = $"Agent update queue {round}",
                Slug = $"agent-update-queue-{Guid.NewGuid():N}",
                Description = "Agent update queue concurrency tests"
            },
            Status = ServerStatus.Online,
            AgentVersion = "0.9.0",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"agent.self-update\",\"shell.execute\"]",
            LastHeartbeat = DateTime.UtcNow
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reserved = await new AgentUpdateRepository(db, TimeProvider.System).ReserveAsync(
            server,
            new AgentReleaseManifestDto
            {
                SoftwareVersion = "1.0.0",
                ProtocolVersion = AgentProtocol.CurrentVersion,
                MinimumSupportedProtocol = AgentProtocol.MinimumSupportedVersion,
                MaximumSupportedProtocol = AgentProtocol.MaximumSupportedVersion,
                SoftwareCapabilities = [AgentCapabilities.SelfUpdate]
            },
            "admin",
            TestContext.Current.CancellationToken);
        return reserved.Request.Id;
    }

    /// <summary>Releases <see cref="Contenders"/> callers, each on its own context as in production
    /// (one scope per coordinator pass or HTTP request), at the same instant.</summary>
    private async Task<(bool Created, string? Failure)[]> QueueConcurrentlyAsync(int requestId)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(bool Created, string? Failure)> QueueAsync()
        {
            await using var db = new AppDbContext(BuildOptions());
            var repository = new AgentUpdateRepository(db, TimeProvider.System);
            await gate.Task;
            try
            {
                var result = await repository.TryQueueAsync(requestId, TestContext.Current.CancellationToken);
                return (result.CreatedTask is not null, null);
            }
            catch (Exception ex)
            {
                return (false, ex.GetBaseException().Message);
            }
        }

        var calls = Enumerable.Range(0, Contenders).Select(_ => Task.Run(QueueAsync)).ToArray();
        gate.SetResult();
        return await Task.WhenAll(calls);
    }
}
