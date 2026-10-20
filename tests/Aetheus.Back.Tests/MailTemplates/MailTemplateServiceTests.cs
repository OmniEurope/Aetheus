// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services.Email;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recette R1-75: the administration of the templates, and the test mail sent to the administrator.</summary>
public sealed class MailTemplateServiceTests
{
    private readonly IMailTemplateRepository _repository = Substitute.For<IMailTemplateRepository>();
    private readonly IEmailSender _sender = Substitute.For<IEmailSender>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly MailTemplateService _sut;

    public MailTemplateServiceTests()
    {
        _repository.FindRecipientPreferencesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(((string?)null, (string?)null));
        _sut = new MailTemplateService(_repository,
            new MailTemplateRenderer(_repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<MailTemplateRenderer>.Instance),
            _sender, _audit);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SaveMailTemplateRequest Draft(string subject = "[Aetheus] {{ChannelName}}", string body = "<p>{{ChannelName}}</p>") =>
        new() { Subject = subject, Body = body };

    [Fact]
    public async Task List_ShowsEveryKindInEveryLanguage_AndWhichOnesAreChanged()
    {
        _repository.ListAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MailTemplate { Kind = MailTemplateKind.NotificationChannelTest, Language = "fr", UpdatedBy = "admin", UpdatedAt = new DateTime(2026, 10, 13) }
        ]);

        var list = await _sut.ListAsync(Ct);

