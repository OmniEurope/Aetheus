// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Certbot;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Certbot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Certbot;

/// <summary>R-69: certificate expiry alerts at lead times the administrator chose; EF InMemory with the real repository.</summary>
public sealed class CertificateExpiryAlertServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly List<(string EventType, string Json)> _sent = [];

    public CertificateExpiryAlertServiceTests()
    {
        _db.Servers.Add(new Server { Id = 1, Name = "web-1", Hostname = "web-1" });
        _notifications.SendEventAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sent.Add(((string)call[0], System.Text.Json.JsonSerializer.Serialize(call[1])));
                return Task.CompletedTask;
            });
    }

    public void Dispose() => _db.Dispose();

    private CertificateExpiryAlertService Sut() => new(
        new CertificateExpiryAlertRepository(_db), _notifications, Substitute.For<IAuditService>(), _clock,
        NullLogger<CertificateExpiryAlertService>.Instance);

    private async Task AddCertificateAsync(string name, double daysFromStart, int id = 0)
    {
        _db.CertbotCertificates.Add(new CertbotCertificate
        {
            Id = id,
            ServerId = 1,
            Name = name,
            Domains = "[\"" + name + "\",\"www." + name + "\"]",
            ExpiryDate = Start.UtcDateTime.AddDays(daysFromStart)
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SaveLeadTimesAsync(params int[] leadDays) =>
        await Sut().UpdateSettingsAsync(new UpdateCertificateAlertSettingsRequest { LeadDays = [.. leadDays] }, TestContext.Current.CancellationToken);

    private Task<int> RunAsync() => Sut().RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task WithoutASavedList_TheDefaultLeadTimesApply_SevenThreeAndOne()
    {
        var settings = await Sut().GetSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([7, 3, 1], settings.LeadDays);
    }

    [Fact]
    public async Task ACertificateFarFromItsExpiry_RaisesNothing()
    {
        await AddCertificateAsync("far.example.com", 40);

        Assert.Equal(0, await RunAsync());
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task CrossingTheSevenDayLeadTime_RaisesOneAlertCarryingTheCertificate()
    {
        await AddCertificateAsync("shop.example.com", 6.5);

        Assert.Equal(1, await RunAsync());

        var (eventType, json) = Assert.Single(_sent);
        Assert.Equal("certbot.certificate-expiring", eventType);
        Assert.Contains("\"CertificateName\":\"shop.example.com\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ServerName\":\"web-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"LeadDays\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"DaysRemaining\":6", json, StringComparison.Ordinal);
        Assert.Contains("shop.example.com, www.shop.example.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameLeadTime_IsNeverRaisedTwice_AcrossRepeatedChecks()
    {
        await AddCertificateAsync("shop.example.com", 6.5);

        await RunAsync();
        _clock.Advance(TimeSpan.FromHours(6));
        await RunAsync();
        _clock.Advance(TimeSpan.FromHours(6));
        await RunAsync();

        Assert.Single(_sent);
    }

    [Fact]
    public async Task EachLaterLeadTime_RaisesItsOwnAlert_AsTheExpiryApproaches()
    {
        await AddCertificateAsync("shop.example.com", 6.5);
        await RunAsync();

        _clock.Advance(TimeSpan.FromDays(3.6)); // 2.9 days left
        await RunAsync();
        _clock.Advance(TimeSpan.FromDays(2.4)); // 0.5 day left
        await RunAsync();

        Assert.Equal([7, 3, 1], _sent.Select(alert => System.Text.Json.JsonDocument.Parse(alert.Json).RootElement.GetProperty("LeadDays").GetInt32()));
    }

    [Fact]
    public async Task SeveralLeadTimesReachedAtOnce_RaiseOneAlertForTheNearestOne_AndMarkThemAll()
    {
        // First seen with 2.5 days left: the 7 and 3 day lead times are both reached.
        await AddCertificateAsync("late.example.com", 2.5);

        await RunAsync();
        _clock.Advance(TimeSpan.FromHours(6));
        await RunAsync();

        var alert = Assert.Single(_sent);
        Assert.Contains("\"LeadDays\":3", alert.Json, StringComparison.Ordinal);
        Assert.Equal(2, await _db.CertificateExpiryAlertMarks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARenewedCertificate_WithANewExpiryDate_AlertsAgain()
    {
        await AddCertificateAsync("shop.example.com", 6.5);
        await RunAsync();
        Assert.Single(_sent);

        // Renewed 80 days later, then close to its new expiry: the heartbeat replaced the row, the date changed.
        _clock.Advance(TimeSpan.FromDays(6));
        var old = await _db.CertbotCertificates.SingleAsync(TestContext.Current.CancellationToken);
        _db.CertbotCertificates.Remove(old);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await AddCertificateAsync("shop.example.com", 6 + 89);
        _clock.Advance(TimeSpan.FromDays(83)); // 6 days left on the new certificate
        await RunAsync();

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task AnAdministratorChosenList_ReplacesTheDefault_AndEmptyMeansNoAlert()
    {
        await AddCertificateAsync("shop.example.com", 20);
        await SaveLeadTimesAsync(30, 14);

        await RunAsync();
        Assert.Single(_sent);
        Assert.Contains("\"LeadDays\":30", _sent[0].Json, StringComparison.Ordinal);

        await SaveLeadTimesAsync();
        await AddCertificateAsync("other.example.com", 0.5);
        await RunAsync();
        Assert.Single(_sent);
    }

    [Fact]
    public async Task AnExpiredCertificate_IsNotAlertedAbout()
    {
        await AddCertificateAsync("gone.example.com", -2);

        Assert.Equal(0, await RunAsync());
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(-3)]
    public async Task ALeadTimeOutOfRange_IsRefused(int days)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => SaveLeadTimesAsync(7, days));
    }

    [Fact]
    public async Task ADuplicatedLeadTime_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => SaveLeadTimesAsync(7, 3, 7));
    }

    [Fact]
    public async Task TheSavedList_IsReadBackLongestFirst()
    {
        await SaveLeadTimesAsync(1, 14, 3);

        var settings = await Sut().GetSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([14, 3, 1], settings.LeadDays);
    }

    [Fact]
    public async Task TheExpiryWarning_IsTheLongestLeadTime_ThirtyDaysWhenNoAlertIsConfigured()
    {
        Assert.Equal(7, (await Sut().GetExpiryWarningAsync(TestContext.Current.CancellationToken)).Days);

        await SaveLeadTimesAsync(45, 10);
        Assert.Equal(45, (await Sut().GetExpiryWarningAsync(TestContext.Current.CancellationToken)).Days);

        await SaveLeadTimesAsync();
        Assert.Equal(CertificateAlertLeadTimes.FallbackWarningDays, (await Sut().GetExpiryWarningAsync(TestContext.Current.CancellationToken)).Days);
    }

    [Fact]
    public async Task TheMarksOfCertificatesLongExpired_AreForgotten()
    {
        await AddCertificateAsync("shop.example.com", 6.5);
        await RunAsync();
        Assert.Equal(1, await _db.CertificateExpiryAlertMarks.CountAsync(TestContext.Current.CancellationToken));

        _clock.Advance(TimeSpan.FromDays(6.5 + CertificateExpiryAlertService.MarkRetentionDays + 1));
        await RunAsync();

        Assert.Equal(0, await _db.CertificateExpiryAlertMarks.CountAsync(TestContext.Current.CancellationToken));
    }
}
