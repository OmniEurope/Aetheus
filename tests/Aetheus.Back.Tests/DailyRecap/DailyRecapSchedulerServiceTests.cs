// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>The scheduler's own job: a check every minute that runs the recap service, under the leader lease.</summary>
public sealed class DailyRecapSchedulerServiceTests : IDisposable
{
    // 07:00 in Paris (summer time) on 2026-10-13.
    private static readonly DateTimeOffset SendTime = new(2026, 10, 13, 5, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(SendTime.AddMinutes(-1));
    private readonly string _database = Guid.NewGuid().ToString("N");
    private readonly List<AppDbContext> _contexts = [];
    private readonly IEmailSender _sender = Substitute.For<IEmailSender>();
    private readonly RecordingLogger<DailyRecapSchedulerService> _logger = new();

    public DailyRecapSchedulerServiceTests()
    {
        _sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<EmailMessage>>().Select(_ => EmailSendResult.Sent("no-reply@example.org")).ToList());

        using var db = Context();
        db.Roles.Add(new Role { Id = 1, Name = "Admin" });
        db.Users.Add(new User { Id = 1, Username = "alice", Email = "alice@example.org", IsActive = true });
        db.UserRoles.Add(new UserRole { UserId = 1, RoleId = 1 });
        db.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AppDbContext Context()
    {
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, _clock);
        _contexts.Add(context);
        return context;
    }

    private DailyRecapService RealService()
    {
        var repo = new DailyRecapRepository(Context());
        var compatibility = Substitute.For<IAgentCompatibilityPolicy>();
        compatibility.Evaluate(Arg.Any<ServerDto>()).Returns(new AgentCompatibilityDto());
        return new DailyRecapService(repo, new DailyRecapCollector(repo, compatibility, new ConfigurationBuilder().Build()), _sender, NotificationMailKit.Templates(),
            Substitute.For<IAuditService>(), _clock,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cors:Origins:0"] = "https://app.example.org" }).Build(),
            NullLogger<DailyRecapService>.Instance);
    }

    private DailyRecapSchedulerService Sut(Func<DailyRecapService> service, IPostgresLeaderLease? lease = null) =>
        new(BackgroundServiceTestKit.ScopeFactoryOf(typeof(DailyRecapService), () => service()), _logger, _clock, lease);

    private void ReachSendTime() => _clock.SetUtcNow(SendTime);

    [Fact]
    public void TheCheckRunsEveryMinute() =>
        Assert.Equal(TimeSpan.FromMinutes(1), DailyRecapSchedulerService.CheckInterval);

    [Fact]
    public async Task Check_RunsTheScheduledRecap_WhichSendsOncePerDay()
    {
        ReachSendTime();
        var sut = Sut(RealService);

        await sut.CheckAsync(Ct);
        await _sender.Received(1).SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromMinutes(1));
        await sut.CheckAsync(Ct);
        await _sender.Received(1).SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_SendsNothing_BeforeTheSendTime()
    {
        await Sut(RealService).CheckAsync(Ct);

        await _sender.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, Ct);
    }

    [Fact]
    public async Task Check_SendsNothing_WhenTheRecapIsSwitchedOff()
    {
        ReachSendTime();
        await new DailyRecapRepository(Context()).SaveSettingRowsAsync(
            new Dictionary<string, string> { [DailyRecapSettingKeys.Enabled] = "false" }, SendTime.UtcDateTime, Ct);

        await Sut(RealService).CheckAsync(Ct);

        await _sender.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, Ct);
    }

    [Fact]
    public async Task Loop_ChecksEveryMinute_LogsAFailedCheck_AndKeepsGoing()
    {
        ReachSendTime();
        var resolutions = new SemaphoreSlim(0);
        var calls = 0;
        DailyRecapService Resolve()
        {
            var call = Interlocked.Increment(ref calls);
            resolutions.Release();
            return call == 1 ? throw new InvalidOperationException("service unavailable") : RealService();
        }

        var (lease, started) = BackgroundServiceTestKit.LeaderLease();
        var sut = Sut(Resolve, lease);

        await sut.StartAsync(Ct);
        await started.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await resolutions.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await resolutions.AwaitAsync(Ct);
        await sut.StopAsync(Ct);

        Assert.Equal(2, calls);
        var error = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        Assert.Contains("daily recap check", error.Message);
        Assert.IsType<InvalidOperationException>(error.Exception);
        // The check that followed the failed one ran the recap for real.
        await _sender.Received(1).SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithALease_RunsUnderTheDailyRecapLease_AndNothingWhenNotLeader()
    {
        ReachSendTime();
        var resolved = false;
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(() =>
        {
            resolved = true;
            return RealService();
        }, lease);

        await sut.StartAsync(Ct);
        Assert.Equal("aetheus:daily-recap", await leaseName.NameAsync(Ct));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await sut.StopAsync(Ct);

        Assert.False(resolved);
        await _sender.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, Ct);
    }
}
