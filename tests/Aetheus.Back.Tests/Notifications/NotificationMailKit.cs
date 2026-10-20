// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Builds notification mails through the real template renderer (OmniMailRenderer), with a repository
/// that holds no stored template unless one is given and returns the recipient preferences given.
/// </summary>
internal static class NotificationMailKit
{
    public static IMailTemplateRepository Repository(
        string? languageJson = null, string? appearanceJson = null, MailTemplate? stored = null)
    {
        var repository = Substitute.For<IMailTemplateRepository>();
        repository.FindRecipientPreferencesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((languageJson, appearanceJson));
        repository.FindAsync(Arg.Any<MailTemplateKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => stored is not null && stored.Kind == call.Arg<MailTemplateKind>() && stored.Language == call.Arg<string>()
                ? stored : null);
        return repository;
    }

    public static IMailTemplateRenderer Templates(string? languageJson = null, string? appearanceJson = null, MailTemplate? stored = null) =>
        new MailTemplateRenderer(Repository(languageJson, appearanceJson, stored), NullLogger<MailTemplateRenderer>.Instance);

    /// <summary>The one mail the emailer hands to the sender for <paramref name="email"/>.</summary>
    public static EmailMessage Build(
        NotificationEmail email, string? frontOrigin = null, string? languageJson = null, string? appearanceJson = null, MailTemplate? stored = null)
    {
        var sender = Substitute.For<IEmailSender>();
        EmailMessage? captured = null;
        sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<IReadOnlyList<EmailMessage>>()[0];
                return (IReadOnlyList<EmailSendResult>)[EmailSendResult.Sent("no-reply@example.org")];
            });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cors:FrontendUrl"] = frontOrigin })
            .Build();
        new NotificationEmailer(sender, Templates(languageJson, appearanceJson, stored), config)
            .SendAsync([email], TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return captured ?? throw new InvalidOperationException("The emailer sent nothing.");
    }
}
