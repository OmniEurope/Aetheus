// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.SharedDtos;

/// <summary>Parses machine-readable mail-manage output; tests/mail-stack/scenario.sh separately
/// exercises its producers against the mail services.</summary>
public sealed class MailTaskOutputParserTests
{
    // Line captured verbatim from `mail-manage queue-list` on Debian 12 by tests/mail-stack (2026-09-14).
    private const string CapturedDeferred =
        "{\"queue_name\": \"deferred\", \"queue_id\": \"1736261560\", \"arrival_time\": 1789401655, \"message_size\": 310, " +
        "\"forced_expire\": false, \"sender\": \"admin@example.test\", \"recipients\": [{\"address\": \"bob@second.test\", " +
        "\"delay_reason\": \"connect to mail.example.test[private/dovecot-lmtp]: Connection refused\"}]}";

    [Fact]
    public void ParseQueue_ReadsARealPostqueueRecord()
    {
        var item = Assert.Single(MailTaskOutputParser.ParseQueue("noise\n" + CapturedDeferred + "\n{truncated"));

        Assert.Equal(("1736261560", "admin@example.test", "bob@second.test", 310L), (item.Id, item.Sender, item.Recipient, item.SizeBytes));
        Assert.Equal("deferred: connect to mail.example.test[private/dovecot-lmtp]: Connection refused", item.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789401655).UtcDateTime, item.ArrivalTime);
    }

    [Fact]
    public void ParseQueue_ReadsPostqueueJsonAndSkipsChatter()
    {
        const string output =
            "sudo: some warning\n" +
            "{\"queue_name\": \"deferred\", \"queue_id\": \"4F2A1B3C9D\", \"arrival_time\": 1789399124, \"message_size\": 2048, " +
            "\"sender\": \"admin@example.com\", \"recipients\": [{\"address\": \"bob@example.org\", \"delay_reason\": \"connect to mx.example.org[1.2.3.4]:25: Connection timed out\"}]}\n" +
            "{\"queue_name\": \"active\", \"queue_id\": \"7B81C0608B\", \"arrival_time\": 1789399200, \"message_size\": 512, " +
            "\"sender\": \"a@example.com\", \"recipients\": [{\"address\": \"c@example.net\"}, {\"address\": \"d@example.net\"}]}\n" +
            "{truncated";

        var items = MailTaskOutputParser.ParseQueue(output);

        Assert.Equal(2, items.Count);
        Assert.Equal(("4F2A1B3C9D", "admin@example.com", "bob@example.org", 2048L), (items[0].Id, items[0].Sender, items[0].Recipient, items[0].SizeBytes));
        Assert.StartsWith("deferred: connect to mx.example.org", items[0].Status, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789399124).UtcDateTime, items[0].ArrivalTime);
        Assert.Equal("c@example.net, d@example.net", items[1].Recipient);
        Assert.Equal("active", items[1].Status);
    }

    [Fact]
    public void ParseQuotaReport_MapsAddressesCaseInsensitively()
    {
        var usage = MailTaskOutputParser.ParseQuotaReport("QUOTA\tbob@second.test\t2048\nnoise\nQUOTA\tadmin@example.test\t12\nQUOTA\tbad\tx\n");

        Assert.Equal(2, usage.Count);
        Assert.Equal(2048, usage["BOB@second.test"]);
    }

    [Fact]
    public void ParseDelivery_ReturnsTheFinalStatusLine()
    {
        var result = MailTaskOutputParser.ParseDelivery(
            "DELIVERY\tsent\t68436587DA\taetheus-test-1-2\t68436587DA: to=<bob@second.test>, status=sent (250 2.0.0 Saved)\n");

        Assert.NotNull(result);
        Assert.True(result.IsSent);
        Assert.Equal(("68436587DA", "aetheus-test-1-2"), (result.QueueId, result.MessageMarker));
        Assert.Null(MailTaskOutputParser.ParseDelivery("nothing here"));
    }

    [Fact]
    public void ParseChecks_ReadsEveryComponent()
    {
        var checks = MailTaskOutputParser.ParseChecks("CHECK\tpostfix\tok\t\nCHECK\tport-587\tfail\tnot listening\n");

        Assert.Equal(2, checks.Count);
        Assert.True(checks[0].IsOk);
        Assert.Equal(("port-587", false, "not listening"), (checks[1].Component, checks[1].IsOk, checks[1].Detail));
    }

    [Fact]
    public void DkimTxtRecord_IsReassembledAndItsKeyExtracted()
    {
        const string file = "s1._domainkey\tIN\tTXT\t( \"v=DKIM1; h=sha256; k=rsa; \"\n\t  \"p=MIIBIjAN\"\n\t  \"BgkqhkiG9w0\" )  ; ----- DKIM key s1 for example.com\n";

        var txt = MailTaskOutputParser.ParseDkimTxtRecord(file);

        Assert.Equal("v=DKIM1; h=sha256; k=rsa; p=MIIBIjANBgkqhkiG9w0", txt);
        Assert.Equal("MIIBIjANBgkqhkiG9w0", MailTaskOutputParser.DkimPublicKeyOf(txt));
        Assert.Equal(string.Empty, MailTaskOutputParser.DkimPublicKeyOf("v=DKIM1; k=rsa"));
    }
}
