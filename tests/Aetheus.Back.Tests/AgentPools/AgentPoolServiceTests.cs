// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentPools;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class AgentPoolServiceTests
{
    private readonly IAgentPoolRepository _repoMock = Substitute.For<IAgentPoolRepository>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly AgentPoolService _sut;

    public AgentPoolServiceTests()
    {
        _sut = new AgentPoolService(_repoMock, _auditMock, Substitute.For<IEntityChangeNotifier>(), TimeProvider.System);
        _repoMock.AllServersInOrganizationAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task GetPoolsAsync_ReturnsPaginatedResult()
    {
        var pools = new List<AgentPool>
        {
            new() { Id = 1, Name = "Pool1", Description = "Desc1", MaxConcurrency = 3, Servers = new List<AgentPoolServer>() }
        };
        _repoMock.GetPoolsPagedAsync(null, 1, 20, null, Arg.Any<CancellationToken>())
            .Returns((pools, 1));

        var result = await _sut.GetPoolsAsync(new PaginationRequest { Page = 1, PageSize = 20 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Pool1", result.Items[0].Name);
    }

    [Fact]
    public async Task GetPoolsAsync_WithAccessibleIds_PassesToRepo()
    {
        _repoMock.GetPoolsPagedAsync(null, 1, 20, Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns((new List<AgentPool>(), 0));

        var result = await _sut.GetPoolsAsync(new PaginationRequest { Page = 1, PageSize = 20 }, [1, 2], ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetPoolAsync_Found_ReturnsDto()
    {
        _repoMock.GetPoolWithServersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AgentPool
            {
                Id = 1,
                Name = "Pool1",
                Description = "Desc1",
                MaxConcurrency = 3,
                Servers = new List<AgentPoolServer>
                {
                    new() { ServerId = 10, Server = new Server { Name = "srv1", Status = ServerStatus.Online } }
                }
            });

        var result = await _sut.GetPoolAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Pool1", result.Name);
        Assert.Single(result.Servers);
        Assert.Equal("srv1", result.Servers[0].ServerName);
    }

    [Fact]
    public async Task GetPoolAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetPoolWithServersAsync(999, Arg.Any<CancellationToken>()).Returns((AgentPool?)null);

        var result = await _sut.GetPoolAsync(999, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreatePoolAsync_CreatesPoolAndAudits()
    {
        var request = new CreateAgentPoolRequest { Name = "NewPool", Description = "Desc", MaxConcurrency = 5, ServerIds = [10, 20], OrganizationId = 7 };

        _repoMock.AddPoolAsync(Arg.Any<AgentPool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPoolWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AgentPool { Id = 1, Name = "NewPool", Description = "Desc", MaxConcurrency = 5, Servers = new List<AgentPoolServer>() });

        var result = await _sut.CreatePoolAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("NewPool", result.Name);
        await _repoMock.Received(1).AddPoolAsync(Arg.Is<AgentPool>(p => p.Name == "NewPool" && p.Servers.Count == 2 && p.OrganizationId == 7), Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AllServersInOrganizationAsync(7, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Created", "AgentPool", Arg.Any<int>(), "NewPool", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePoolAsync_Found_UpdatesAndAudits()
    {
        var entity = new AgentPool
        {
            Id = 1,
            Name = "Old",
            Description = "OldDesc",
            MaxConcurrency = 1,
            Servers = new List<AgentPoolServer> { new() { ServerId = 10 } }
        };
        _repoMock.FindPoolAsync(1, Arg.Any<CancellationToken>()).Returns(entity);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPoolWithServersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AgentPool { Id = 1, Name = "Updated", Servers = new List<AgentPoolServer>() });

        var request = new UpdateAgentPoolRequest { Name = "Updated", Description = "NewDesc", MaxConcurrency = 10, ServerIds = [20, 30] };
        var result = await _sut.UpdatePoolAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Updated", entity.Name);
        Assert.Equal(10, entity.MaxConcurrency);
        await _auditMock.Received(1).LogAsync("Updated", "AgentPool", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePoolAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindPoolAsync(999, Arg.Any<CancellationToken>()).Returns((AgentPool?)null);

        var result = await _sut.UpdatePoolAsync(999, new UpdateAgentPoolRequest { Name = "X", ServerIds = [] }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeletePoolAsync_Found_DeletesAndAudits()
    {
        var entity = new AgentPool { Id = 1, Name = "Pool1", Servers = new List<AgentPoolServer>() };
        _repoMock.FindPoolAsync(1, Arg.Any<CancellationToken>()).Returns(entity);

        var result = await _sut.DeletePoolAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).RemovePoolAsync(entity, Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Deleted", "AgentPool", 1, "Pool1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePoolAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindPoolAsync(999, Arg.Any<CancellationToken>()).Returns((AgentPool?)null);

        var result = await _sut.DeletePoolAsync(999, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CreatePoolAsync_WithoutOrganization_GoesToTheDefaultOrganization()
    {
        _repoMock.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(3);
        _repoMock.GetPoolWithServersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AgentPool { Id = 1, Name = "P", OrganizationId = 3 });

        var result = await _sut.CreatePoolAsync(new CreateAgentPoolRequest { Name = "P" }, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.OrganizationId);
        await _repoMock.Received(1).AddPoolAsync(Arg.Is<AgentPool>(p => p.OrganizationId == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePoolAsync_NoOrganizationAtAll_IsRefused()
    {
        _repoMock.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreatePoolAsync(new CreateAgentPoolRequest { Name = "P" }, TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().AddPoolAsync(Arg.Any<AgentPool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePoolAsync_ServerOfAnotherOrganization_IsRefused()
    {
        _repoMock.AllServersInOrganizationAsync(7, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreatePoolAsync(
            new CreateAgentPoolRequest { Name = "P", OrganizationId = 7, ServerIds = [10] }, TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().AddPoolAsync(Arg.Any<AgentPool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePoolAsync_NameTaken_IsAConflict()
    {
        _repoMock.NameTakenAsync("P", null, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CreatePoolAsync(
            new CreateAgentPoolRequest { Name = "P", OrganizationId = 7 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdatePoolAsync_ServerOfAnotherOrganization_IsRefused_AndNothingChanges()
    {
        var entity = new AgentPool { Id = 1, Name = "Old", OrganizationId = 4, Servers = [new() { ServerId = 10 }] };
        _repoMock.FindPoolAsync(1, Arg.Any<CancellationToken>()).Returns(entity);
        _repoMock.AllServersInOrganizationAsync(4, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdatePoolAsync(
            1, new UpdateAgentPoolRequest { Name = "New", ServerIds = [99] }, TestContext.Current.CancellationToken));

        Assert.Equal("Old", entity.Name);
        Assert.Single(entity.Servers);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPoolAsync_HidesALinkToAServerOfAnotherOrganization()
    {
        _repoMock.GetPoolWithServersAsync(1, Arg.Any<CancellationToken>()).Returns(new AgentPool
        {
            Id = 1,
            Name = "P",
            OrganizationId = 4,
            Servers =
            [
                new() { ServerId = 10, Server = new Server { Name = "own", OrganizationId = 4 } },
                new() { ServerId = 11, Server = new Server { Name = "foreign", OrganizationId = 5 } }
            ]
        });

        var result = await _sut.GetPoolAsync(1, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("own", Assert.Single(result.Servers).ServerName);
    }
}
