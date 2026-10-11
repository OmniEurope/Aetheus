// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// SITE-OFFLINE-001: the DNS records dialog lists the records the backend computed and, when the DKIM key is
/// not known (read in progress, or agent offline), says why instead of showing an empty DKIM value as a record.
/// </summary>
public sealed class MailDnsRecordsDialogTests : BunitContext
{
    public MailDnsRecordsDialogTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static MailDnsRecordsDto Records(MailDkimKeyStatus status) => new()
    {
        Domain = "example.com",
        MxRecord = "10 mx.example.com.",
        SpfRecord = "v=spf1 mx a ~all",
        DkimSelector = "default",
        DkimRecord = "default._domainkey.example.com",
        DkimPublicKey = status == MailDkimKeyStatus.Available ? "v=DKIM1; k=rsa; p=MIIB" : string.Empty,
        DkimKeyStatus = status,
        DmarcRecord = "v=DMARC1; p=quarantine; rua=mailto:postmaster@example.com"
    };

    private IRenderedComponent<MailOperationDialog> RenderDialog(MailDkimKeyStatus status) =>
        Render<MailOperationDialog>(p => p
            .Add(x => x.Mode, MailDialogMode.DnsRecords)
            .Add(x => x.DnsRecords, Records(status)));

    private static string[] Titles(IRenderedComponent<MailOperationDialog> cut) =>
        [.. cut.FindComponents<OmniCodeBlock>().Select(block => block.Instance.Title ?? string.Empty)];

    [Theory]
    [InlineData(MailDkimKeyStatus.AgentOffline, "MailDkimKeyAgentOffline")]
    [InlineData(MailDkimKeyStatus.Reading, "MailDkimKeyReading")]
    public void AnUnknownDkimKey_IsSaidWithItsReason_AndTheOtherRecordsStay(MailDkimKeyStatus status, string reason)
    {
        var cut = RenderDialog(status);

        Assert.Contains(reason, cut.Find(".mail-dkim-unavailable").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(Titles(cut), title => title.StartsWith("DKIM", StringComparison.Ordinal));
        Assert.Contains("MxRecord", Titles(cut));
        Assert.Contains("SPF (TXT)", Titles(cut));
        Assert.Contains("DMARC (TXT _dmarc.example.com)", Titles(cut));
    }

    [Fact]
    public void AKnownDkimKey_IsListedAsARecord_WithNoUnavailableNotice()
    {
        var cut = RenderDialog(MailDkimKeyStatus.Available);

        Assert.Empty(cut.FindAll(".mail-dkim-unavailable"));
        var dkim = cut.FindComponents<OmniCodeBlock>().Single(block => block.Instance.Title == "DKIM (TXT default._domainkey.example.com)");
        Assert.Equal("v=DKIM1; k=rsa; p=MIIB", dkim.Instance.Code);
    }
}
