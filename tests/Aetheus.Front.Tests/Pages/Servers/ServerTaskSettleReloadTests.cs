// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-119: a page whose data is read from the backend (not pushed with the server state) reads it again
/// when the agent's new state has arrived, and does so BEFORE the task veil lifts, so the veil never lifts on what
/// the task has just changed. Each test follows a task on the section, ends it, delivers the server's next state, and
/// compares the number of reads of the page's data at the moment the veil lifts with the number before the state.
/// The firewall has its own test in <see cref="ServerTaskVeilTests"/>.
/// </summary>
public sealed class ServerTaskSettleReloadTests : BunitContext
{
    private const int Sid = 10;
    private readonly BunitTestHelper.TestHandler _handler;
    private ServerDetailLoader? _loader;

    public ServerTaskSettleReloadTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        Services.AddScoped(sp => new ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance));
        _handler.SetJsonResponse(HttpMethod.Get, "api/monitoring/servers/", new List<ServerMetricDto>());
    }

    private ServerDetailLoader Loader => _loader ??= Services.GetRequiredService<ServerDetailLoader>();

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private async Task LoadServerAsync(ServerDetailDto server)
    {
        _handler.SetJsonResponse($"api/servers/{Sid}", server with { Id = Sid });
        await Loader.EnsureLoadedAsync(Sid, Xunit.TestContext.Current.CancellationToken);
    }

    private int Reads(string urlPart) =>
        _handler.RequestDetails.Count(r => r.Method == "GET" && r.Url.Contains(urlPart, StringComparison.Ordinal));

    private static ServerTaskFollower FollowerOf(object section)
    {
        for (var type = section.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField("_follower", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetValue(section) is ServerTaskFollower follower)
                return follower;
        }
        throw new InvalidOperationException($"{section.GetType().Name} has no follower.");
    }

    /// <summary>Follows a task on the section, ends it, delivers the server's next state, and returns the reads of the
    /// page's data before that state and at the moment the veil lifted.</summary>
    private async Task<(int Before, int AtLift)> SettleAsync(IRenderedComponent<IComponent> cut, object section, int taskId, Func<int> reads)
    {
        // The page has read its data once, and nothing else is in flight.
        cut.WaitForAssertion(() => Assert.True(reads() >= 1, "the page never read its data"));
        await Task.Delay(150, Xunit.TestContext.Current.CancellationToken);
        var atLift = -1;
        Loader.OnChanged += () =>
        {
            if (!Loader.TaskPending && atLift < 0) atLift = reads();
        };

        await cut.InvokeAsync(() => FollowerOf(section).Follow("Task", taskId));
        await cut.InvokeAsync(() => Tracker.HandleTerminal(
            new TaskCompletedNotification { TaskId = taskId, ServerId = Sid, TaskName = "Task", Status = TaskExecutionStatus.Success }));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));
        Assert.True(Loader.TaskPending, "the agent's new state has not arrived yet");
        var before = reads();

        await Task.Delay(30, Xunit.TestContext.Current.CancellationToken);
        await Loader.RefreshAsync(Sid, Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        return (before, atLift);
    }

    private static void AssertReadBeforeTheVeilLifted((int Before, int AtLift) result) =>
        Assert.True(result.AtLift > result.Before,
            $"the page read its data {result.Before} time(s) before the new state and {result.AtLift} when the veil lifted: it must read it again before");

    [Fact]
    public async Task TheUpdatesPage_ReadsThePendingUpdatesAgain_BeforeTheVeilLifts()
    {
        await LoadServerAsync(new ServerDetailDto { Id = Sid, Name = "srv", OsDescription = "Debian GNU/Linux 12" });
        _handler.SetJsonResponse(HttpMethod.Get, "security-updates", new ServerSecurityUpdatesDto
        {
            Known = true,
            PackageManagerPresent = true,
            PatchManagementAvailable = true,
            CheckedAt = DateTime.Now,
            Updates = []
        });
        var cut = Render<ServerPatchSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, Sid)
            .Add(x => x.Server, Loader.Server)
            .Add(x => x.PatchManagementAvailable, true));

        var result = await SettleAsync(cut, cut.Instance, 51001, () => Reads("security-updates"));

        AssertReadBeforeTheVeilLifted(result);
    }

    [Fact]
    public async Task ThePortsentryPage_ReadsTheBlockedAddressesAgain_BeforeTheVeilLifts()
    {
        await LoadServerAsync(new ServerDetailDto { Id = Sid, Name = "srv" });
        _handler.SetJsonResponse("portsentry/filter-values", new PortsentryFilterValuesDto());
        _handler.SetJsonResponse($"api/servers/{Sid}/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        _handler.SetJsonResponse($"api/servers/{Sid}/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
        var cut = Render<ServerPortsentrySection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, Sid)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true, Version = "2.0", Mode = "atcp" }));

        var result = await SettleAsync(cut, cut.Instance, 51002, () => Reads("portsentry/blocked"));

        AssertReadBeforeTheVeilLifted(result);
    }

    [Fact]
    public async Task TheRkhunterPage_ReadsTheResultsAgain_BeforeTheVeilLifts()
    {
        await LoadServerAsync(new ServerDetailDto { Id = Sid, Name = "srv" });
        _handler.SetJsonResponse($"api/servers/{Sid}/rkhunter/warnings", new List<RkhunterWarningDto>());
        _handler.SetJsonResponse($"api/servers/{Sid}/rkhunter/history", new List<RkhunterScanResultDto>());
        var cut = Render<ServerRkhunterSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, Sid)
            .Add(x => x.Rk, new RkhunterDataDto { IsInstalled = true, Version = "1.4.6", ScanScheduleCron = "0 3 * * *" }));

        var result = await SettleAsync(cut, cut.Instance, 51003, () => Reads("rkhunter/warnings"));

        AssertReadBeforeTheVeilLifted(result);
    }

    [Fact]
    public async Task TheTeamspeakPage_ReadsItsStateAgain_BeforeTheVeilLifts()
    {
        var server = new ServerDetailDto
        {
            Id = Sid,
            Name = "ts-srv",
            Teamspeak = new TeamspeakDataDto { IsInstalled = true, IsRunning = true, Version = "3.13.7", ServerName = "My TS", VoicePort = 9987, QueryPort = 10011, MaxClients = 32 }
        };
        await LoadServerAsync(server);
        _handler.SetJsonResponse($"api/servers/{Sid}/teamspeak", server.Teamspeak);
        _handler.SetPaginatedJsonResponse($"api/servers/{Sid}/teamspeak/clients", Array.Empty<TeamspeakClientDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{Sid}/teamspeak/channels", Array.Empty<TeamspeakChannelDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{Sid}/teamspeak/bans", Array.Empty<TeamspeakBanDto>());
        var cut = Render<ServerTeamspeakSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, Sid)
            .Add(x => x.Server, Loader.Server!));

        var result = await SettleAsync(cut, cut.Instance, 51004, () => Reads("teamspeak/clients"));

        AssertReadBeforeTheVeilLifted(result);
    }

    [Fact]
    public async Task TheMailPage_ReadsTheStackStatusAgain_BeforeTheVeilLifts()
    {
        await LoadServerAsync(ServerTestData.MakeMailServer());
        _handler.SetJsonResponse($"api/servers/{Sid}/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "example.com", IsActive = true, DkimSelector = "default" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse($"api/servers/{Sid}/mail/accounts", new PaginatedResult<MailAccountDto>
        {
            Items = [new() { Id = 1, Email = "admin@example.com", IsActive = true, QuotaMb = 1024 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse($"api/servers/{Sid}/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse($"api/servers/{Sid}/mail/diagnostics", new MailDiagnosticsDto());
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"/servers/{Sid}/mail");
        var cut = Render<ServerMailSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, Sid)
            .Add(x => x.Server, Loader.Server!));

        var result = await SettleAsync(cut, cut.Instance, 51005, () => Reads("mail/diagnostics"));

        AssertReadBeforeTheVeilLifted(result);
    }
}
