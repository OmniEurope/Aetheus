// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.ModuleLinks;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.ServerApps;
using Aetheus.Back.Components.Teamspeak;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Repositories;

/// <summary>Every sort key of the paged repository queries, ascending and descending, on rows whose order
/// differs from key to key: the resulting order is asserted, not just that a page comes back.</summary>
public sealed class PagedSortOrderTests : IDisposable
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db;

    public PagedSortOrderTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Saves the new rows, then gives them the wanted creation days: the context stamps CreatedAt on insert.</summary>
    private async Task StampCreatedAtAsync<T>(T[] rows, int[] days, Action<T, DateTime> setCreatedAt)
    {
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        for (var index = 0; index < rows.Length; index++)
            setCreatedAt(rows[index], Base.AddDays(days[index]));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public static TheoryData<string?, bool, string> MailDomainOrders => new()
    {
        { null, false, "a.com,b.com,c.com" },
        { "Name", true, "c.com,b.com,a.com" },
        { "IsActive", false, "c.com,a.com,b.com" },
        { "isactive", true, "a.com,b.com,c.com" },
        { "CreatedAt", false, "c.com,a.com,b.com" },
        { "createdat", true, "b.com,a.com,c.com" },
        { "DkimSelector", false, "b.com,a.com,c.com" },
        { "dkimselector", true, "c.com,a.com,b.com" },
    };

    [Theory]
    [MemberData(nameof(MailDomainOrders))]
    public async Task MailRepository_GetDomainsPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        MailDomain[] domains =
        [
            new() { ServerId = 1, Name = "b.com", IsActive = true, DkimSelector = "k1" },
            new() { ServerId = 1, Name = "c.com", IsActive = false, DkimSelector = "k3" },
            new() { ServerId = 1, Name = "a.com", IsActive = true, DkimSelector = "k2" },
            new() { ServerId = 2, Name = "other.com" },
        ];
        _db.MailDomains.AddRange(domains);
        await StampCreatedAtAsync(domains, [3, 1, 2, 4], (domain, at) => domain.CreatedAt = at);

        var (items, total) = await new MailRepository(_db).GetDomainsPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Name)));
    }

    public static TheoryData<string?, bool, string> MailAccountOrders => new()
    {
        { "Email", false, "a@x,m@x,z@x" },
        { null, true, "z@x,m@x,a@x" },
        { "QuotaMb", false, "z@x,a@x,m@x" },
        { "quotamb", true, "m@x,a@x,z@x" },
        { "IsActive", false, "z@x,a@x,m@x" },
        { "isactive", true, "a@x,m@x,z@x" },
        { "CreatedAt", false, "a@x,m@x,z@x" },
        { "createdat", true, "z@x,m@x,a@x" },
    };

    [Theory]
    [MemberData(nameof(MailAccountOrders))]
    public async Task MailRepository_GetAccountsPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        var domain = new MailDomain { ServerId = 1, Name = "x" };
        _db.MailDomains.Add(domain);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        MailAccount[] accounts =
        [
            new() { MailDomainId = domain.Id, Email = "m@x", QuotaMb = 300, IsActive = true },
            new() { MailDomainId = domain.Id, Email = "z@x", QuotaMb = 100, IsActive = false },
            new() { MailDomainId = domain.Id, Email = "a@x", QuotaMb = 200, IsActive = true },
        ];
        _db.MailAccounts.AddRange(accounts);
        await StampCreatedAtAsync(accounts, [2, 3, 1], (account, at) => account.CreatedAt = at);

        var (items, total) = await new MailRepository(_db).GetAccountsPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Email)));
    }

    public static TheoryData<string?, bool, string> MailAliasOrders => new()
    {
        { "SourceEmail", false, "a@x,m@x,z@x" },
        { null, true, "z@x,m@x,a@x" },
        { "DestinationEmail", false, "z@x,a@x,m@x" },
        { "destinationemail", true, "m@x,a@x,z@x" },
        { "IsActive", false, "z@x,a@x,m@x" },
        { "isactive", true, "a@x,m@x,z@x" },
        { "CreatedAt", false, "a@x,m@x,z@x" },
        { "createdat", true, "z@x,m@x,a@x" },
    };

    [Theory]
    [MemberData(nameof(MailAliasOrders))]
    public async Task MailRepository_GetAliasesPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        var domain = new MailDomain { ServerId = 1, Name = "x" };
        _db.MailDomains.Add(domain);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        MailAlias[] aliases =
        [
            new() { MailDomainId = domain.Id, SourceEmail = "m@x", DestinationEmail = "c@x", IsActive = true },
            new() { MailDomainId = domain.Id, SourceEmail = "z@x", DestinationEmail = "a@x", IsActive = false },
            new() { MailDomainId = domain.Id, SourceEmail = "a@x", DestinationEmail = "b@x", IsActive = true },
        ];
        _db.MailAliases.AddRange(aliases);
        await StampCreatedAtAsync(aliases, [2, 3, 1], (alias, at) => alias.CreatedAt = at);

        var (items, total) = await new MailRepository(_db).GetAliasesPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.SourceEmail)));
    }

    public static TheoryData<string?, bool, string> NotificationChannelOrders => new()
    {
        { "Name", false, "Alpha,Mid,Zed" },
        { null, true, "Zed,Mid,Alpha" },
        { "Type", false, "Zed,Mid,Alpha" },
        { "type", true, "Alpha,Mid,Zed" },
        { "IsEnabled", false, "Zed,Alpha,Mid" },
        { "isenabled", true, "Alpha,Mid,Zed" },
        { "RuleCount", false, "Mid,Alpha,Zed" },
        { "rulecount", true, "Zed,Alpha,Mid" },
        { "CreatedAt", false, "Alpha,Mid,Zed" },
        { "createdat", true, "Zed,Mid,Alpha" },
    };

    [Theory]
    [MemberData(nameof(NotificationChannelOrders))]
    public async Task NotificationRepository_GetChannelsPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        var mid = new NotificationChannel { Name = "Mid", Type = NotificationChannelType.Slack, IsEnabled = true };
        var zed = new NotificationChannel { Name = "Zed", Type = NotificationChannelType.Email, IsEnabled = false };
        var alpha = new NotificationChannel { Name = "Alpha", Type = NotificationChannelType.Webhook, IsEnabled = true };
        zed.Rules.AddRange([new NotificationRule { EventType = "e1" }, new NotificationRule { EventType = "e2" }]);
        alpha.Rules.Add(new NotificationRule { EventType = "e3" });
        NotificationChannel[] channels = [mid, zed, alpha];
        _db.NotificationChannels.AddRange(channels);
        await StampCreatedAtAsync(channels, [2, 3, 1], (channel, at) => channel.CreatedAt = at);

        var (items, total) = await new NotificationRepository(_db).GetChannelsPagedAsync(
            null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Name)));
    }

    public static TheoryData<string?, bool, string> NotificationRuleOrders => new()
    {
        { "EventType", false, "a,b,c" },
        { null, true, "c,b,a" },
        { "ChannelName", false, "a,c,b" },
        { "channelname", true, "b,c,a" },
        { "IsEnabled", false, "b,a,c" },
        { "isenabled", true, "a,c,b" },
        { "CreatedAt", false, "a,c,b" },
        { "createdat", true, "b,c,a" },
    };

    [Theory]
    [MemberData(nameof(NotificationRuleOrders))]
    public async Task NotificationRepository_GetRulesPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        var zed = new NotificationChannel { Name = "Zed" };
        var mid = new NotificationChannel { Name = "Mid" };
        var alpha = new NotificationChannel { Name = "Alpha" };
        _db.NotificationChannels.AddRange(zed, mid, alpha);
        NotificationRule[] rules =
        [
            new() { EventType = "b", Channel = zed, IsEnabled = false },
            new() { EventType = "c", Channel = mid, IsEnabled = true },
            new() { EventType = "a", Channel = alpha, IsEnabled = true },
        ];
        _db.NotificationRules.AddRange(rules);
        await StampCreatedAtAsync(rules, [3, 2, 1], (rule, at) => rule.CreatedAt = at);

        var (items, total) = await new NotificationRepository(_db).GetRulesPagedAsync(
            null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.EventType)));
    }

    public static TheoryData<string?, bool, string> TeamspeakBanOrders => new()
    {
        { "Nickname", false, "alan,mike,zed" },
        { "nickname", true, "zed,mike,alan" },
        { "Reason", false, "alan,zed,mike" },
        { "reason", true, "mike,zed,alan" },
        { "Created", false, "mike,alan,zed" },
        { null, false, "zed,alan,mike" },
        { "unknown", true, "zed,alan,mike" },
    };

    [Theory]
    [MemberData(nameof(TeamspeakBanOrders))]
    public async Task TeamspeakRepository_GetBansPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        _db.TeamspeakBans.AddRange(
            new TeamspeakBan { ServerId = 1, BanId = 1, Nickname = "mike", Reason = "z", Created = 100 },
            new TeamspeakBan { ServerId = 1, BanId = 2, Nickname = "zed", Reason = "m", Created = 300 },
            new TeamspeakBan { ServerId = 1, BanId = 3, Nickname = "alan", Reason = "a", Created = 200 },
            new TeamspeakBan { ServerId = 2, BanId = 4, Nickname = "other" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, total) = await new TeamspeakRepository(_db).GetBansPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Nickname)));
    }

    public static TheoryData<string?, bool, string> ServerAppOrders => new()
    {
        { "Name", false, "alan,mid,zed" },
        { "Name", true, "zed,mid,alan" },
        { null, false, "alan,mid,zed" },
        { "Version", false, "alan,zed,mid" },
        { "Version", true, "mid,zed,alan" },
        { "Port", false, "zed,alan,mid" },
        { "Port", true, "mid,alan,zed" },
        { "Source", false, "zed,mid,alan" },
        { "Source", true, "alan,mid,zed" },
        { "Status", false, "zed,alan,mid" },
        { "Status", true, "mid,alan,zed" },
    };

    [Theory]
    [MemberData(nameof(ServerAppOrders))]
    public async Task ServerAppRepository_GetPageAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        _db.ServerApps.AddRange(
            new ServerApp { ServerId = 1, Name = "mid", Version = "3", Port = 300, Source = "b", Status = ServerAppStatus.Error },
            new ServerApp { ServerId = 1, Name = "zed", Version = "2", Port = 100, Source = "a", Status = ServerAppStatus.Running },
            new ServerApp { ServerId = 1, Name = "alan", Version = "1", Port = 200, Source = "c", Status = ServerAppStatus.Stopped },
            new ServerApp { ServerId = 2, Name = "other", Source = "x" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, total) = await new ServerAppRepository(_db).GetPageAsync(
            1, null, sortBy, descending, 1, 10, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Name)));
    }

    public static TheoryData<string?, bool, string> ProjectServerOrders => new()
    {
        { "DisplayName", false, "alpha,mid,zed" },
        { "DisplayName", true, "zed,mid,alpha" },
        { null, false, "alpha,mid,zed" },
        { "Host", false, "zed,alpha,mid" },
        { "Host", true, "mid,alpha,zed" },
        { "Port", false, "mid,zed,alpha" },
        { "Port", true, "alpha,zed,mid" },
        { "Type", false, "zed,mid,alpha" },
        { "Type", true, "mid,alpha,zed" },
    };

    [Theory]
    [MemberData(nameof(ProjectServerOrders))]
    public async Task ProjectRepository_GetProjectServersPageAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        _db.ProjectServers.AddRange(
            new ProjectServer { ProjectId = 1, DisplayName = "mid", Host = "h3", Port = 10, Type = ProjectServerType.ExternalHost },
            new ProjectServer { ProjectId = 1, DisplayName = "zed", Host = "h1", Port = 20, Type = ProjectServerType.AgentServer },
            new ProjectServer { ProjectId = 1, DisplayName = "alpha", Host = "h2", Port = 30, Type = ProjectServerType.ExternalHost },
            new ProjectServer { ProjectId = 2, DisplayName = "other", Host = "h9" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new ProjectRepository(_db, Substitute.For<Aetheus.Back.Components.Analysis.IProjectAnalysisGradeReader>());
        var (items, total) = await repository.GetProjectServersPageAsync(
            1, null, sortBy, descending, 1, 10, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.DisplayName)));
    }

    public static TheoryData<string?, bool, string> ModuleLinkOrders => new()
    {
        { null, false, "aa,mm,zz" },
        { "Identifier", true, "zz,mm,aa" },
        { "Type", false, "zz,aa,mm" },
        { "type", true, "mm,aa,zz" },
        { "IsAutoDetected", false, "zz,aa,mm" },
        { "isautodetected", true, "mm,aa,zz" },
    };

    [Theory]
    [MemberData(nameof(ModuleLinkOrders))]
    public async Task ModuleLinkRepository_GetLinksPageAsync_OrdersByTheOtherEndOfTheLink(string? sortBy, bool descending, string expected)
    {
        _db.ModuleLinks.AddRange(
            new ModuleLink
            {
                ServerId = 1,
                SourceType = ModuleLinkType.Docker,
                SourceIdentifier = "web",
                TargetType = ModuleLinkType.Apache,
                TargetIdentifier = "zz",
                IsAutoDetected = false,
            },
            new ModuleLink
            {
                ServerId = 1,
                SourceType = ModuleLinkType.Certbot,
                SourceIdentifier = "aa",
                TargetType = ModuleLinkType.Docker,
                TargetIdentifier = "web",
                IsAutoDetected = true,
            },
            new ModuleLink
            {
                ServerId = 1,
                SourceType = ModuleLinkType.Docker,
                SourceIdentifier = "web",
                TargetType = ModuleLinkType.Mail,
                TargetIdentifier = "mm",
                IsAutoDetected = true,
            });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new ModuleLinkPageRequest
        {
            SourceType = ModuleLinkType.Docker,
            ResourceIdentifiers = ["web"],
            SortBy = sortBy,
            SortDescending = descending,
        };
        var (items, total) = await new ModuleLinkRepository(_db).GetLinksPageAsync(1, request, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        var counterpart = items.Select(link => link.SourceType == ModuleLinkType.Docker ? link.TargetIdentifier : link.SourceIdentifier);
        Assert.Equal(expected, string.Join(",", counterpart));
    }
}
