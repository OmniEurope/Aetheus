// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Administration for organization admins (2026-10-28): the mail of an event about an organization's resource
/// is written with that organization's own template; another organization's mail, and a mail of no
/// organization, keep the platform's.
/// </summary>
public sealed class NotificationOrganizationTemplateTests
{
    private const string OwnSubject = "Organization seven: {{Event}}";

    private static IMailTemplateRepository Repository()
    {
        var repository = NotificationMailKit.Repository();
        repository.FindForOrganizationAsync(7, MailTemplateKind.NotificationEvent, MailTemplateLanguages.English, Arg.Any<CancellationToken>())
            .Returns(new OrganizationMailTemplate
            {
                OrganizationId = 7,
                Kind = MailTemplateKind.NotificationEvent,
                Language = MailTemplateLanguages.English,
                Subject = OwnSubject,
                Body = "<p>{{Summary}}</p>"
            });
        return repository;
    }

    private static async Task<string> SubjectSentAsync(int? organizationId)
    {
        var sender = Substitute.For<IEmailSender>();
        EmailMessage? captured = null;
        sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<IReadOnlyList<EmailMessage>>()[0];
                return (IReadOnlyList<EmailSendResult>)[EmailSendResult.Sent("no-reply@example.org")];
            });
        var emailer = new NotificationEmailer(sender, new MailTemplateRenderer(Repository(), NullLogger<MailTemplateRenderer>.Instance));
        await emailer.SendAsync(
            [new NotificationEmail("reader@example.org", "pipeline.failed in project Shop", "pipeline.failed",
                """{"PipelineRunId":1,"PipelineId":2,"ProjectId":3,"ProjectName":"Shop","Status":"Failed"}""", organizationId)],
            TestContext.Current.CancellationToken);
        return captured?.Subject ?? throw new InvalidOperationException("Nothing was sent.");
    }

    [Fact]
    public async Task TheOrganizationsOwnTemplate_WritesTheMailsOfItsResources()
    {
        Assert.StartsWith("Organization seven: ", await SubjectSentAsync(7), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(null)]
    public async Task AnotherOrganization_OrNone_KeepsThePlatformsTemplate(int? organizationId)
    {
        Assert.DoesNotContain("Organization seven", await SubjectSentAsync(organizationId), StringComparison.Ordinal);
    }
}
