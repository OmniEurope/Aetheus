// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>The six-hourly DNS re-verification of mail domains: selection, per-server context, errors, loop.</summary>
public sealed class MailDnsRefreshServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly IMailInventoryRepository _repo = Substitute.For<IMailInventoryRepository>();
    private readonly IMailDnsVerifier _verifier = Substitute.For<IMailDnsVerifier>();
    private readonly IServerRepository _servers = Substitute.For<IServerRepository>();
    private readonly RecordingLogger<MailDnsRefreshService> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MailDnsRefreshService Sut(IPostgresLeaderLease? lease = null) => new(
        BackgroundServiceTestKit.ScopeFactory(
            (typeof(IMailInventoryRepository), _repo), (typeof(IMailDnsVerifier), _verifier), (typeof(IServerRepository), _servers)),
        _clock, _logger, lease);

    private void Due(params MailDomain[] domains) =>
        _repo.GetTrackedDomainsDueForDnsCheckAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(domains.ToList());

    private static MailDomain Domain(int id, int serverId) => new() { Id = id, ServerId = serverId, Name = $"d{id}.example.org" };

    [Fact]
    public async Task Refresh_AsksForDomainsNotCheckedForSixHours_FiftyAtATime()
    {
        Due();

        var count = await Sut().RefreshAsync(Ct);

        Assert.Equal(0, count);
        await _repo.Received(1).GetTrackedDomainsDueForDnsCheckAsync(
            Start.UtcDateTime.AddHours(-6), 50, Arg.Any<CancellationToken>());
        // Nothing due: no server lookup, no write.
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await _repo.DidNotReceiveWithAnyArgs().GetStateAsync(default, Ct);
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task Refresh_CutoffFollowsTheInjectedClock()
    {
        Due();
        _clock.Advance(TimeSpan.FromDays(2));

        await Sut().RefreshAsync(Ct);

        await _repo.Received(1).GetTrackedDomainsDueForDnsCheckAsync(
            Start.UtcDateTime.AddDays(2).AddHours(-6), 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_VerifiesEachDomain_AgainstItsServerHostnameAndAddress_ThenSavesOnce()
    {
        var first = Domain(1, serverId: 10);
        var second = Domain(2, serverId: 20);
        Due(first, second);
        _repo.GetStateAsync(10, Arg.Any<CancellationToken>()).Returns(new MailState { ServerId = 10, Hostname = "mail10.example.org" });
        _repo.GetStateAsync(20, Arg.Any<CancellationToken>()).Returns(new MailState { ServerId = 20, Hostname = "mail20.example.org" });
        _servers.FindServerAsync(10, Arg.Any<CancellationToken>()).Returns(new Server { Id = 10, IpAddress = "192.0.2.10" });
        _servers.FindServerAsync(20, Arg.Any<CancellationToken>()).Returns(new Server { Id = 20, IpAddress = "192.0.2.20" });

        var count = await Sut().RefreshAsync(Ct);

        Assert.Equal(2, count);
        await _verifier.Received(1).VerifyAsync(first, "mail10.example.org", "192.0.2.10", Arg.Any<CancellationToken>());
        await _verifier.Received(1).VerifyAsync(second, "mail20.example.org", "192.0.2.20", Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Contains(_logger.Records, entry => entry.Level == LogLevel.Information && entry.Message.Contains("2 domains"));
    }

    [Fact]
    public async Task Refresh_LooksAServerUpOnce_WhenSeveralDomainsShareIt()
    {
        Due(Domain(1, 10), Domain(2, 10), Domain(3, 10));

        await Sut().RefreshAsync(Ct);

        await _repo.Received(1).GetStateAsync(10, Arg.Any<CancellationToken>());
        await _servers.Received(1).FindServerAsync(10, Arg.Any<CancellationToken>());
        await _verifier.ReceivedWithAnyArgs(3).VerifyAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task Refresh_PassesNoHostnameNorAddress_WhenTheServerOrItsMailStateIsGone()
    {
        var domain = Domain(1, 10);
        Due(domain);
        _repo.GetStateAsync(10, Arg.Any<CancellationToken>()).Returns((MailState?)null);
        _servers.FindServerAsync(10, Arg.Any<CancellationToken>()).Returns((Server?)null);

        await Sut().RefreshAsync(Ct);

        await _verifier.Received(1).VerifyAsync(domain, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_OneFailingDomain_DoesNotStopTheOthers_NorTheirSave_AndIsLogged()
    {
        var broken = Domain(1, 10);
        var healthy = Domain(2, 10);
        Due(broken, healthy);
        _verifier.VerifyAsync(broken, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("resolver exploded"));

        var count = await Sut().RefreshAsync(Ct);

        Assert.Equal(1, count);
        await _verifier.Received(1).VerifyAsync(healthy, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        var warning = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("1", warning.Message);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public async Task Refresh_Cancellation_Propagates_WithoutSaving()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Due(Domain(1, 10), Domain(2, 10));
        _verifier.VerifyAsync(Arg.Any<MailDomain>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<MailDnsCheckDto>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => Sut().RefreshAsync(cts.Token));

        await _verifier.ReceivedWithAnyArgs(1).VerifyAsync(default!, default, default, Ct);
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        Assert.Empty(_logger.Records);
    }

    [Fact]
    public async Task Loop_RunsEverySixHours_LogsAFailedPass_AndKeepsGoing()
    {
        var passes = new SemaphoreSlim(0);
        var calls = 0;
        _repo.GetTrackedDomainsDueForDnsCheckAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var call = Interlocked.Increment(ref calls);
                passes.Release();
                return call == 1 ? throw new InvalidOperationException("database down") : Task.FromResult(new List<MailDomain>());
            });
        var (lease, started) = BackgroundServiceTestKit.LeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        await started.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, calls);
        _clock.Advance(TimeSpan.FromHours(1));
        await passes.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromHours(6));
        await passes.AwaitAsync(Ct);
        await sut.StopAsync(Ct);

        Assert.Equal(2, calls);
        var error = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        Assert.Contains("mail DNS refresh", error.Message);
    }

    [Fact]
    public async Task WithALease_RunsUnderTheMailDnsRefreshLease_AndNothingWhenNotLeader()
    {
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        Assert.Equal("aetheus:mail-dns-refresh", await leaseName.NameAsync(Ct));
        _clock.Advance(TimeSpan.FromHours(12));
        await sut.StopAsync(Ct);

        await _repo.DidNotReceiveWithAnyArgs().GetTrackedDomainsDueForDnsCheckAsync(default, default, Ct);
    }
}
