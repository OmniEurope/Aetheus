// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

/// <summary> The servers list's column header filters. The stored columns go through the generic
/// map in the query; tags and the agent column (versions mixed with compatibility states) resolve
/// to server ids first. </summary>
public sealed class ServerListQueryTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private static GridFilter In(string field, params string[] values) =>
        new() { Field = field, Operator = GridFilterOperator.In, Value = string.Join(GridFilter.ListSeparator, values) };

    [Fact]
    public async Task StoredColumns_AreFilteredInTheQuery_BeforeTheCount()
    {
        _db.Servers.AddRange(
            new Server { Name = "web-1", Hostname = "w1", Status = ServerStatus.Online, Type = ServerType.Docker, OsDescription = "Ubuntu 24.04" },
            new Server { Name = "web-2", Hostname = "w2", Status = ServerStatus.Offline, Type = ServerType.Normal, OsDescription = "Debian 12" },
            new Server { Name = "db-1", Hostname = "d1", Status = ServerStatus.Online, Type = ServerType.Normal, OsDescription = "Debian 12" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repo = new ServerRepository(_db, TimeProvider.System);

        var (items, total) = await repo.GetServersPagedProjectedAsync(
            null, "name", false, null, null, 1, 10, ct: TestContext.Current.CancellationToken,
            columnFilters: [In("Status", "Online"), In("OsDescription", "debian 12", "Windows Server")]);

        Assert.Equal(1, total);
        Assert.Equal(["db-1"], items.Select(server => server.Name));
    }

    [Fact]
    public async Task FilterFacts_ReadTheTagsOfEveryServerInScope()
    {
        _db.Servers.AddRange(
            new Server { Name = "a", Hostname = "a", Tags = "[\"prod\",\"eu\"]", AgentVersion = "2.1.0" },
            new Server { Name = "b", Hostname = "b", Tags = "[]", AgentVersion = "2.0.0" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repo = new ServerRepository(_db, TimeProvider.System);

        var facts = await repo.GetServerFilterFactsAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(["prod", "eu"], facts.Single(fact => fact.AgentVersion == "2.1.0").Tags);
        Assert.Empty(facts.Single(fact => fact.AgentVersion == "2.0.0").Tags);
    }

    [Fact]
    public void Tags_MatchAServerHoldingAnyTickedTag_WithoutCase()
    {
        var ids = ServerListQuery.TaggedWithAny(In("Tags", "PROD", "lab"),
        [
            (1, ["prod", "eu"]),
            (2, ["staging"]),
            (3, ["lab"])
        ]);

        Assert.Equal([1, 3], ids.Order());
    }

    [Fact]
    public void AgentColumn_MatchesATickedVersionOrATickedState()
    {
        var ids = ServerListQuery.AgentMatchingAny(
            In("AgentVersion", "2.1.0", ServerAgentFilter.State(AgentCompatibilityStatus.UpdateRequired)),
            [
                (1, "2.1.0", AgentCompatibilityStatus.UpToDate),
                (2, "1.0.0", AgentCompatibilityStatus.UpdateRequired),
                (3, "2.0.0", AgentCompatibilityStatus.UpdateRecommended),
                (4, "2.0.0", null)
            ]);

        Assert.Equal([1, 2], ids.Order());
    }

    [Fact]
    public void AgentColumn_RefusesAStateThatDoesNotExist_AndAnOperatorItDoesNotOffer()
    {
        Assert.Throws<BadRequestException>(() => ServerListQuery.AgentMatchingAny(
            In("AgentVersion", ServerAgentFilter.StatePrefix + "Sleeping"), []));
        Assert.Throws<BadRequestException>(() => ServerListQuery.TaggedWithAny(
            new GridFilter { Field = "Tags", Operator = GridFilterOperator.Contains, Value = "prod" }, []));
    }

    [Fact]
    public void OnlyTagsAndTheAgentColumn_AreResolvedInMemory()
    {
        Assert.True(ServerListQuery.IsResolvedInMemory(In("tags", "x")));
        Assert.True(ServerListQuery.IsResolvedInMemory(In("AgentVersion", "x")));
        Assert.False(ServerListQuery.IsResolvedInMemory(In("Status", "Online")));
    }

    public void Dispose() => _db.Dispose();
}
