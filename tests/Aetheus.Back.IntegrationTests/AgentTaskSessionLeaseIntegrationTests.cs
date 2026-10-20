// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AgentTaskSessionLeaseIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentSessions_OnlyOneClaims_AndSupersededTokenCannotMutate()
    {
        await ResetAndMigrateAsync();
        int serverId;

        await using (var seed = NewContext())
        {
            var organization = new Organization
            {
                Name = "Lease test",
                Slug = $"lease-{Guid.NewGuid():N}"
            };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var server = new Server
            {
                Name = "lease-server",
                Hostname = "lease-host",
                OrganizationId = organization.Id,
                Status = ServerStatus.Online,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = "[\"shell.execute\"]"
            };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            serverId = server.Id;

            seed.Tasks.AddRange(Enumerable.Range(1, 3).Select(index => new ServerTask
            {
                ServerId = server.Id,
                Name = $"task-{index}",
                Command = "echo",
                Status = TaskExecutionStatus.Pending
            }));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        const string sessionA = "11111111111111111111111111111111";
        const string sessionB = "22222222222222222222222222222222";
        await using var dbA = new AppDbContext(BuildOptions());
        await using var dbB = new AppDbContext(BuildOptions());
        var repositoryA = new TaskRepository(dbA, TimeProvider.System);
        var repositoryB = new TaskRepository(dbB, TimeProvider.System);

        var claims = await Task.WhenAll(
            repositoryA.ClaimPendingTasksAsync(
                serverId, 2, sessionA, TestContext.Current.CancellationToken),
            repositoryB.ClaimPendingTasksAsync(
                serverId, 2, sessionB, TestContext.Current.CancellationToken));
        var claimA = claims[0];
        var claimB = claims[1];

        var winningTasks = claimA.Count > 0 ? claimA : claimB;
        var winningRepository = claimA.Count > 0 ? repositoryA : repositoryB;
        var winningSession = claimA.Count > 0 ? sessionA : sessionB;
        var losingRepository = claimA.Count > 0 ? repositoryB : repositoryA;
        var losingSession = claimA.Count > 0 ? sessionB : sessionA;
        Assert.Equal(2, winningTasks.Count);
        Assert.Empty(claimA.Count > 0 ? claimB : claimA);
        Assert.All(winningTasks, task => Assert.Equal(1, task.AssignedAgentSessionFencingToken));

        Assert.True(await winningRepository.TryStartTaskWithLeaseAsync(
            winningTasks[0],
            winningSession,
            1,
            DateTime.UtcNow,
            TestContext.Current.CancellationToken));

        await using (var expire = NewContext())
        {
            await expire.Servers
                .Where(server => server.Id == serverId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        server => server.AgentSessionLeaseExpiresAt,
                        DateTime.UtcNow.AddMinutes(-1)),
                    TestContext.Current.CancellationToken);
        }
        var replacementTasks = await losingRepository.ClaimPendingTasksAsync(
            serverId, 1, losingSession, TestContext.Current.CancellationToken);
        var replacementTask = Assert.Single(replacementTasks);
        Assert.Equal(2, replacementTask.AssignedAgentSessionFencingToken);

        Assert.False(await winningRepository.TryStartTaskWithLeaseAsync(
            winningTasks[1],
            winningSession,
            1,
            DateTime.UtcNow,
            TestContext.Current.CancellationToken));
        Assert.False(await winningRepository.TryCompleteTaskWithLeaseAsync(
            winningTasks[0],
            winningSession,
            1,
            TaskExecutionStatus.Success,
            0,
            DateTime.UtcNow,
            "{}",
            TestContext.Current.CancellationToken));
        Assert.False(await winningRepository.HasCurrentAgentLeaseAsync(
            winningTasks[0].Id,
            serverId,
            winningSession,
            1,
            TestContext.Current.CancellationToken));
        Assert.True(await losingRepository.HasCurrentAgentLeaseAsync(
            replacementTask.Id,
            serverId,
            losingSession,
            2,
            TestContext.Current.CancellationToken));

        await using var verification = NewContext();
        var persisted = await verification.Servers.AsNoTracking()
            .SingleAsync(server => server.Id == serverId, TestContext.Current.CancellationToken);
        Assert.Equal(losingSession, persisted.AgentSessionId);
        Assert.Equal(2, persisted.AgentSessionFencingToken);
    }

    /// <summary>Verifies on PostgreSQL that reclaiming an expired lease for the same agent session
    /// preserves its fencing token and running-task ownership.</summary>
    [Fact]
    public async Task ReclaimingALapsedLeaseWithTheSameSession_KeepsTheTokenAndTheRunningTask()
    {
        await ResetAndMigrateAsync();
        int serverId;

        await using (var seed = NewContext())
        {
            var organization = new Organization
            {
                Name = "Lapsed lease test",
                Slug = $"lapsed-{Guid.NewGuid():N}"
            };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var server = new Server
            {
                Name = "lapsed-server",
                Hostname = "lapsed-host",
                OrganizationId = organization.Id,
                Status = ServerStatus.Online,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = "[\"shell.execute\"]"
            };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            serverId = server.Id;

            seed.Tasks.AddRange(
                new ServerTask
                {
                    ServerId = server.Id,
                    Name = "Package the exact validated application",
                    Command = "build",
                    Status = TaskExecutionStatus.Pending
                },
                new ServerTask
                {
                    ServerId = server.Id,
                    Name = "queued behind the build",
                    Command = "echo",
                    Status = TaskExecutionStatus.Pending
                });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        const string session = "33333333333333333333333333333333";
        await using var db = new AppDbContext(BuildOptions());
        var repository = new TaskRepository(db, TimeProvider.System);

        var claimed = await repository.ClaimPendingTasksAsync(
            serverId, 1, session, TestContext.Current.CancellationToken);
        var buildTask = Assert.Single(claimed);
        Assert.Equal(1, buildTask.AssignedAgentSessionFencingToken);
        Assert.True(await repository.TryStartTaskWithLeaseAsync(
            buildTask, session, 1, DateTime.UtcNow, TestContext.Current.CancellationToken));

        // The build runs long enough that the poll loop misses the two-minute renewal window.
        await using (var expire = NewContext())
        {
            await expire.Servers
                .Where(server => server.Id == serverId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        server => server.AgentSessionLeaseExpiresAt,
                        DateTime.UtcNow.AddMinutes(-1)),
                    TestContext.Current.CancellationToken);
        }

        // The very same process polls again and reclaims its own lapsed lease.
        await repository.ClaimPendingTasksAsync(
            serverId, 1, session, TestContext.Current.CancellationToken);

        await using (var verification = NewContext())
        {
            var persisted = await verification.Servers.AsNoTracking()
                .SingleAsync(server => server.Id == serverId, TestContext.Current.CancellationToken);
            Assert.Equal(session, persisted.AgentSessionId);
            Assert.Equal(1, persisted.AgentSessionFencingToken);
            Assert.True(persisted.AgentSessionLeaseExpiresAt > DateTime.UtcNow);
        }

        // The running build is not orphaned work, so it never reaches the superseded sweep, its log
        // batches stay authorised and its completion is accepted instead of answered 409.
        Assert.Empty(await repository.GetTasksFromSupersededAgentSessionsAsync(
            TestContext.Current.CancellationToken));
        Assert.True(await repository.HasCurrentAgentLeaseAsync(
            buildTask.Id, serverId, session, 1, TestContext.Current.CancellationToken));
        Assert.True(await repository.TryCompleteTaskWithLeaseAsync(
            buildTask,
            session,
            1,
            TaskExecutionStatus.Success,
            0,
            DateTime.UtcNow,
            "{}",
            TestContext.Current.CancellationToken));
    }

    /// <summary>On PostgreSQL, an agent holding only the build-function capability claims the build
    /// function task and leaves the deployment function task pending: the access level of a
    /// <c>type: aetheus</c> function is enforced at claim time, not only at save time.</summary>
    [Fact]
    public async Task ADeploymentFunctionTaskIsNotClaimedWithoutTheDeploymentFunctionsCapability()
    {
        await ResetAndMigrateAsync();
        int serverId;
        int buildFunctionId;
        int deploymentFunctionId;

        await using (var seed = NewContext())
        {
            var organization = new Organization { Name = "Function claim", Slug = $"fn-{Guid.NewGuid():N}" };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var server = new Server
            {
                Name = "function-server",
                Hostname = "function-host",
                OrganizationId = organization.Id,
                Status = ServerStatus.Online,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = $"[\"{AgentCapabilities.PipelineFunctions}\"]"
            };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            serverId = server.Id;

            var deploymentFunction = new ServerTask
            {
                ServerId = server.Id,
                Name = "deployment function",
                Command = "some-deployment-function",
                Operation = OperationKind.PipelineDeploymentFunction,
                Status = TaskExecutionStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            };
            var buildFunction = new ServerTask
            {
                ServerId = server.Id,
                Name = "build function",
                Command = "detect-project-languages",
                Operation = OperationKind.PipelineFunction,
                Status = TaskExecutionStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            };
            seed.Tasks.AddRange(deploymentFunction, buildFunction);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            buildFunctionId = buildFunction.Id;
            deploymentFunctionId = deploymentFunction.Id;
        }

        await using var db = new AppDbContext(BuildOptions());
        var repository = new TaskRepository(db, TimeProvider.System);
        var claimed = await repository.ClaimPendingTasksAsync(
            serverId, 10, "33333333333333333333333333333333", TestContext.Current.CancellationToken);

        Assert.Equal(buildFunctionId, Assert.Single(claimed).Id);
        await using var verification = NewContext();
        var deploymentTask = await verification.Tasks.AsNoTracking()
            .SingleAsync(task => task.Id == deploymentFunctionId, TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Pending, deploymentTask.Status);
    }
}
