// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Shared;

/// <summary>Mail-manage helper versions that gate a capability. Kept below the Mail and Servers modules
/// because the heartbeat handling (Servers) and the mail operations (Mail) both read them.</summary>
internal static class MailHelperVersions
{
    /// <summary>DEP-005: the first mail-manage helper version that writes and enforces the mailbox quota.</summary>
    internal const int QuotaMinimum = 5;
}
