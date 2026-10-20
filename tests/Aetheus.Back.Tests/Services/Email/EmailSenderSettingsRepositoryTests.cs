// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>The mail domain and mailbox the default sender is built from.</summary>
public sealed class EmailSenderSettingsRepositoryTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero)));
    private readonly EmailSenderSettingsRepository _sut;

    public EmailSenderSettingsRepositoryTests() => _sut = new EmailSenderSettingsRepository(_db);

    public void Dispose() => _db.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Seed()
    {
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "adopted", Hostname = "old-host", IpAddress = "10.0.0.1" },
            new Server { Id = 2, Name = "vps", Hostname = "vps-host", IpAddress = "10.0.0.2" });
        _db.MailStates.AddRange(
            new MailState { Id = 1, ServerId = 1, IsManagedByAetheus = false, Hostname = "mx.old.org" },
            new MailState { Id = 2, ServerId = 2, IsManagedByAetheus = true, Hostname = "" });
        _db.MailDomains.AddRange(
            new MailDomain { Id = 1, ServerId = 1, Name = "old.org" },
            new MailDomain { Id = 2, ServerId = 2, Name = "gone.org", MissingSince = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
            new MailDomain { Id = 3, ServerId = 2, Name = "example.org" });
        _db.MailAccounts.AddRange(
            new MailAccount { Id = 10, MailDomainId = 3, Email = "No-Reply@Example.org", IsActive = true },
            new MailAccount { Id = 11, MailDomainId = 1, Email = "no-reply@old.org", IsActive = false });
        _db.AppSettings.AddRange(
            new AppSetting { Key = EmailSenderSettingKeys.Host, Value = "smtp.x" },
            new AppSetting { Key = "Theme", Value = "dark" });
        _db.SaveChanges();
    }

    [Fact]
    public async Task DefaultDomain_PrefersTheStackAetheusManages_AndFallsBackToTheServerHostname()
    {
        Seed();

        var domain = await _sut.FindDefaultMailDomainAsync(Ct);

        Assert.Equal(new ManagedMailDomain(2, "example.org", "vps-host"), domain);
    }

    [Fact]
    public async Task DefaultDomain_IsNullWithoutAnyMailStack()
    {
        _db.Servers.Add(new Server { Id = 1, Name = "s" });
        _db.MailDomains.Add(new MailDomain { Id = 1, ServerId = 1, Name = "example.org" });
        await _db.SaveChangesAsync(Ct);

        Assert.Null(await _sut.FindDefaultMailDomainAsync(Ct));
    }

    [Fact]
    public async Task Mailbox_IsFoundCaseInsensitively_AndOnlyWhenActive()
    {
        Seed();

        Assert.Equal(10, await _sut.FindActiveMailboxIdAsync("no-reply@example.org", Ct));
        Assert.Null(await _sut.FindActiveMailboxIdAsync("no-reply@old.org", Ct));
    }

    [Fact]
    public async Task Settings_AreOnlyTheEmailSenderRows()
    {
        Seed();

        var settings = await _sut.GetSettingsAsync(Ct);

        Assert.Equal("smtp.x", Assert.Single(settings).Value);
    }
}
