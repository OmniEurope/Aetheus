// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests;

/// <summary>The TLS line of the mail checklist is derived from the last heartbeat inventory only: each state of
/// the certificate maps to one verdict and one stable detail code the UI localises.</summary>
public sealed class MailCertificateDiagnosticTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    private static MailState State(
        string path = "/etc/letsencrypt/live/mx/fullchain.pem",
        bool readable = true,
        bool selfSigned = false,
        DateTime? expiresAt = null) => new()
        {
            TlsCertPath = path,
            TlsIsReadable = readable,
            TlsIsSelfSigned = selfSigned,
            TlsExpiresAt = expiresAt
        };

    [Fact]
    public void Certificate_NoCertificatePath_IsMissing()
    {
        var item = MailDiagnosticsService.Certificate(State(path: string.Empty), Now);

        Assert.Equal("tls", item.Key);
        Assert.Equal(MailCheckVerdict.Missing, item.Verdict);
    }

    [Fact]
    public void Certificate_AnUnreadableFile_IsUnknownAndSaysSo()
    {
        var item = MailDiagnosticsService.Certificate(State(readable: false, expiresAt: Now.AddDays(90)), Now);

        Assert.Equal(MailCheckVerdict.Unknown, item.Verdict);
        Assert.Equal("unreadable", item.Detail);
    }

    [Fact]
    public void Certificate_ASelfSignedCertificate_IsAMismatchEvenWhenNotExpiring()
    {
        var item = MailDiagnosticsService.Certificate(State(selfSigned: true, expiresAt: Now.AddDays(90)), Now);

        Assert.Equal(MailCheckVerdict.Mismatch, item.Verdict);
        Assert.Equal("self-signed", item.Detail);
    }

    [Fact]
    public void Certificate_ExpiringWithinTheWarningWindow_IsAMismatchExpiring()
    {
        var item = MailDiagnosticsService.Certificate(State(expiresAt: Now + MailDiagnosticsService.CertificateWarning - TimeSpan.FromHours(1)), Now);

        Assert.Equal(MailCheckVerdict.Mismatch, item.Verdict);
        Assert.Equal("expiring", item.Detail);
    }

    [Fact]
    public void Certificate_AlreadyExpired_IsAMismatchExpired()
    {
        var item = MailDiagnosticsService.Certificate(State(expiresAt: Now.AddDays(-1)), Now);

        Assert.Equal(MailCheckVerdict.Mismatch, item.Verdict);
        Assert.Equal("expired", item.Detail);
    }

    [Fact]
    public void Certificate_ExpiringExactlyAtTheWarningLimit_IsStillOk()
    {
        var item = MailDiagnosticsService.Certificate(State(expiresAt: Now + MailDiagnosticsService.CertificateWarning), Now);

        Assert.Equal(MailCheckVerdict.Ok, item.Verdict);
    }

    [Fact]
    public void Certificate_ValidWithADistantExpiry_IsOk()
    {
        var item = MailDiagnosticsService.Certificate(State(expiresAt: Now.AddDays(80)), Now);

        Assert.Equal(MailCheckVerdict.Ok, item.Verdict);
        Assert.Empty(item.Detail);
    }

    [Fact]
    public void Certificate_ValidWithNoKnownExpiry_IsOk()
    {
        var item = MailDiagnosticsService.Certificate(State(), Now);

        Assert.Equal(MailCheckVerdict.Ok, item.Verdict);
    }
}
