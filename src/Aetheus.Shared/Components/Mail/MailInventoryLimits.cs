// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Mail;

/// <summary>Caps on the mail inventory an agent reports in one heartbeat. The collector truncates at these
/// limits (and says so in a diagnostic) so the heartbeat never exceeds the DTO validation bounds.</summary>
public static class MailInventoryLimits
{
    public const int MaxDomains = 500;
    public const int MaxAccounts = 5000;
    public const int MaxAliases = 5000;
    public const int MaxDiagnostics = 50;
    public const int MaxDeferredMessages = 20;
    public const int MaxDeferralReasonLength = 300;
    public const int MaxHostIpv6Addresses = 8;
}
