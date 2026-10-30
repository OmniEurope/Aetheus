// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Certbot;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Certbot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-69 on a real PostgreSQL (the migration is applied for real): a certificate that reaches an alert lead time
/// raises its alert once however many checks run, the next lead time raises its own, and a renewed certificate
/// (a new expiry date, its row replaced by the heartbeat) alerts again. The notification pipeline is replaced by a
/// recorder: this proves the persistence and the once-only rule, not the delivery channels.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CertificateExpiryAlertIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Start = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed class ManualClock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(Utc, TimeSpan.Zero);
    }

    private static CertificateExpiryAlertService Sut(Aetheus.Back.Data.AppDbContext db, INotificationService notifications, TimeProvider clock) =>
        new(new CertificateExpiryAlertRepository(db), notifications, Substitute.For<IAuditService>(), clock,
            NullLogger<CertificateExpiryAlertService>.Instance);

    [Fact]
    public async Task TheAlertIsRaisedOncePerLeadTime_AndAgainForARenewedCertificate()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        var raised = new List<string>();
        var notifications = Substitute.For<INotificationService>();
        notifications.SendEventAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                raised.Add(System.Text.Json.JsonSerializer.Serialize(call[1]));
                return Task.CompletedTask;
            });
        var clock = new ManualClock(Start);

        await using (var seed = NewContext())
        {
            var organization = new Organization { Name = $"r69-{Guid.NewGuid():N}", Slug = $"r{Guid.NewGuid():N}"[..12] };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            var server = new Server { Name = "web-r69", Hostname = "web-r69.test", OrganizationId = organization.Id };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(ct);
            seed.CertbotCertificates.Add(new CertbotCertificate
            {
                ServerId = server.Id,
                Name = "shop.example.com",
                Domains = "[\"shop.example.com\"]",
                ExpiryDate = Start.AddDays(6.5)
            });
            await seed.SaveChangesAsync(ct);
        }

        // Three checks while the 7 day lead time is the only one reached: one alert.
        for (var check = 0; check < 3; check++)
        {
            await using var db = NewContext();
            await Sut(db, notifications, clock).RunAsync(ct);
            clock.Utc = clock.Utc.AddHours(6);
        }
        Assert.Single(raised);
        Assert.Contains("\"LeadDays\":7", raised[0], StringComparison.Ordinal);

        // 2.5 days later (about 3.5 days after the start): the 3 day lead time is reached, once.
        clock.Utc = Start.AddDays(3.6);
        for (var check = 0; check < 2; check++)
        {
            await using var db = NewContext();
            await Sut(db, notifications, clock).RunAsync(ct);
        }
        Assert.Equal(2, raised.Count);
        Assert.Contains("\"LeadDays\":3", raised[1], StringComparison.Ordinal);

        // The heartbeat replaces the certificate row; renewed, it expires 90 days on: it alerts again at 7 days.
        await using (var renew = NewContext())
        {
            await renew.CertbotCertificates.ExecuteDeleteAsync(ct);
            var serverId = await renew.Servers.Select(server => server.Id).SingleAsync(ct);
            renew.CertbotCertificates.Add(new CertbotCertificate
            {
                ServerId = serverId,
                Name = "shop.example.com",
                Domains = "[\"shop.example.com\"]",
                ExpiryDate = Start.AddDays(96)
            });
            await renew.SaveChangesAsync(ct);
        }
        clock.Utc = Start.AddDays(89.5);
        await using (var db = NewContext())
            await Sut(db, notifications, clock).RunAsync(ct);
        Assert.Equal(3, raised.Count);
        Assert.Contains("\"LeadDays\":7", raised[2], StringComparison.Ordinal);

        await using var verify = NewContext();
        // The marks of the replaced certificate (expired for over a month) are forgotten; the renewed one keeps its own.
        var mark = await verify.CertificateExpiryAlertMarks.SingleAsync(ct);
        Assert.Equal(7, mark.LeadDays);
        Assert.Equal(Start.AddDays(96), mark.ExpiryDate);
    }

    [Fact]
    public async Task TheAdministratorsLeadTimes_ArePersisted_AndReadBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        var notifications = Substitute.For<INotificationService>();

        await using (var db = NewContext())
            await Sut(db, notifications, TimeProvider.System).UpdateSettingsAsync(
                new UpdateCertificateAlertSettingsRequest { LeadDays = [1, 30, 7] }, ct);

        await using var read = NewContext();
        var settings = await Sut(read, notifications, TimeProvider.System).GetSettingsAsync(ct);
        Assert.Equal([30, 7, 1], settings.LeadDays);
        Assert.Equal(30, (await Sut(read, notifications, TimeProvider.System).GetExpiryWarningAsync(ct)).Days);
    }
}
