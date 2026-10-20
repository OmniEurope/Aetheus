// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.ModuleLinks;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Portsentry;
using Aetheus.Back.Components.ServerApps;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Components.Teamspeak;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

/// <summary> The column maps of the server detail grids and of the identity pages. Each map is
/// proved against rows it must keep and rows it must drop; the repositories that own a map are
/// proved to apply it before the count, so the grid's total is the filtered one. </summary>
public sealed class ServerAndIdentityGridColumnsTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GridFilter F(string field, GridFilterOperator op, string? value = null) =>
        new() { Field = field, Operator = op, Value = value };

    private static string List(params string[] values) => string.Join(GridFilter.ListSeparator, values);

    [Fact]
    public void RetiredServers_FilterByNameAndRetirementDate()
    {
        var servers = new[]
        {
            new Server { Id = 1, Name = "web-1", Hostname = "h1", DeletedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Server { Id = 2, Name = "db-1", Hostname = "h2", DeletedAt = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc) },
            new Server { Id = 3, Name = "web-2", Hostname = "h3", DeletedAt = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc) }
        }.AsQueryable();

        var kept = RetiredServerListQuery.Columns.ApplyFilters(servers,
        [
            F("Name", GridFilterOperator.Contains, "WEB"),
            new GridFilter
            {
                Field = "RetiredAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-05T00:00:00",
                SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-09-30T00:00:00"
            }
        ]);

        Assert.Equal([3], kept.Select(s => s.Id));
    }

    [Fact]
    public void ServerProjects_FilterByStatusListAndCreationDate()
    {
        var projects = new[]
        {
            new Project { Id = 1, Name = "a", Status = ProjectStatus.Active, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Project { Id = 2, Name = "b", Status = ProjectStatus.Archived, CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Project { Id = 3, Name = "c", Status = ProjectStatus.Archived, CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) }
        }.AsQueryable();

        var kept = ServerProjectListQuery.Columns.ApplyFilters(projects,
        [
            F("Status", GridFilterOperator.In, List("Archived")),
            F("CreatedAt", GridFilterOperator.GreaterThanOrEqual, "2026-01-01T00:00:00")
        ]);

        Assert.Equal([2], kept.Select(p => p.Id));
    }

    [Fact]
    public void Mail_DomainAccountAndAliasColumns_Filter()
    {
        var domains = new[]
        {
            new MailDomain { Id = 1, Name = "example.com", DkimSelector = "default" },
            new MailDomain { Id = 2, Name = "test.org", DkimSelector = "mail2026" }
        }.AsQueryable();
        var accounts = new[]
        {
            new MailAccount { Id = 1, Email = "a@example.com", CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new MailAccount { Id = 2, Email = "b@example.com", CreatedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc) }
        }.AsQueryable();
        var aliases = new[]
        {
            new MailAlias { Id = 1, SourceEmail = "info@example.com", DestinationEmail = "a@example.com" },
            new MailAlias { Id = 2, SourceEmail = "sales@example.com", DestinationEmail = "b@example.com" }
        }.AsQueryable();

        Assert.Equal([2], MailListQuery.DomainColumns.ApplyFilters(domains, [F("DkimSelector", GridFilterOperator.StartsWith, "mail")]).Select(d => d.Id));
        Assert.Equal([2], MailListQuery.AccountColumns.ApplyFilters(accounts, [F("CreatedAt", GridFilterOperator.GreaterThanOrEqual, "2026-09-02T00:00:00")]).Select(a => a.Id));
        Assert.Equal([1], MailListQuery.AliasColumns.ApplyFilters(aliases, [F("DestinationEmail", GridFilterOperator.Equals, "A@EXAMPLE.COM")]).Select(a => a.Id));
    }

    [Fact]
    public void Portsentry_BlockedAndWhitelistColumns_Filter()
    {
        var blocked = new[]
        {
            new PortsentryBlockedIp { Id = 1, IpAddress = "10.0.0.1", Protocol = "tcp", Reason = "scan", BlockedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc) },
            new PortsentryBlockedIp { Id = 2, IpAddress = "10.0.0.2", Protocol = "udp", Reason = "scan", BlockedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc) },
            new PortsentryBlockedIp { Id = 3, IpAddress = "10.0.0.3", Protocol = "TCP", Reason = "probe", BlockedAt = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc) }
        }.AsQueryable();
        var whitelist = new[]
        {
            new PortsentryWhitelistIp { Id = 1, IpAddress = "192.168.1.1", Description = "Office" },
            new PortsentryWhitelistIp { Id = 2, IpAddress = "192.168.1.2", Description = "Home" }
        }.AsQueryable();

        var tcp = PortsentryListQuery.BlockedColumns.ApplyFilters(blocked, [F("Protocol", GridFilterOperator.In, List("tcp"))]);
        var early = PortsentryListQuery.BlockedColumns.ApplyFilters(blocked, [F("BlockedAt", GridFilterOperator.LessThan, "2026-09-01T11:00:00")]);

        Assert.Equal([1, 3], tcp.Select(b => b.Id).Order());
        Assert.Equal([1], early.Select(b => b.Id));
        Assert.Equal([2], PortsentryListQuery.WhitelistColumns.ApplyFilters(whitelist, [F("Description", GridFilterOperator.Contains, "hom")]).Select(w => w.Id));
    }

    [Fact]
    public void Teamspeak_ClientChannelAndBanColumns_Filter()
    {
        var clients = new[]
        {
            new TeamspeakClient { Id = 1, Nickname = "alice", ChannelName = "Lobby", Platform = "Windows" },
            new TeamspeakClient { Id = 2, Nickname = "bob", ChannelName = "AFK", Platform = "Linux" }
        }.AsQueryable();
        var channels = new[]
        {
            new TeamspeakChannel { Id = 1, Name = "Lobby" },
            new TeamspeakChannel { Id = 2, Name = "AFK" }
        }.AsQueryable();
        var bans = new[]
        {
            new TeamspeakBan { Id = 1, BanId = 7, Nickname = "troll", UniqueId = "u1", Reason = "spam" },
            new TeamspeakBan { Id = 2, BanId = 8, Nickname = "noisy", UniqueId = "u2", Reason = "noise" }
        }.AsQueryable();

        Assert.Equal([2], TeamspeakListQuery.ClientColumns.ApplyFilters(clients, [F("Platform", GridFilterOperator.In, List("linux"))]).Select(c => c.Id));
        Assert.Equal([1], TeamspeakListQuery.ClientColumns.ApplyFilters(clients, [F("ChannelName", GridFilterOperator.Contains, "lob")]).Select(c => c.Id));
        Assert.Equal([2], TeamspeakListQuery.ChannelColumns.ApplyFilters(channels, [F("Name", GridFilterOperator.Equals, "afk")]).Select(c => c.Id));
        Assert.Equal([2], TeamspeakListQuery.BanColumns.ApplyFilters(bans, [F("BanId", GridFilterOperator.Equals, "8")]).Select(b => b.Id));
    }

    [Fact]
    public void Roles_ListColumns_FilterTheProjectedRows_AndUsersColumns_FilterTheUserEntity()
    {
        var roles = new[]
        {
            new RoleDto { Id = 1, Name = "Admin", Description = "All", PermissionCount = 9, UserCount = 1 },
            new RoleDto { Id = 2, Name = "Reader", Description = "Read only", PermissionCount = 3, UserCount = 5 }
        }.AsQueryable();
        var members = new[]
        {
            new User { Id = 1, Username = "alice", Email = "alice@example.test", IsActive = true },
            new User { Id = 2, Username = "bob", Email = null, IsActive = false }
        }.AsQueryable();

        Assert.Equal([2], RoleListQuery.Columns.ApplyFilters(roles, [F("UserCount", GridFilterOperator.GreaterThan, "2")]).Select(r => r.Id));
        Assert.Equal([2], RoleListQuery.Columns.ApplyFilters(roles, [F("Description", GridFilterOperator.Contains, "read")]).Select(r => r.Id));
        Assert.Equal([2], RoleListQuery.UserColumns.ApplyFilters(members, [F("IsActive", GridFilterOperator.Equals, bool.FalseString)]).Select(u => u.Id));
    }

    [Fact]
    public void Organizations_FilterBySlugAndMemberCount()
    {
        var organizations = new[]
        {
            new Organization { Id = 1, Name = "Acme", Slug = "acme", Members = [new OrganizationMember(), new OrganizationMember()] },
            new Organization { Id = 2, Name = "Beta", Slug = "beta", Members = [new OrganizationMember()] }
        }.AsQueryable();

        Assert.Equal([1], OrganizationListQuery.Columns.ApplyFilters(organizations, [F("MemberCount", GridFilterOperator.Equals, "2")]).Select(o => o.Id));
        Assert.Equal([2], OrganizationListQuery.Columns.ApplyFilters(organizations, [F("Slug", GridFilterOperator.StartsWith, "be")]).Select(o => o.Id));
    }

    [Fact]
    public async Task Users_RolesFilter_KeepsUsersHoldingAnyTickedRole_AndTheTotalIsFiltered()
    {
        var admin = new Role { Id = 1, Name = "Admin" };
        var reader = new Role { Id = 2, Name = "Reader" };
        _db.Roles.AddRange(admin, reader);
        _db.Users.AddRange(
            new User { Id = 1, Username = "alice", IsActive = true },
            new User { Id = 2, Username = "bob", IsActive = true },
            new User { Id = 3, Username = "carol", IsActive = false });
        _db.UserRoles.AddRange(
            new UserRole { UserId = 1, RoleId = 1, Role = admin },
            new UserRole { UserId = 2, RoleId = 2, Role = reader },
            new UserRole { UserId = 3, RoleId = 1, Role = admin });
        await _db.SaveChangesAsync(Ct);
        var repo = new UserRepository(_db);

        var (items, total) = await repo.GetUsersPagedProjectedAsync(null, 1, 10, Ct, filters:
        [
            F("Roles", GridFilterOperator.In, List("admin")),
            F("IsActive", GridFilterOperator.Equals, bool.TrueString)
        ]);

        Assert.Equal(1, total);
        Assert.Equal("alice", Assert.Single(items).Username);
    }

    [Fact]
    public void Users_RolesFilter_RejectsAnOperatorThatIsNotAList()
    {
        Assert.Throws<BadRequestException>(() =>
            UserListQuery.Columns.ApplyFilters(Array.Empty<User>().AsQueryable(), [F("Roles", GridFilterOperator.Contains, "adm")]));
    }

    [Fact]
    public async Task ServerApps_FiltersApplyBeforeTheCount_AndTheSourcesAreTheServersOwn()
    {
        _db.ServerApps.AddRange(
            new ServerApp { Id = 1, ServerId = 1, Name = "nginx", Status = ServerAppStatus.Running, Source = "docker", Port = 80 },
            new ServerApp { Id = 2, ServerId = 1, Name = "api", Status = ServerAppStatus.Stopped, Source = "systemd", Port = 5000 },
            new ServerApp { Id = 3, ServerId = 1, Name = "worker", Status = ServerAppStatus.Running, Source = "systemd" },
            new ServerApp { Id = 4, ServerId = 2, Name = "other", Status = ServerAppStatus.Running, Source = "manual" });
        await _db.SaveChangesAsync(Ct);
        var repo = new ServerAppRepository(_db);

        var (items, total) = await repo.GetPageAsync(1, null, null, false, 1, 25, Ct,
        [
            F("Status", GridFilterOperator.In, List("Running")),
            F("Source", GridFilterOperator.In, List("systemd"))
        ]);
        var byPort = await repo.GetPageAsync(1, null, null, false, 1, 25, Ct, [F("Port", GridFilterOperator.Equals, "80")]);
        var sources = await repo.GetSourcesAsync(1, Ct);

        Assert.Equal(1, total);
        Assert.Equal("worker", Assert.Single(items).Name);
        Assert.Equal("nginx", Assert.Single(byPort.Items).Name);
        Assert.Equal(["docker", "systemd"], sources);
    }

    [Fact]
    public async Task ModuleLinks_TypeFilter_ReadsTheOtherSideOfEachLink()
    {
        _db.ModuleLinks.AddRange(
            // nginx (Docker) -> Apache vhost: the row shows Apache.
            new ModuleLink { Id = 1, ServerId = 1, SourceType = ModuleLinkType.Docker, SourceIdentifier = "nginx", TargetType = ModuleLinkType.Apache, TargetIdentifier = "site.conf" },
            // Certbot cert -> nginx (Docker): the row shows Certbot.
            new ModuleLink { Id = 2, ServerId = 1, SourceType = ModuleLinkType.Certbot, SourceIdentifier = "example.com", TargetType = ModuleLinkType.Docker, TargetIdentifier = "nginx", IsAutoDetected = true });
        await _db.SaveChangesAsync(Ct);
        var repo = new ModuleLinkRepository(_db);

        var (items, total) = await repo.GetLinksPageAsync(1, new ModuleLinkPageRequest
        {
            SourceType = ModuleLinkType.Docker,
            ResourceIdentifiers = ["nginx"],
            Filters = [F("Type", GridFilterOperator.In, List("Certbot"))]
        }, Ct);
        var manual = await repo.GetLinksPageAsync(1, new ModuleLinkPageRequest
        {
            SourceType = ModuleLinkType.Docker,
            ResourceIdentifiers = ["nginx"],
            Filters = [F("IsAutoDetected", GridFilterOperator.Equals, bool.FalseString), F("Identifier", GridFilterOperator.Contains, "site")]
        }, Ct);

        Assert.Equal(1, total);
        Assert.Equal(2, Assert.Single(items).Id);
        Assert.Equal(1, Assert.Single(manual.Items).Id);
    }
}
