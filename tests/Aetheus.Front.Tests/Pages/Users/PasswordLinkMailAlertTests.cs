// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Users;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-14: a mail that did not leave is explained in the reader's language, by kind of failure,
/// never with the backend's English sentence.</summary>
public sealed class PasswordLinkMailAlertTests : BunitContext
{
    private const string EnglishBackendText = "No e-mail sender: the mailbox does not exist yet";

    public PasswordLinkMailAlertTests() => BunitTestHelper.RegisterServices(this, isAdmin: true);

    [Theory]
    [InlineData(WelcomeMailStatus.NotConfigured, WelcomeMailReason.NoSender, "PasswordLinkMailNoSender")]
    [InlineData(WelcomeMailStatus.NotConfigured, WelcomeMailReason.NoPublicAddress, "PasswordLinkMailNoPublicAddress")]
    [InlineData(WelcomeMailStatus.NotConfigured, WelcomeMailReason.None, "PasswordLinkMailNoSender")]
    [InlineData(WelcomeMailStatus.Failed, WelcomeMailReason.None, "PasswordLinkMailFailed")]
    public void ANotSentMail_IsExplainedByItsKind_AndNeverShowsTheBackendText(
        WelcomeMailStatus status, WelcomeMailReason reason, string expectedKey)
    {
        var cut = Render<PasswordLinkMailAlert>(parameters => parameters.Add(p => p.Result, new WelcomeMailResultDto
        {
            Status = status,
            Reason = reason,
            To = "jdoe@example.org",
            Message = EnglishBackendText
        }));

        Assert.True(cut.Markup.Contains(expectedKey, StringComparison.Ordinal), $"expected the localized text {expectedKey}");
        Assert.False(cut.Markup.Contains(EnglishBackendText, StringComparison.Ordinal), "the backend text reached the page");
    }
}
