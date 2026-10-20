// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Resources;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Mail split-button routing, quota colours and code localization.</summary>
public sealed class MailSectionHelpersTests
{
    private static readonly IStringLocalizer<AppStrings> L = CreateLocalizer();

    private static IStringLocalizer<AppStrings> CreateLocalizer()
    {
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>() + ":{0}"));
        return localizer;
    }

    [Theory]
    [InlineData("postfix", "start", MailAction.StartPostfix)]
    [InlineData("postfix", "stop", MailAction.StopPostfix)]
    [InlineData("postfix", "reload", MailAction.ReloadPostfix)]
    [InlineData("postfix", "restart", MailAction.RestartPostfix)]
    [InlineData("dovecot", "start", MailAction.StartDovecot)]
    [InlineData("dovecot", "stop", MailAction.StopDovecot)]
    [InlineData("dovecot", "reload", MailAction.ReloadDovecot)]
    [InlineData("dovecot", "restart", MailAction.RestartDovecot)]
    public void SplitButtonItems_MapToTheirOwnAction(string unit, string verb, MailAction expected)
        => Assert.Equal(expected, ServerMailStatusList.ServiceAction(unit, verb));

    [Theory]
    [InlineData(1000, 500, OmniTone.Success)]
    [InlineData(1000, 700, OmniTone.Warning)]
    [InlineData(1000, 900, OmniTone.Warning)]
    [InlineData(1000, 950, OmniTone.Danger)]
    public void QuotaColour_FollowsTheUsageBands(int quota, int used, OmniTone expected)
        => Assert.Equal(expected, ServerMailSection.QuotaStyle(new MailAccountDto { QuotaMb = quota, UsedMb = used }));

    [Fact]
    public void UnmanagedQuota_HasNoPercentage()
        => Assert.Equal(0, ServerMailSection.UsagePercent(new MailAccountDto { QuotaMb = 0, UsedMb = 300 }));

    [Theory]
    [InlineData("aliases-skipped|3", "MailFindingAliasesSkipped:3")]
    [InlineData("unreadable|/etc/postfix/vmailbox", "MailFindingUnreadable:/etc/postfix/vmailbox")]
    [InlineData("brand-new-code|x", "brand-new-code|x")]
    public void CollectorFindings_AreLocalisedOrShownVerbatim(string finding, string expected)
        => Assert.Equal(expected, MailUiText.Finding(L, finding));

    [Fact]
    public void DiagnosticLabels_AndDetails_UseTheirResources()
    {
        Assert.Equal("MailDiagMx:example.com", MailUiText.DiagnosticLabel(L, "mx:example.com"));
        Assert.Equal("MailDiagTls:{0}", MailUiText.DiagnosticLabel(L, "tls"));
        Assert.Equal("MailDetailKeyMismatch:{0}", MailUiText.Detail(L, "key-mismatch"));
        Assert.Equal("mail.example.com", MailUiText.Detail(L, "mail.example.com"));
        Assert.Equal(OmniTone.Danger, MailUiText.VerdictStyle(MailCheckVerdict.Missing));
    }

    [Fact]
    public void R2_090_TheServicesFix_StartsEachStoppedUnit()
        => Assert.Equal([MailAction.StartPostfix, MailAction.StartDovecot, MailAction.RestartOpenDkim],
            ServerMailStatusList.StartActions("postfix, dovecot,opendkim,unknown"));

    [Fact]
    public void R2_090_SpfDkimAndDmarcLines_NameTheirDomain()
    {
        Assert.Equal("MailDiagSpf:example.com", MailUiText.DiagnosticLabel(L, "spf:example.com"));
        Assert.Equal("MailDiagDkim:example.com", MailUiText.DiagnosticLabel(L, "dkim:example.com"));
        Assert.Equal("MailDiagDmarc:example.com", MailUiText.DiagnosticLabel(L, "dmarc:example.com"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("example.com", "mx.example.com")]
    public void R2_080_TheSuggestedHost_IsMxOfTheDomain(string domain, string expected)
        => Assert.Equal(expected, ServerMailSetupAssistant.SuggestedHost(domain));
}
