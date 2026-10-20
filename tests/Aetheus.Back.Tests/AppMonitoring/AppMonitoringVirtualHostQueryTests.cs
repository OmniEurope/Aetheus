// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary> The fleet server deduced to host a backend-probed app, read from the enabled Apache
/// virtual hosts of the project's organization. Several servers serving the host, a disabled vhost,
/// a deleted server or another organization's server give no answer rather than a guess. </summary>
public sealed class AppMonitoringVirtualHostQueryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppMonitoringRepository _repository;

    public AppMonitoringVirtualHostQueryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options);
        _repository = new AppMonitoringRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task VirtualHostServers_OneServerOfTheOrganizationPerHost_OrNone()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.Projects.Add(new Project { Id = 1, Name = "Aetheus", Description = "d", OrganizationId = 1 });
        _db.Servers.AddRange(
            new Server { Id = 7, Name = "vps2577917", OrganizationId = 1 },
            new Server { Id = 8, Name = "web-2", OrganizationId = 1 },
            new Server { Id = 9, Name = "retired", OrganizationId = 1, DeletedAt = DateTime.UtcNow },
            new Server { Id = 10, Name = "other-org", OrganizationId = 2 });
        _db.ApacheVirtualHosts.AddRange(
            new ApacheVirtualHost { Id = 1, ServerId = 7, ServerName = "Aetheus-API.example.com", IsEnabled = true },
            new ApacheVirtualHost { Id = 2, ServerId = 7, ServerName = "shared.example.com", IsEnabled = true },
            new ApacheVirtualHost { Id = 3, ServerId = 8, ServerName = "shared.example.com", IsEnabled = true },
            new ApacheVirtualHost { Id = 4, ServerId = 8, ServerName = "disabled.example.com", IsEnabled = false },
            new ApacheVirtualHost { Id = 5, ServerId = 9, ServerName = "gone.example.com", IsEnabled = true },
            new ApacheVirtualHost { Id = 6, ServerId = 10, ServerName = "foreign.example.com", IsEnabled = true });
        await _db.SaveChangesAsync(ct);

        var servers = await _repository.GetVirtualHostServersAsync(1,
            ["aetheus-api.example.com", "shared.example.com", "disabled.example.com", "gone.example.com", "foreign.example.com"], ct);

        var single = Assert.Single(servers);
        Assert.Equal("aetheus-api.example.com", single.Key);
        Assert.Equal(new AppHostingServer(7, "vps2577917"), single.Value);
    }
}
