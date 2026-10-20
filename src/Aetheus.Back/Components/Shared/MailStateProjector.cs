// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Shared;

/// <summary>Map heartbeat mail state consistently between persistence and server-detail
/// DTOs.</summary>
internal static class MailStateProjector
{
    public static MailState? ToState(int serverId, MailDataDto mail) => mail.IsInstalled ? new MailState
    {
        ServerId = serverId,
        PostfixVersion = mail.PostfixVersion,
        DovecotVersion = mail.DovecotVersion,
        IsPostfixRunning = mail.IsPostfixRunning,
        IsDovecotRunning = mail.IsDovecotRunning,
        QueueSize = mail.QueueSize,
        Hostname = mail.Hostname,
        IsOpenDkimRunning = mail.IsOpenDkimRunning,
        IsManagedByAetheus = mail.IsManagedByAetheus,
        HelperVersion = mail.HelperVersion,
        TlsCertPath = mail.Tls.CertPath,
        TlsIsReadable = mail.Tls.IsReadable,
        TlsSubject = mail.Tls.Subject,
        TlsIssuer = mail.Tls.Issuer,
        TlsExpiresAt = mail.Tls.ExpiresAt,
        TlsIsSelfSigned = mail.Tls.IsSelfSigned,
        SpamFilterName = mail.SpamFilter.Name,
        IsSpamFilterInstalled = mail.SpamFilter.IsInstalled,
        IsSpamFilterRunning = mail.SpamFilter.IsRunning,
        SpamFilterVersion = mail.SpamFilter.Version,
        SpamRejectScore = mail.SpamFilter.RejectScore,
        SpamAddHeaderScore = mail.SpamFilter.AddHeaderScore,
        SpamGreylistScore = mail.SpamFilter.GreylistScore,
        DiagnosticsJson = mail.Diagnostics.Count == 0 ? null : JsonSerializer.Serialize(mail.Diagnostics),
        DeliveryJson = mail.Delivery.IsEmpty ? null : JsonSerializer.Serialize(mail.Delivery)
    } : null;

    public static MailDataDto ToDto(MailState? state) => state is null ? new MailDataDto() : new MailDataDto
    {
        IsInstalled = true,
        IsPostfixRunning = state.IsPostfixRunning,
        IsDovecotRunning = state.IsDovecotRunning,
        PostfixVersion = state.PostfixVersion,
        DovecotVersion = state.DovecotVersion,
        QueueSize = state.QueueSize,
        Hostname = state.Hostname,
        IsOpenDkimRunning = state.IsOpenDkimRunning,
        IsManagedByAetheus = state.IsManagedByAetheus,
        HelperVersion = state.HelperVersion,
        Tls = new MailTlsStateDto
        {
            CertPath = state.TlsCertPath,
            IsReadable = state.TlsIsReadable,
            Subject = state.TlsSubject,
            Issuer = state.TlsIssuer,
            ExpiresAt = state.TlsExpiresAt,
            IsSelfSigned = state.TlsIsSelfSigned
        },
        SpamFilter = new MailSpamFilterStateDto
        {
            Name = state.SpamFilterName,
            IsInstalled = state.IsSpamFilterInstalled,
            IsRunning = state.IsSpamFilterRunning,
            Version = state.SpamFilterVersion,
            RejectScore = state.SpamRejectScore,
            AddHeaderScore = state.SpamAddHeaderScore,
            GreylistScore = state.SpamGreylistScore
        },
        Diagnostics = string.IsNullOrEmpty(state.DiagnosticsJson)
            ? []
            : JsonSerializer.Deserialize<List<string>>(state.DiagnosticsJson) ?? [],
        Delivery = string.IsNullOrEmpty(state.DeliveryJson)
            ? new MailDeliveryStateDto()
            : JsonSerializer.Deserialize<MailDeliveryStateDto>(state.DeliveryJson) ?? new MailDeliveryStateDto()
    };
}