        Assert.Equal(Enum.GetValues<MailTemplateKind>().Length * MailTemplateLanguages.All.Count, list.Count);
        var changed = Assert.Single(list, item => item.IsCustomized);
        Assert.Equal((MailTemplateKind.NotificationChannelTest, "fr", "admin"), (changed.Kind, changed.Language, changed.UpdatedBy));
    }

    [Fact]
    public async Task Get_WithoutStoredTemplate_ReturnsTheDefault_WithItsVariablesAndSamples()
    {
        var template = await _sut.GetAsync(MailTemplateKind.NotificationChannelTest, "fr", Ct);

        Assert.False(template.IsCustomized);
        Assert.Contains("{{ChannelName}}", template.Subject, StringComparison.Ordinal);
        Assert.Equal(["ChannelName", "InboxUrl"], template.Variables.Select(variable => variable.Name));
        Assert.Equal("Équipe exploitation", template.Variables[0].SampleValue);
        Assert.Equal([MailTemplateVariableKind.Text, MailTemplateVariableKind.Link], template.Variables.Select(variable => variable.Kind));
    }

    [Fact]
    public async Task Get_TheNotificationEvent_OffersItsLinkAndBlocks_WithSamplesForThePreview()
    {
        var template = await _sut.GetAsync(MailTemplateKind.NotificationEvent, "en", Ct);

        var byName = template.Variables.ToDictionary(variable => variable.Name);
        Assert.Equal(MailTemplateVariableKind.Link, byName["Link"].Kind);
        Assert.Equal("https://aetheus.example.org/pipelines/runs/4821", byName["Link"].SampleValue);
        Assert.Equal(MailTemplateVariableKind.Block, byName["Details"].Kind);
        Assert.Contains("background-color:#fde8e6;color:#a3231a\">Failed</span>", byName["Details"].SampleValue, StringComparison.Ordinal);
        Assert.Equal(MailTemplateVariableKind.Block, byName["StatusBanner"].Kind);
        Assert.Contains("{{#button Link}}", template.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_AcceptsAButtonToAnOfferedLink_AndRefusesOneToAnUnknownVariable()
    {
        _repository.UpsertAsync(Arg.Any<MailTemplateKind>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => new MailTemplate { Kind = call.Arg<MailTemplateKind>(), Language = call.ArgAt<string>(1), Subject = call.ArgAt<string>(2), Body = call.ArgAt<string>(3) });

        var saved = await _sut.SaveAsync(MailTemplateKind.DailyRecap, "fr",
            Draft("Récap {{Date}}", "<p>{{Tiles}}</p><p>{{#button SettingsUrl}}Régler</p>"), "admin", Ct);
        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SaveAsync(MailTemplateKind.DailyRecap, "fr", Draft("Récap", "<p>{{#button Elsewhere}}Aller</p>"), "admin", Ct));

        Assert.True(saved.IsCustomized);
        Assert.Contains("{{Elsewhere}}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_RefusesAVariableTheKindDoesNotOffer_AndStoresNothing()
    {
        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SaveAsync(MailTemplateKind.NotificationChannelTest, "en", Draft(body: "<p>{{ChannelName}} {{Headline}}</p>"), "admin", Ct));

        Assert.Contains("{{Headline}}", error.Message, StringComparison.Ordinal);
        await _repository.DidNotReceive().UpsertAsync(Arg.Any<MailTemplateKind>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("de")]
    [InlineData("FR")]
    [InlineData("")]
    public async Task AnUnsupportedLanguage_IsRefused(string language)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.GetAsync(MailTemplateKind.NotificationChannelTest, language, Ct));
    }

    [Fact]
    public async Task NotificationsAreNotAnEditableKind()
    {
        // Notifications keep their rich layout (decision of 2026-10-13): no template of theirs can be read or stored.
        Assert.DoesNotContain(Enum.GetNames<MailTemplateKind>(), name => name == "Notification");
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetAsync((MailTemplateKind)0, "en", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.SaveAsync((MailTemplateKind)0, "en", Draft(), "admin", Ct));
    }

    [Fact]
    public async Task Save_StoresTheTemplate_AndAuditsIt()
    {
        _repository.UpsertAsync(MailTemplateKind.NotificationChannelTest, "en", "[Aetheus] {{ChannelName}}", "<p>{{ChannelName}}</p>", "admin", Arg.Any<CancellationToken>())
            .Returns(new MailTemplate { Id = 4, Kind = MailTemplateKind.NotificationChannelTest, Language = "en", Subject = "[Aetheus] {{ChannelName}}", Body = "<p>{{ChannelName}}</p>", UpdatedBy = "admin" });

        var saved = await _sut.SaveAsync(MailTemplateKind.NotificationChannelTest, "en", Draft(), "admin", Ct);

        Assert.True(saved.IsCustomized);
        Assert.Equal("<p>{{ChannelName}}</p>", saved.Body);
        await _audit.Received(1).LogAsync("Updated", "MailTemplate", 4, "NotificationChannelTest (en)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_RemovesTheStoredTemplate_AndReturnsTheDefault()
    {
        _repository.DeleteAsync(MailTemplateKind.NotificationChannelTest, "fr", Arg.Any<CancellationToken>()).Returns(true);

        var restored = await _sut.ResetAsync(MailTemplateKind.NotificationChannelTest, "fr", Ct);

        Assert.False(restored.IsCustomized);
        Assert.Equal(MailTemplateDefaults.For(MailTemplateKind.NotificationChannelTest, "fr").Body, restored.Body);
        await _audit.Received(1).LogAsync("Reset", "MailTemplate", null, "NotificationChannelTest (fr)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendTest_RendersTheDraftWithTheSamples_AndSendsItToTheAdministrator()
    {
        _repository.FindUserEmailAsync(7, Arg.Any<CancellationToken>()).Returns("admin@example.org");
        _sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(EmailSendResult.Sent("no-reply@example.org"));

        var result = await _sut.SendTestAsync(MailTemplateKind.NotificationChannelTest, "fr", Draft("Essai {{ChannelName}}", "<p>Canal {{ChannelName}}</p>"), 7, Ct);

        Assert.Equal(NotificationTestStatus.Sent, result.Status);
        Assert.Equal("admin@example.org", result.To);
        await _sender.Received(1).SendAsync(Arg.Is<EmailMessage>(message =>
            message.To == "admin@example.org"
            && message.Subject == "Essai Équipe exploitation"
            && message.HtmlBody!.Contains("lang=\"fr\"")
            && message.TextBody.Contains("Canal Équipe exploitation")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendTest_WithoutAnAddress_SendsNothing_AndSaysSo()
    {
        _repository.FindUserEmailAsync(7, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _sut.SendTestAsync(MailTemplateKind.NotificationChannelTest, "en", Draft(), 7, Ct);

        Assert.Equal(NotificationTestStatus.NotConfigured, result.Status);
        Assert.Equal(MailTemplateService.NoAddressMessage, result.Message);
        await _sender.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendTest_ReportsTheSendersFailure_Verbatim()
    {
        _repository.FindUserEmailAsync(7, Arg.Any<CancellationToken>()).Returns("admin@example.org");
        _sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(EmailSendResult.Failed("550 mailbox unavailable"));

        var result = await _sut.SendTestAsync(MailTemplateKind.NotificationChannelTest, "en", Draft(), 7, Ct);

        Assert.Equal(NotificationTestStatus.Failed, result.Status);
        Assert.Equal("550 mailbox unavailable", result.Message);
        await _audit.DidNotReceive().LogAsync("Tested", Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
