// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Components.Plugins;
using Aetheus.Back.Components.Portsentry;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Teamspeak;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Repositories;

/// <summary>Every sort key of the remaining paged grid queries (blocked addresses, plugins, entry versions,
/// notification deliveries, project tasks, access tokens, TeamSpeak clients), ascending and descending, on
/// rows whose order differs from key to key: the resulting order is asserted, and rows outside the scope
/// (another server, user, entry or project) never count.</summary>
public sealed class PagedSortKeyTests : IDisposable
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db;

    public PagedSortKeyTests()
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

    public static TheoryData<string?, bool, string> BlockedIpOrders => new()
    {
        { "IpAddress", false, "1,2,3" },
        { " ipaddress ", true, "3,2,1" },
        { "Protocol", false, "2,3,1" },
        { "protocol", true, "1,3,2" },
        { "Reason", false, "3,1,2" },
        { "reason", true, "2,1,3" },
        { "BlockedAt", false, "2,1,3" },
        { "blockedat", true, "3,1,2" },
        { null, false, "3,1,2" },
    };

    [Theory]
    [MemberData(nameof(BlockedIpOrders))]
    public async Task PortsentryRepository_GetBlockedIpsPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        _db.PortsentryBlockedIps.AddRange(
            new PortsentryBlockedIp { ServerId = 1, IpAddress = "10.0.0.1", Protocol = "udp", Reason = "m", BlockedAt = Base.AddDays(2) },
            new PortsentryBlockedIp { ServerId = 1, IpAddress = "10.0.0.2", Protocol = "icmp", Reason = "z", BlockedAt = Base.AddDays(1) },
            new PortsentryBlockedIp { ServerId = 1, IpAddress = "10.0.0.3", Protocol = "tcp", Reason = "a", BlockedAt = Base.AddDays(3) },
            new PortsentryBlockedIp { ServerId = 2, IpAddress = "10.0.0.9", Protocol = "tcp", Reason = "other", BlockedAt = Base });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, total) = await new PortsentryRepository(_db).GetBlockedIpsPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.IpAddress[^1..])));
    }

    public static TheoryData<string?, bool, string> PluginOrders => new()
    {
        { null, false, "alpha,beta,gamma" },
        { "Name", true, "gamma,beta,alpha" },
        { "Version", false, "beta,alpha,gamma" },
        { "Version", true, "gamma,alpha,beta" },
        { "Author", false, "gamma,beta,alpha" },
        { "Author", true, "alpha,beta,gamma" },
        { "Type", false, "gamma,alpha,beta" },
        { "Type", true, "beta,alpha,gamma" },
        { "Status", false, "beta,gamma,alpha" },
        { "Status", true, "alpha,gamma,beta" },
        { "CreatedAt", false, "beta,gamma,alpha" },
        { "CreatedAt", true, "alpha,gamma,beta" },
    };

    [Theory]
    [MemberData(nameof(PluginOrders))]
    public async Task PluginRepository_GetPageAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        PluginRegistration[] plugins =
        [
            new() { Name = "alpha", Version = "2.0", Author = "zoe", Type = PluginType.Collector, Status = PluginStatus.Disabled },
            new() { Name = "beta", Version = "1.0", Author = "max", Type = PluginType.Custom, Status = PluginStatus.Registered },
            new() { Name = "gamma", Version = "3.0", Author = "ann", Type = PluginType.Executor, Status = PluginStatus.Enabled },
        ];
        _db.PluginRegistrations.AddRange(plugins);
        await StampCreatedAtAsync(plugins, [3, 1, 2], (plugin, at) => plugin.CreatedAt = at);

        var (items, total) = await new PluginRepository(_db).GetPageAsync(
            null, sortBy, descending, 1, 10, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Name)));
    }

    public static TheoryData<string?, bool, string> EntryVersionOrders => new()
    {
        { null, false, "3,2,1" },
        { "Version", false, "1,2,3" },
        { "version", true, "3,2,1" },
        { "Key", false, "3,1,2" },
        { "key", true, "2,1,3" },
        { "Value", false, "2,3,1" },
        { "value", true, "1,3,2" },
        { "ChangeType", false, "2,1,3" },
        { "changetype", true, "3,1,2" },
        { "ChangedAt", false, "3,1,2" },
        { " changedat", true, "2,1,3" },
    };

    [Theory]
    [MemberData(nameof(EntryVersionOrders))]
    public async Task VariableLibraryRepository_GetEntryVersionsPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        _db.VariableLibraryEntryVersions.AddRange(
            new VariableLibraryEntryVersion { VariableLibraryEntryId = 1, Version = 1, Key = "b", Value = "z", ChangeType = ChangeType.Updated, ChangedAt = Base.AddDays(2) },
            new VariableLibraryEntryVersion { VariableLibraryEntryId = 1, Version = 2, Key = "c", Value = "x", ChangeType = ChangeType.Created, ChangedAt = Base.AddDays(3) },
            new VariableLibraryEntryVersion { VariableLibraryEntryId = 1, Version = 3, Key = "a", Value = "y", ChangeType = ChangeType.Deleted, ChangedAt = Base.AddDays(1) },
            new VariableLibraryEntryVersion { VariableLibraryEntryId = 2, Version = 9, Key = "other", Value = "o", ChangedAt = Base });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, total) = await new VariableLibraryRepository(_db).GetEntryVersionsPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Version)));
    }

    public static TheoryData<string?, bool, string> DeliveryOrders => new()
    {
        { null, false, "a,m,z" },
        { null, true, "z,m,a" },
        { "EventType", false, "z,m,a" },
        { "eventtype", true, "a,m,z" },
        { "ChannelName", false, "m,a,z" },
        { "channelname", true, "z,a,m" },
        { "Subject", false, "a,m,z" },
        { "subject", true, "z,m,a" },
        { "Status", false, "z,a,m" },
        { "status", true, "m,a,z" },
    };

    [Theory]
    [MemberData(nameof(DeliveryOrders))]
    public async Task UserNotificationRepository_GetDeliveriesPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        NotificationChannel email = new() { Name = "email" }, slack = new() { Name = "slack" }, teams = new() { Name = "teams" };
        _db.NotificationChannels.AddRange(email, slack, teams);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        NotificationDelivery[] deliveries =
        [
            new() { RecipientUserId = 1, ChannelId = email.Id, EventType = "build", Subject = "m", Status = NotificationDeliveryStatus.Failed },
            new() { RecipientUserId = 1, ChannelId = teams.Id, EventType = "alert", Subject = "z", Status = NotificationDeliveryStatus.Pending },
            new() { RecipientUserId = 1, ChannelId = slack.Id, EventType = "deploy", Subject = "a", Status = NotificationDeliveryStatus.Sent },
            new() { RecipientUserId = 2, ChannelId = email.Id, EventType = "other", Subject = "other" },
        ];
        _db.NotificationDeliveries.AddRange(deliveries);
        await StampCreatedAtAsync(deliveries, [2, 3, 1, 0], (delivery, at) => delivery.CreatedAt = at);

        var (items, total) = await new UserNotificationRepository(_db).GetDeliveriesPagedAsync(
            1, 1, 10, new UserNotificationPageRequest { SortBy = sortBy, SortDescending = descending },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Subject)));
    }

    public static TheoryData<string?, bool, string> ProjectTaskOrders => new()
    {
        { null, false, "zed,mid,alan" },
        { "Name", false, "alan,mid,zed" },
        { "Name", true, "zed,mid,alan" },
        { "ServerName", false, "zed,mid,alan" },
        { "ServerName", true, "alan,mid,zed" },
        { "Status", false, "zed,alan,mid" },
        { "Status", true, "mid,alan,zed" },
        { "ExitCode", false, "mid,alan,zed" },
        { "ExitCode", true, "zed,alan,mid" },
        { "CreatedAt", false, "alan,mid,zed" },
        { "CreatedAt", true, "zed,mid,alan" },
    };

    [Theory]
    [MemberData(nameof(ProjectTaskOrders))]
    public async Task ProjectRepository_GetTasksPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        Project project = new() { Name = "P1", Description = "d" }, other = new() { Name = "P2", Description = "d" };
        Server serverA = new() { Name = "s-a", Hostname = "a" }, serverB = new() { Name = "s-b", Hostname = "b" }, serverC = new() { Name = "s-c", Hostname = "c" };
        _db.Projects.AddRange(project, other);
        _db.Servers.AddRange(serverA, serverB, serverC);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Pipeline pipeline = new() { Name = "pipe", ProjectId = project.Id, YamlDefinition = "" }, otherPipeline = new() { Name = "pipe", ProjectId = other.Id, YamlDefinition = "" };
        _db.Pipelines.AddRange(pipeline, otherPipeline);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        PipelineRun run = new() { PipelineId = pipeline.Id }, otherRun = new() { PipelineId = otherPipeline.Id };
        _db.PipelineRuns.AddRange(run, otherRun);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ServerTask[] tasks =
        [
            new() { ServerId = serverB.Id, Name = "mid", Command = "echo", Status = TaskExecutionStatus.Failed, ExitCode = 0, PipelineRunId = run.Id },
            new() { ServerId = serverA.Id, Name = "zed", Command = "echo", Status = TaskExecutionStatus.Pending, ExitCode = 2, PipelineRunId = run.Id },
            new() { ServerId = serverC.Id, Name = "alan", Command = "echo", Status = TaskExecutionStatus.Success, ExitCode = 1, PipelineRunId = run.Id },
            new() { ServerId = serverA.Id, Name = "other", Command = "echo", PipelineRunId = otherRun.Id },
        ];
        _db.Tasks.AddRange(tasks);
        await StampCreatedAtAsync(tasks, [2, 3, 1, 4], (task, at) => task.CreatedAt = at);

        var repository = new ProjectRepository(_db, Substitute.For<Aetheus.Back.Components.Analysis.IProjectAnalysisGradeReader>());
        var (items, total) = await repository.GetTasksPagedAsync(
            project.Id, null, sortBy, descending, 1, 10, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Name)));
    }

    public static TheoryData<string?, bool, string> AccessTokenOrders => new()
    {
        { null, false, "zed,mid,alan" },
        { "Name", false, "alan,mid,zed" },
        { "name", true, "zed,mid,alan" },
        { "ExpiresAt", false, "zed,alan,mid" },
        { "expiresat", true, "mid,alan,zed" },
        { "LastUsedAt", false, "mid,zed,alan" },
        { "lastusedat", true, "alan,zed,mid" },
        { "CreatedAt", false, "alan,mid,zed" },
        { "createdat", true, "zed,mid,alan" },
    };

    [Theory]
    [MemberData(nameof(AccessTokenOrders))]
    public async Task PersonalAccessTokenRepository_GetForUserPagedAsync_OrdersBySortKey(string? sortBy, bool descending, string expected)
    {
        PersonalAccessToken[] tokens =
        [
            new() { UserId = 1, Name = "mid", TokenHash = "h1", TokenPrefix = "p1", ExpiresAt = Base.AddDays(30), LastUsedAt = Base.AddDays(1) },
            new() { UserId = 1, Name = "zed", TokenHash = "h2", TokenPrefix = "p2", ExpiresAt = Base.AddDays(10), LastUsedAt = Base.AddDays(2) },
            new() { UserId = 1, Name = "alan", TokenHash = "h3", TokenPrefix = "p3", ExpiresAt = Base.AddDays(20), LastUsedAt = Base.AddDays(3) },
            new() { UserId = 2, Name = "other", TokenHash = "h4", TokenPrefix = "p4", ExpiresAt = Base.AddDays(5) },
        ];
        _db.PersonalAccessTokens.AddRange(tokens);
        await StampCreatedAtAsync(tokens, [2, 3, 1, 4], (token, at) => token.CreatedAt = at);

        var (items, total) = await new PersonalAccessTokenRepository(_db).GetForUserPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Name)));
    }

    public static TheoryData<string?, bool, string> TeamspeakClientOrders => new()
    {
        { null, false, "alan,mid,zed" },
        { "Nickname", true, "zed,mid,alan" },
        { "ChannelId", false, "zed,alan,mid" },
        { "channelid", true, "mid,alan,zed" },
        { "ChannelName", false, "mid,zed,alan" },
        { "channelname", true, "alan,zed,mid" },
        { "Platform", false, "mid,alan,zed" },
        { " platform ", true, "zed,alan,mid" },
    };

    [Theory]
    [MemberData(nameof(TeamspeakClientOrders))]
    public async Task TeamspeakRepository_GetClientsPagedAsync_OrdersBySortKeyAndHidesServerQuery(string? sortBy, bool descending, string expected)
    {
        _db.TeamspeakClients.AddRange(
            new TeamspeakClient { ServerId = 1, ClientId = 1, Nickname = "mid", ChannelId = 3, ChannelName = "afk", Platform = "linux" },
            new TeamspeakClient { ServerId = 1, ClientId = 2, Nickname = "zed", ChannelId = 1, ChannelName = "lobby", Platform = "windows" },
            new TeamspeakClient { ServerId = 1, ClientId = 3, Nickname = "alan", ChannelId = 2, ChannelName = "music", Platform = "macos" },
            new TeamspeakClient { ServerId = 1, ClientId = 4, Nickname = "serveradmin", IsServerQuery = true },
            new TeamspeakClient { ServerId = 2, ClientId = 5, Nickname = "other" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, total) = await new TeamspeakRepository(_db).GetClientsPagedAsync(
            1, null, 1, 10, sortBy, descending, TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(expected, string.Join(",", items.Select(item => item.Nickname)));
    }
}
