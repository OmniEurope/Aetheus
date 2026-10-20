// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The collector inventories an existing configuration from disk, reports what it could
/// not read, and never claims a list is complete when a source was missing.</summary>
public sealed class MailCollectorInventoryTests
{
    private const string VmailboxPath = "/etc/postfix/vmailbox";
    private const string VirtualPath = "/etc/postfix/virtual";

    private static IShellRunner Shell(string postconfParameters, string domains = "example.com, second.test")
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "which" || x == "where"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(1)[0].StartsWith("rspamadm", StringComparison.Ordinal)
                ? new ShellExecResult(0, "/usr/bin/rspamadm\n", string.Empty)
                : new ShellExecResult(0, "/usr/sbin/x\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "mail_version" })), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "mail_version = 3.7.11\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.Contains("virtual_mailbox_domains")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, domains + "\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.Contains("virtual_mailbox_maps")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, postconfParameters, string.Empty));
        shell.RunExecAsync("dovecot", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "2.3.19.1 (9b53102964)\n", string.Empty));
        shell.RunExecAsync("rspamadm", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "Rspamadm 3.4\n", string.Empty));
        shell.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "active\n", string.Empty));
        shell.RunExecAsync("postqueue", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "Mail queue is empty\n", string.Empty));
        return shell;
    }

    private static string Parameters(string mailboxMaps = "hash:/etc/postfix/vmailbox", string aliasMaps = "hash:/etc/postfix/virtual",
        string certFile = "/etc/ssl/certs/mail.pem") =>
        $"myhostname = mail.example.com\nsmtpd_tls_cert_file = {certFile}\nvirtual_mailbox_maps = {mailboxMaps}\nvirtual_alias_maps = {aliasMaps}\n";

    private static Task<MailDataDto> CollectAsync(IShellRunner shell, Dictionary<string, string> files, HashSet<string>? unreadable = null)
    {
        unreadable ??= [];
        var collector = new MailCollector(
            NullLogger<MailCollector>.Instance,
            shell,
            fileExists: path => path == "/etc/postfix/main.cf" || files.ContainsKey(path) || unreadable.Contains(path),
            readText: path => files.TryGetValue(path, out var text) ? text : null);
        return collector.CollectAsync(TestContext.Current.CancellationToken);
    }

    private static string SelfSignedPem(string cn, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(notAfter.AddDays(-30), notAfter);
        return cert.ExportCertificatePem();
    }

    [Fact]
    public async Task MissingPostfixBinary_ReportsNotInstalledWithoutProbingAnythingElse()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "which" || x == "where"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(1, string.Empty, "not found"));

        var data = await CollectAsync(shell, []);

        Assert.False(data.IsInstalled);
        await shell.DidNotReceive().RunExecAsync("postconf", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdoptedConfiguration_IsInventoriedCompletely()
    {
        var expiry = new DateTimeOffset(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var files = new Dictionary<string, string>
        {
            [VmailboxPath] = "# comment\nadmin@example.com example.com/admin/\nbob@second.test second.test/bob/\n@catchall.test catchall/\n",
            [VirtualPath] = "info@second.test bob@second.test\nteam@example.com a@example.com, b@example.com\nsecond.test anything\n",
            ["/etc/opendkim.conf"] = "Syslog yes\nKeyTable  /etc/opendkim/KeyTable\n",
            ["/etc/opendkim/KeyTable"] = "default._domainkey.example.com example.com:default:/etc/opendkim/keys/example.com/default.private\n",
            ["/etc/opendkim/keys/example.com/default.txt"] = "default._domainkey\tIN\tTXT\t( \"v=DKIM1; h=sha256; k=rsa; \"\n\t  \"p=MIIBIjANBg\" )  ; ----- DKIM key\n",
            ["/etc/ssl/certs/mail.pem"] = SelfSignedPem("mail.example.com", expiry),
            ["/etc/aetheus/mail-stack.managed"] = "managed-by=aetheus\n",
            ["/usr/local/lib/aetheus/mail-manage"] = "#!/bin/sh\n# aetheus-mail-helper-version: 2\n",
            ["/etc/rspamd/local.d/actions.conf"] = "reject = 20;\nadd_header = 8.5;\ngreylist = 5;\n"
        };

        var result = await CollectAsync(Shell(Parameters()), files);

        Assert.True(result.IsInstalled);
        Assert.Equal("mail.example.com", result.Hostname);
        Assert.True(result.DomainsCollected);
        Assert.True(result.AccountsCollected);
        Assert.True(result.AliasesCollected);
        Assert.Equal(["admin@example.com", "bob@second.test"], result.Accounts.Select(a => a.Email));
        Assert.Equal("second.test", result.Accounts[1].Domain);
        var alias = Assert.Single(result.Aliases);
        Assert.Equal(("info@second.test", "bob@second.test"), (alias.SourceEmail, alias.DestinationEmail));
        Assert.Contains("aliases-skipped|1", result.Diagnostics);
        var dkim = Assert.Single(result.DkimKeys);
        Assert.Equal(("example.com", "default", "v=DKIM1; h=sha256; k=rsa; p=MIIBIjANBg"), (dkim.Domain, dkim.Selector, dkim.PublicKey));
        Assert.True(result.Tls.IsReadable);
        Assert.True(result.Tls.IsSelfSigned);
        Assert.Equal(expiry.UtcDateTime, result.Tls.ExpiresAt);
        Assert.True(result.IsManagedByAetheus);
        Assert.Equal(2, result.HelperVersion);
        Assert.True(result.IsOpenDkimRunning);
        Assert.Equal(("rspamd", true, "3.4"), (result.SpamFilter.Name, result.SpamFilter.IsRunning, result.SpamFilter.Version));
        Assert.Equal((20.0, 8.5, 5.0), (result.SpamFilter.RejectScore, result.SpamFilter.AddHeaderScore, result.SpamFilter.GreylistScore));
    }

    [Fact]
    public async Task UnreadableMailboxSource_IsReportedAndNeverClaimedComplete()
    {
        var result = await CollectAsync(Shell(Parameters()), [], unreadable: [VmailboxPath]);

        Assert.False(result.AccountsCollected);
        Assert.Empty(result.Accounts);
        Assert.Contains($"unreadable|{VmailboxPath}", result.Diagnostics);
    }

    [Fact]
    public async Task DatabaseBackedMaps_AreUnsupportedAndNotCollected()
    {
        var result = await CollectAsync(
            Shell(Parameters(mailboxMaps: "proxy:mysql:/etc/postfix/mysql-mailboxes.cf", aliasMaps: "")),
            new Dictionary<string, string>());

        Assert.False(result.AccountsCollected);
        Assert.True(result.AliasesCollected);
        Assert.Contains("unsupported-map|proxy:mysql:/etc/postfix/mysql-mailboxes.cf", result.Diagnostics);
    }

    [Fact]
    public async Task UnreadableCertificate_KeepsThePathAndReportsIt()
    {
        const string live = "/etc/letsencrypt/live/mail.example.com/fullchain.pem";
        var result = await CollectAsync(Shell(Parameters(certFile: live)), new Dictionary<string, string>
        {
            [VmailboxPath] = string.Empty,
            [VirtualPath] = string.Empty
        });

        Assert.Equal(live, result.Tls.CertPath);
        Assert.False(result.Tls.IsReadable);
        Assert.Null(result.Tls.ExpiresAt);
        Assert.Contains($"tls-unreadable|{live}", result.Diagnostics);
    }

    [Fact]
    public async Task PostconfParameterFailure_LeavesInventoryUncollected()
    {
        var shell = Shell(string.Empty);
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.Contains("virtual_mailbox_maps")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(1, string.Empty, "fatal"));

        var result = await CollectAsync(shell, new Dictionary<string, string>());

        Assert.True(result.IsInstalled);
        Assert.False(result.AccountsCollected);
        Assert.False(result.AliasesCollected);
    }
}
