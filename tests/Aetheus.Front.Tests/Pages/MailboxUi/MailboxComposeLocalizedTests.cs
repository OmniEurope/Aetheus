// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.MailboxUi;

/// <summary>Recette R1-17: the sentences the reader sees, rendered with the real English resources.</summary>
public sealed class MailboxComposeLocalizedTests : BunitContext
{
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

    public MailboxComposeLocalizedTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton(Aetheus.Front.Tests.Pages.Servers.MailOperationDialogValidationTests.RealLocalizer());
    }

    protected override void Dispose(bool disposing)
    {
        CultureInfo.CurrentUICulture = _previousUiCulture;
        base.Dispose(disposing);
    }

    [Fact]
    public void A_forward_names_the_attachments_it_sends_again()
    {
        var cut = Render<MailboxComposeDialog>(parameters => parameters
            .Add(dialog => dialog.ServerId, 7).Add(dialog => dialog.AccountId, 3).Add(dialog => dialog.From, "me@example.org")
            .Add(dialog => dialog.Mode, MailboxComposeMode.Forward)
            .Add(dialog => dialog.Draft, new MailboxDraftDto
            {
                Subject = "Fwd: Planning",
                ForwardedAttachments = [new MailboxAttachmentDto { FileName = "report.pdf" }, new MailboxAttachmentDto { FileName = "plan.xlsx" }]
            }));

        Assert.Equal("Attachments sent again: report.pdf, plan.xlsx", cut.Find(".mailbox-forwarded").TextContent.Trim());
    }

    [Fact]
    public void Blocked_remote_images_are_counted_in_the_reader()
    {
        var cut = Render<MailboxReader>(parameters => parameters
            .Add(reader => reader.Message, new MailboxMessageDto { Subject = "s", Html = "<p>x</p>", BlockedRemoteImages = 3 })
            .Add(reader => reader.CanWrite, true));

        Assert.StartsWith("3 remote images are not shown", cut.Find(".mailbox-images-blocked").TextContent.Trim(), StringComparison.Ordinal);
    }
}
