// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

public sealed class AgentUpdateRepositoryIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentReservation_IsIdempotentAndCreatesOneActiveRequest()
    {
        await ResetAndMigrateAsync();
        await using (var seed = NewContext())
        {
            seed.Servers.Add(CurrentServer());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var release = CurrentRelease();
        var clock = new MutableTimeProvider();
        async Task<(int RequestId, bool Created)> ReserveAsync(string requestedBy)
        {
            await using var db = NewContext();
            var server = await db.Servers.SingleAsync(TestContext.Current.CancellationToken);
            var repository = new AgentUpdateRepository(db, clock);
            var result = await repository.ReserveAsync(
                server, release, requestedBy, TestContext.Current.CancellationToken);
            return (result.Request.Id, result.Created);
        }

        var results = await Task.WhenAll(ReserveAsync("admin-a"), ReserveAsync("admin-b"));

        Assert.Single(results, result => result.Created);
        Assert.Equal(results[0].RequestId, results[1].RequestId);
        await using var verify = NewContext();
        Assert.Equal(1, await verify.AgentUpdateRequests.CountAsync(
            request => request.IsActive,
            TestContext.Current.CancellationToken));
        Assert.True((await verify.Servers.SingleAsync(
            TestContext.Current.CancellationToken)).AgentUpdateReserved);
    }

    private static Server CurrentServer() => new()
    {
        Name = "agent-1",
        Hostname = "agent-1",
        Organization = new Organization
        {
            Name = "Agent update tests",
            Slug = $"agent-update-{Guid.NewGuid():N}",
            Description = "Agent update integration tests"
        },
        Status = ServerStatus.Online,
        AgentVersion = "0.9.0",
        AgentProtocolVersion = AgentProtocol.CurrentVersion,
        AgentCapabilitiesJson = "[\"agent.self-update\",\"pipeline.build\",\"shell.execute\"]",
        LastHeartbeat = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc)
    };

    private static AgentReleaseManifestDto CurrentRelease() => new()
    {
        SoftwareVersion = "1.0.0",
        ProtocolVersion = AgentProtocol.CurrentVersion,
        MinimumSupportedProtocol = AgentProtocol.MinimumSupportedVersion,
        MaximumSupportedProtocol = AgentProtocol.MaximumSupportedVersion,
        SoftwareCapabilities = [AgentCapabilities.SelfUpdate, AgentCapabilities.PipelineBuild]
    };

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
