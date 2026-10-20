// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R3-48: after a setup or a reinstall, the mail status says whether the DNS provider has
/// anything to change, or lists the exact records to publish.</summary>
public sealed class MailDnsSummaryTests : BunitContext
{
    public MailDnsSummaryTests() => BunitTestHelper.RegisterServices(this);

    private static MailDiagnosticItemDto Line(string key, MailCheckVerdict verdict, string type = "", string name = "", string value = "") =>
        new() { Key = key, Verdict = verdict, RecordType = type, RecordName = name, RecordValue = value };

    private static List<MailDiagnosticItemDto> AllGreen() =>
    [
        Line("services", MailCheckVerdict.Ok),
        Line("hostname-dns", MailCheckVerdict.Ok, "A", "mx.example.com", "203.0.113.5"),
        Line("reverse-dns", MailCheckVerdict.Ok, "PTR", "203.0.113.5", "mx.example.com"),
        Line("mx:example.com", MailCheckVerdict.Ok, "MX", "example.com", "10 mx.example.com"),
        Line("dkim:example.com", MailCheckVerdict.Ok, "TXT", "default._domainkey.example.com", "v=DKIM1; k=rsa; p=OLD")
    ];

    private IRenderedComponent<MailDnsSummary> RenderSummary(List<MailDiagnosticItemDto> items) =>
        Render<MailDnsSummary>(p => p.Add(x => x.Items, items));

    [Fact]
    public void R3_48_EveryRecordMatches_SaysThereIsNothingToChangeAtTheProvider()
    {
        var cut = RenderSummary(AllGreen());

        Assert.Contains("MailDnsNothingToChange", cut.Find(".mail-dns-summary--ok").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".mail-dns-summary--publish"));
    }

    [Fact]
    public void R3_48_ANewDkimKey_ListsExactlyTheRecordToPublish()
    {
        var items = AllGreen();
        items[4] = Line("dkim:example.com", MailCheckVerdict.Mismatch, "TXT", "default._domainkey.example.com", "v=DKIM1; k=rsa; p=NEW");

        var cut = RenderSummary(items);

        Assert.Empty(cut.FindAll(".mail-dns-summary--ok"));
        var block = Assert.Single(cut.FindComponents<OmniCodeBlock>());
        Assert.Equal("TXT default._domainkey.example.com", block.Instance.Title);
        Assert.Equal("v=DKIM1; k=rsa; p=NEW", block.Instance.Code);
        Assert.Equal("MailDnsRecordsToPublish", cut.FindComponent<OmniAlert>().Instance.Title);
    }

    [Fact]
    public void R3_48_AWrongReverseDns_IsNotAskedOfTheDnsProvider()
    {
        var items = AllGreen();
        items[2] = Line("reverse-dns", MailCheckVerdict.Mismatch, "PTR", "203.0.113.5", "mx.example.com");

        var cut = RenderSummary(items);

        Assert.NotEmpty(cut.FindAll(".mail-dns-summary--ok"));
        Assert.Empty(cut.FindComponents<OmniCodeBlock>());
    }

    [Fact]
    public void R3_48_AnUnansweredLookup_NeverClaimsThatNothingChanges()
    {
        var items = AllGreen();
        items[3] = Line("mx:example.com", MailCheckVerdict.Unknown, "MX", "example.com", "10 mx.example.com");

        var cut = RenderSummary(items);

        Assert.Empty(cut.FindAll(".mail-dns-summary--ok"));
        Assert.NotEmpty(cut.FindAll(".mail-dns-summary--incomplete"));
    }

    [Fact]
    public void R3_48_ARedRecordWithoutAKnownValue_IsNamedAsNotReadYet()
    {
        var items = AllGreen();
        items[4] = Line("dkim:example.com", MailCheckVerdict.Missing, "TXT", "default._domainkey.example.com");

        var cut = RenderSummary(items);

        Assert.Contains("MailDnsRecordValueUnknown", cut.Find(".mail-dns-summary--publish").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_48_NoDnsLineYet_SaysNothing()
    {
        var cut = RenderSummary([Line("services", MailCheckVerdict.Ok)]);

        Assert.Empty(cut.FindAll(".mail-dns-summary"));
    }
}
