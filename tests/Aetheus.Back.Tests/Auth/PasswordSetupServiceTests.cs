// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Back.Services.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

/// <summary>
/// Recette R-14: the welcome mail carries a single-use link (never a password), the token is stored only as
/// its keyed hash, sets the password once, is refused a second time and after its lifetime, and a mail that
/// does not leave is said as such. Real renderer and templates over the InMemory store; only SMTP is replaced.
/// </summary>
public sealed class PasswordSetupServiceTests : IDisposable
{
    private const string SigningKey = "unit-test-signing-key-least-32-bytes!!";
    private const string Front = "https://aetheus.example.org";
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly AppDbContext _db;
    private readonly IEmailSender _sender = Substitute.For<IEmailSender>();
    private readonly List<EmailMessage> _sent = [];

    public PasswordSetupServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, _clock);
        _sender.SendAsync(Arg.Do<EmailMessage>(_sent.Add), Arg.Any<CancellationToken>())
            .Returns(EmailSendResult.Sent("no-reply@example.org"));
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PasswordSetupService Service(string? frontOrigin = Front)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cors:FrontendUrl"] = frontOrigin })
            .Build();
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))
        });
        return new PasswordSetupService(
            new PasswordSetupRepository(_db),
            new AuthRepository(_db, _clock),
            new MailTemplateRenderer(new MailTemplateRepository(_db), NullLogger<MailTemplateRenderer>.Instance),
            _sender,
            new JwtOptions { SigningKey = SigningKey },
            _clock,
            configuration,
            http,
            new MemoryCache(new MemoryCacheOptions()),
            _audit,
            Substitute.For<IDomainEventDispatcher>(),
            _queue,
            NullLogger<PasswordSetupService>.Instance);
    }

    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IBackgroundTaskQueue _queue = Substitute.For<IBackgroundTaskQueue>();

    private async Task<User> SeedUserAsync(string adminTypedPassword = "Admin-Typed-Pwd-2026!")
    {
        var user = new User
        {
            Username = "jdupont",
            Email = "jean.dupont@example.org",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminTypedPassword),
            MustChangePassword = true,
            IsActive = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(Ct);
        return user;
    }

    private static readonly Regex Link = new(@"/login/set-password#token=([A-Za-z0-9_\-]+)");

    private static string TokenIn(EmailMessage mail) => Link.Match(mail.HtmlBody!).Groups[1].Value;

    [Fact]
    public async Task SendWelcome_MailsTheAddress_ALinkWhoseTokenIsStoredOnlyAsItsHash_AndNoPassword()
    {
        var user = await SeedUserAsync("Admin-Typed-Pwd-2026!");

        var result = await Service().SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);

        Assert.Equal(WelcomeMailStatus.Sent, result.Status);
        Assert.Equal(user.Email, result.To);
        var mail = Assert.Single(_sent);
        Assert.Equal(user.Email, mail.To);
        var token = TokenIn(mail);
        Assert.Equal(43, token.Length); // 32 random bytes, base64url
        Assert.Contains(Front + "/login/set-password#token=" + token, mail.TextBody, StringComparison.Ordinal);
        var row = await _db.PasswordSetupTokens.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(AuthTokenHelper.HashToken(SigningKey, token), row.TokenHash);
        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(Now.UtcDateTime + PasswordSetupService.WelcomeLifetime, row.ExpiresAt);
        Assert.Null(row.UsedAt);
        Assert.DoesNotContain("Admin-Typed-Pwd-2026!", mail.Subject + mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(user.Email!, mail.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_SetsThePasswordOnce_AndRefusesTheSameTokenTheSecondTime()
    {
        var user = await SeedUserAsync();
        var service = Service();
        await service.SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);
        var token = TokenIn(Assert.Single(_sent));
        var stampBefore = user.SecurityStamp;

        Assert.True(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = token, NewPassword = "Chosen-By-Jean-2026!" }, Ct));
        Assert.False(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = token, NewPassword = "Second-Try-Pwd-2026!" }, Ct));

        var stored = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct);
        Assert.True(BCrypt.Net.BCrypt.Verify("Chosen-By-Jean-2026!", stored.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("Second-Try-Pwd-2026!", stored.PasswordHash));
        Assert.False(stored.MustChangePassword);
        Assert.NotEqual(stampBefore, stored.SecurityStamp);
        Assert.NotNull((await _db.PasswordSetupTokens.AsNoTracking().SingleAsync(Ct)).UsedAt);
    }

    [Fact]
    public async Task Complete_AfterTheLifetime_IsRefused_AndThePasswordIsUnchanged()
    {
        var user = await SeedUserAsync("Admin-Typed-Pwd-2026!");
        var service = Service();
        await service.SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);
        var token = TokenIn(Assert.Single(_sent));

        _clock.Advance(PasswordSetupService.WelcomeLifetime + TimeSpan.FromSeconds(1));

        Assert.False(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = token, NewPassword = "Too-Late-Pwd-2026!" }, Ct));
        var stored = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct);
        Assert.True(BCrypt.Net.BCrypt.Verify("Admin-Typed-Pwd-2026!", stored.PasswordHash));
    }

    [Fact]
    public async Task Complete_AnUnknownToken_OrADeactivatedAccount_IsRefused()
    {
        var user = await SeedUserAsync();
        var service = Service();
        await service.SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);
        var token = TokenIn(Assert.Single(_sent));

        Assert.False(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = "not-a-token", NewPassword = "Whatever-Pwd-2026!" }, Ct));
        user.IsActive = false;
        await _db.SaveChangesAsync(Ct);
        Assert.False(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = token, NewPassword = "Whatever-Pwd-2026!" }, Ct));
    }

    [Fact]
    public async Task SendWelcome_WithoutAPublicAddress_SendsNothing_AndSaysSo()
    {
        var user = await SeedUserAsync();

        var result = await Service(frontOrigin: null).SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);

        Assert.Equal(WelcomeMailStatus.NotConfigured, result.Status);
        Assert.Equal(PasswordSetupService.NoFrontAddressMessage, result.Message);
        Assert.Equal(WelcomeMailReason.NoPublicAddress, result.Reason);
        Assert.Empty(_sent);
        Assert.False(await _db.PasswordSetupTokens.AnyAsync(Ct));
    }

    [Fact]
    public async Task SendWelcome_WhenSmtpRefuses_ReportsFailedWithTheServersText()
    {
        var user = await SeedUserAsync();
        _sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(EmailSendResult.Failed("550 mailbox unavailable"));

        var result = await Service().SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);

        Assert.Equal(WelcomeMailStatus.Failed, result.Status);
        Assert.Equal("550 mailbox unavailable", result.Message);
        Assert.Equal(WelcomeMailReason.None, result.Reason);
    }

    [Fact]
    public async Task SendWelcome_WithoutASender_ReportsNotConfiguredWithTheNoSenderReason()
    {
        var user = await SeedUserAsync();
        _sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(EmailSendResult.NotConfigured("No e-mail sender: the mailbox does not exist yet"));

        var result = await Service().SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);

        Assert.Equal(WelcomeMailStatus.NotConfigured, result.Status);
        Assert.Equal(WelcomeMailReason.NoSender, result.Reason);
    }

    // --- Owner decision 2026-10-28 n°3: an administrator sends a new link ---

    [Fact]
    public async Task SendAdminLink_VoidsTheEarlierLink_AndSendsASevenDayOne()
    {
        var user = await SeedUserAsync();
        var service = Service();
        await service.SendWelcomeAsync(user.Id, user.Username, user.Email!, Ct);
        var first = TokenIn(_sent[0]);

        var result = await service.SendAdminLinkAsync(user.Id, Ct);

        Assert.Equal(WelcomeMailStatus.Sent, result!.Status);
        Assert.Equal(2, _sent.Count);
        var second = TokenIn(_sent[1]);
        Assert.NotEqual(first, second);
        var row = await _db.PasswordSetupTokens.AsNoTracking().SingleAsync(t => t.TokenHash == AuthTokenHelper.HashToken(SigningKey, second), Ct);
        Assert.Equal(PasswordSetupPurpose.AdminLink, row.Purpose);
        Assert.Equal(Now.UtcDateTime + PasswordSetupService.WelcomeLifetime, row.ExpiresAt);
        Assert.False(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = first, NewPassword = "Old-Link-Pwd-2026!" }, Ct));
        Assert.True(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = second, NewPassword = "New-Link-Pwd-2026!" }, Ct));
        await _audit.Received(1).LogAsync("PasswordLinkSent", "User", user.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAdminLink_PastTheHourlyLimit_SendsNothing_AndSaysSo()
    {
        var user = await SeedUserAsync();
        var service = Service();
        for (var i = 0; i < PasswordSetupService.MaxAdminLinksPerHour; i++)
            Assert.NotNull(await service.SendAdminLinkAsync(user.Id, Ct));

        Assert.Null(await service.SendAdminLinkAsync(user.Id, Ct));

        Assert.Equal(PasswordSetupService.MaxAdminLinksPerHour, _sent.Count);
        await _audit.Received(1).LogAsync("PasswordLinkRefused.RateLimited", "User", user.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.NotNull(await service.SendAdminLinkAsync(user.Id, Ct));
    }

    [Fact]
    public async Task SendAdminLink_ToAnInactiveOrUnknownAccount_IsRefused_AndSendsNothing()
    {
        var user = await SeedUserAsync();
        user.IsActive = false;
        await _db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<ConflictException>(() => Service().SendAdminLinkAsync(user.Id, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().SendAdminLinkAsync(user.Id + 1000, Ct));
        Assert.Empty(_sent);
    }

    // --- Owner decision 2026-10-28 n°4: forgotten password ---

    [Fact]
    public async Task RequestReset_OnlyQueuesTheWork_SoTheAnswerSaysNothingAboutTheAddress()
    {
        var user = await SeedUserAsync();

        Service().RequestReset(user.Email!);

        _queue.Received(1).Enqueue(Arg.Any<Func<IServiceProvider, CancellationToken, Task>>());
        Assert.Empty(_sent);
        Assert.False(await _db.PasswordSetupTokens.AnyAsync(Ct));
    }

    [Fact]
    public async Task ProcessReset_SendsTheActiveAccountAOneHourLink_WithTheForgottenPasswordTemplate()
    {
        var user = await SeedUserAsync("Current-Pwd-2026!");
        var service = Service();

        await service.ProcessResetAsync("  JEAN.Dupont@Example.org ", Ct);

        var mail = Assert.Single(_sent);
        Assert.Equal(user.Email, mail.To);
        Assert.Equal("Aetheus: choose a new password", mail.Subject);
        Assert.Contains("60", mail.TextBody, StringComparison.Ordinal);
        var token = TokenIn(mail);
        var row = await _db.PasswordSetupTokens.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(PasswordSetupPurpose.Reset, row.Purpose);
        Assert.Equal(Now.UtcDateTime + PasswordSetupService.ResetLifetime, row.ExpiresAt);
        // Asking for a link changes nothing until the link is used.
        Assert.True(BCrypt.Net.BCrypt.Verify("Current-Pwd-2026!", (await _db.Users.AsNoTracking().SingleAsync(Ct)).PasswordHash));
        await _audit.Received(1).LogAsync("PasswordResetMailSent", "User", user.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());

        _clock.Advance(PasswordSetupService.ResetLifetime + TimeSpan.FromSeconds(1));
        Assert.False(await service.CompleteAsync(new CompletePasswordSetupRequest { Token = token, NewPassword = "Too-Late-Pwd-2026!" }, Ct));
    }

    [Fact]
    public async Task ProcessReset_ForAnUnknownOrInactiveAddress_SendsNothing_AndAuditsWithoutTheAddress()
    {
        var user = await SeedUserAsync();
        var service = Service();

        await service.ProcessResetAsync("nobody@example.org", Ct);
        user.IsActive = false;
        await _db.SaveChangesAsync(Ct);
        await service.ProcessResetAsync(user.Email!, Ct);

        Assert.Empty(_sent);
        Assert.False(await _db.PasswordSetupTokens.AnyAsync(Ct));
        await _audit.Received(2).LogAsync("PasswordResetRequested.NoAccount", "Authentication", null,
            Arg.Is<string>(detail => !detail.Contains('@')), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessReset_PastTheHourlyLimitOfTheAccount_SendsNothing()
    {
        var user = await SeedUserAsync();
        var service = Service();
        for (var i = 0; i < PasswordSetupService.MaxResetsPerHour + 2; i++)
            await service.ProcessResetAsync(user.Email!, Ct);

        Assert.Equal(PasswordSetupService.MaxResetsPerHour, _sent.Count);
        await _audit.Received(2).LogAsync("PasswordResetRequested.RateLimited", "User", user.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
