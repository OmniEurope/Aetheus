// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>The scheduled send: once per local day, claimed before sending, reported afterwards.</summary>
public sealed class DailyRecapServiceTests : IDisposable
{
    // 07:00 in Paris (summer time) on 2026-10-09.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 9, 5, 0, 0, TimeSpan.Zero));
    private readonly string _database = Guid.NewGuid().ToString("N");
    private readonly List<AppDbContext> _contexts = [];
    private readonly IEmailSender _sender = Substitute.For<IEmailSender>();
    private readonly List<EmailMessage> _sent = [];

    public DailyRecapServiceTests()
    {
        _sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var messages = call.Arg<IReadOnlyList<EmailMessage>>();
                _sent.AddRange(messages);
                return messages.Select(_ => EmailSendResult.Sent("no-reply@example.org")).ToList();
            });
        _sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sent.Add(call.Arg<EmailMessage>());
                return EmailSendResult.Sent("no-reply@example.org");
            });

        using var db = Context();
        db.Roles.Add(new Role { Id = 1, Name = "Admin" });
        db.Users.AddRange(
            new User { Id = 1, Username = "alice", Email = "alice@example.org", IsActive = true },
            new User { Id = 2, Username = "nomail", Email = null, IsActive = true });
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

    /// <summary>A fresh service over a fresh context, as after a restart or in another instance.</summary>
    private DailyRecapService Service(IConfiguration? configuration = null, IMailTemplateRenderer? templates = null)
    {
        var repo = new DailyRecapRepository(Context());
        var compatibility = Substitute.For<IAgentCompatibilityPolicy>();
        compatibility.Evaluate(Arg.Any<ServerDto>()).Returns(new AgentCompatibilityDto());
        var collector = new DailyRecapCollector(repo, compatibility, new ConfigurationBuilder().Build());
        return new DailyRecapService(repo, collector, _sender, templates ?? NotificationMailKit.Templates(), Substitute.For<IAuditService>(), _clock,
            configuration ?? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cors:Origins:0"] = "https://app.example.org" }).Build(),
            NullLogger<DailyRecapService>.Instance);
    }

    [Fact]
    public async Task AtTheSendTime_TheRecapGoesToTheAdministrators_Once()
    {
        Assert.Equal(DailyRecapScheduleOutcome.Due, await Service().RunScheduledAsync(Ct));

        var message = Assert.Single(_sent);
        Assert.Equal("alice@example.org", message.To);
        Assert.Contains("8 octobre 2026", message.Subject);
        Assert.NotNull(message.HtmlBody);
        Assert.False(string.IsNullOrWhiteSpace(message.TextBody));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DailyRecapScheduleOutcome.AlreadySent, await Service().RunScheduledAsync(Ct));
        Assert.Single(_sent);

        var settings = await Service().GetSettingsAsync(Ct);
        Assert.Equal(new DateOnly(2026, 10, 9), settings.LastSentDate);
        Assert.Equal(1, settings.LastRecipientCount);
        Assert.Equal(1, settings.LastDeliveredCount);
        Assert.Null(settings.LastError);
    }

    [Fact]
    public async Task BeforeTheSendTime_NothingIsSent()
    {
        // It is 07:00 in Paris; the recap is set for 07:01.
        await Service().UpdateSettingsAsync(new UpdateDailyRecapSettingsRequest
        {
            SendTime = new TimeOnly(7, 1),
            TimeZoneId = "Europe/Paris",
            Sections = [DailyRecapSection.Pipelines],
            Recipients = ["alice@example.org"]
        }, Ct);

        Assert.Equal(DailyRecapScheduleOutcome.NotYet, await Service().RunScheduledAsync(Ct));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task TheNextDay_SendsAgain()
    {
        await Service().RunScheduledAsync(Ct);
        _clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(DailyRecapScheduleOutcome.Due, await Service().RunScheduledAsync(Ct));
        Assert.Equal(2, _sent.Count);
        Assert.Contains("9 octobre 2026", _sent[1].Subject);
    }

    [Fact]
    public async Task AFailedSend_IsReportedVerbatim_AndNotRepeatedTheSameDay()
    {
        _sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<EmailMessage>>().Select(_ => EmailSendResult.Failed("5.7.1 Relay access denied")).ToList());

        await Service().RunScheduledAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(DailyRecapScheduleOutcome.AlreadySent, await Service().RunScheduledAsync(Ct));

        await _sender.Received(1).SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>());
        var settings = await Service().GetSettingsAsync(Ct);
        Assert.Equal(0, settings.LastDeliveredCount);
        Assert.Equal("alice@example.org: 5.7.1 Relay access denied", settings.LastError);
    }

    [Fact]
    public async Task ARecapThatThrows_IsShownOnTheSettings_AndNotRepeatedTheSameDay()
    {
        _sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<EmailSendResult>>(_ => throw new InvalidOperationException("SMTP pipeline broke"));

        Assert.Equal(DailyRecapScheduleOutcome.Due, await Service().RunScheduledAsync(Ct));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(DailyRecapScheduleOutcome.AlreadySent, await Service().RunScheduledAsync(Ct));

        var settings = await Service().GetSettingsAsync(Ct);
        Assert.Equal(0, settings.LastDeliveredCount);
        Assert.Contains("SMTP pipeline broke", settings.LastError);
    }

    [Fact]
    public async Task ChosenRecipients_ReplaceTheAdministrators()
    {
        await Service().UpdateSettingsAsync(new UpdateDailyRecapSettingsRequest
        {
            Enabled = true,
            SendTime = new TimeOnly(7, 0),
            TimeZoneId = "Europe/Paris",
            Sections = [DailyRecapSection.Pipelines],
            Recipients = ["ops@example.org", "cto@example.org"]
        }, Ct);

        await Service().RunScheduledAsync(Ct);

        Assert.Equal(["ops@example.org", "cto@example.org"], _sent.Select(message => message.To));
    }

    /// <summary>Recette R1-8: the sent recap follows each recipient's own language and appearance, like a notification mail.</summary>
    [Fact]
    public async Task R18_EachRecipientReceivesTheRecap_InTheirOwnLanguageAndColours()
    {
        const string dark = """{"version":1,"appearance":"Dark","preset":null,"palette":null}""";
        var repository = Substitute.For<IMailTemplateRepository>();
        repository.FindRecipientPreferencesAsync("dark-en@example.org", Arg.Any<CancellationToken>()).Returns(("\"en\"", dark));
        repository.FindRecipientPreferencesAsync("light-fr@example.org", Arg.Any<CancellationToken>()).Returns(("\"fr-FR\"", null));
        repository.FindRecipientPreferencesAsync("nobody@example.org", Arg.Any<CancellationToken>()).Returns(((string?)null, (string?)null));
        var templates = new MailTemplateRenderer(repository, NullLogger<MailTemplateRenderer>.Instance);
        await Service().UpdateSettingsAsync(new UpdateDailyRecapSettingsRequest
        {
            Enabled = true,
            SendTime = new TimeOnly(7, 0),
            TimeZoneId = "Europe/Paris",
            Sections = [DailyRecapSection.Pipelines],
            Recipients = ["dark-en@example.org", "light-fr@example.org", "nobody@example.org"]
        }, Ct);

        await Service(templates: templates).RunScheduledAsync(Ct);

        var darkTheme = MailRecipientStyle.FromPreferences(null, dark).Theme;
        var byAddress = _sent.ToDictionary(message => message.To);
        Assert.Equal(3, byAddress.Count);
        Assert.Contains("lang=\"en\"", byAddress["dark-en@example.org"].HtmlBody, StringComparison.Ordinal);
        Assert.Contains("October 8, 2026", byAddress["dark-en@example.org"].Subject, StringComparison.Ordinal);
        Assert.Contains(darkTheme.PageBackground, byAddress["dark-en@example.org"].HtmlBody!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lang=\"fr\"", byAddress["light-fr@example.org"].HtmlBody, StringComparison.Ordinal);
        Assert.Contains("8 octobre 2026", byAddress["light-fr@example.org"].Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(darkTheme.PageBackground, byAddress["light-fr@example.org"].HtmlBody!, StringComparison.OrdinalIgnoreCase);
        // No account, no preference: the language the recap is configured in (French here), default colours.
        Assert.Contains("8 octobre 2026", byAddress["nobody@example.org"].Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(darkTheme.PageBackground, byAddress["nobody@example.org"].HtmlBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchedOff_NothingIsSent()
    {
        await Service().UpdateSettingsAsync(new UpdateDailyRecapSettingsRequest { Enabled = false, TimeZoneId = "Europe/Paris" }, Ct);

        Assert.Equal(DailyRecapScheduleOutcome.Disabled, await Service().RunScheduledAsync(Ct));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task SendNow_GoesToTheNextRecipients_TheAdministratorsUntilAListIsSaved_AndDoesNotClaimTheDay()
    {
        var first = await Service().SendNowAsync(Ct);

        Assert.Equal(DailyRecapSendStatus.Sent, first.Status);
        Assert.Equal(["alice@example.org"], first.Recipients);
        Assert.Equal(1, first.Delivered);

        await Service().UpdateSettingsAsync(new UpdateDailyRecapSettingsRequest
        {
            TimeZoneId = "Europe/Paris",
            Sections = [DailyRecapSection.Servers],
            Recipients = ["ops@example.org", "external@partner.net"]
        }, Ct);
        var second = await Service().SendNowAsync(Ct);

        Assert.Equal(["ops@example.org", "external@partner.net"], second.Recipients);
        Assert.Equal(["alice@example.org", "ops@example.org", "external@partner.net"], _sent.Select(message => message.To));
        Assert.Null((await Service().GetSettingsAsync(Ct)).LastSentDate);
    }

    [Fact]
    public async Task SendNow_ReportsARefusal_WithTheAddressAndTheSmtpText()
    {
        _sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Failed("5.1.1 Mailbox unavailable")]);

        var result = await Service().SendNowAsync(Ct);

        Assert.Equal(DailyRecapSendStatus.Failed, result.Status);
        Assert.Equal(0, result.Delivered);
        Assert.Equal("alice@example.org: 5.1.1 Mailbox unavailable", result.Message);
    }

    [Fact]
    public async Task SendNow_WithAnEmptySavedList_SendsNothing_AndSaysWhy()
    {
        await Service().UpdateSettingsAsync(new UpdateDailyRecapSettingsRequest { Enabled = false, TimeZoneId = "Europe/Paris", Recipients = [] }, Ct);

        var result = await Service().SendNowAsync(Ct);

        Assert.Equal(DailyRecapSendStatus.NotConfigured, result.Status);
        Assert.Equal(DailyRecapService.NoRecipientMessage, result.Message);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task SwitchingOnWithAnEmptyList_IsRefused()
    {
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() =>
            Service().UpdateSettingsAsync(new UpdateDailyRecapSettingsRequest { Enabled = true, TimeZoneId = "Europe/Paris", Recipients = [] }, Ct));
    }

    [Fact]
    public async Task Preview_IsYesterdaysRecap()
    {
        var preview = await Service().PreviewAsync(Ct);

        Assert.Equal(new DateOnly(2026, 10, 8), preview.Day);
        Assert.Contains("<!DOCTYPE html>", preview.Html);
        Assert.Contains("8 octobre 2026", preview.Subject);
    }

    [Fact]
    public async Task PreviewLinks_UseTheRemoteOriginInsteadOfTheDevelopmentDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:FrontendUrl"] = "https://localhost:5401",
            ["Cors:Origins:0"] = "https://app.example.org"
        }).Build();
        var preview = await Service(configuration).PreviewAsync(Ct);
        Assert.Contains("href=\"https://app.example.org/admin/settings?tab=daily-recap\"", preview.Html);
        Assert.DoesNotContain("localhost", preview.Html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PreviewLinks_FallBackToFrontendUrlWhenTheRemoteOriginIsNotSet(string? remoteOrigin)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:FrontendUrl"] = "https://app.example.org",
            ["Cors:Origins:0"] = remoteOrigin
        }).Build();
        var preview = await Service(configuration).PreviewAsync(Ct);
        Assert.Contains("href=\"https://app.example.org/admin/settings?tab=daily-recap\"", preview.Html);
    }

    [Fact]
    public async Task Settings_DefaultRecipientsListTheAdministrators()
    {
        var settings = await Service().GetSettingsAsync(Ct);

        Assert.True(settings.Enabled);
        Assert.Equal(["alice@example.org"], settings.Recipients);
        Assert.True(settings.RecipientsAreDefault);
        Assert.Equal(Enum.GetValues<DailyRecapSection>(), settings.Sections);
    }
}
