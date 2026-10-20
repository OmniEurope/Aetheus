// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class MailAccount
{
    public int Id { get; set; }
    public int MailDomainId { get; set; }
    public string Email { get; set; } = string.Empty;
    public int QuotaMb { get; set; } = 1024;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public MailRecordSource Source { get; set; } = MailRecordSource.Aetheus;

    /// <summary>Set by the heartbeat reconciliation when the server stopped reporting the mailbox.</summary>
    public DateTime? MissingSince { get; set; }

    /// <summary>Maildir size from the last quota report, in MiB (rounded up).</summary>
    public int UsedMb { get; set; }
    public DateTime? UsageMeasuredAt { get; set; }

    // Navigation
    public MailDomain MailDomain { get; set; } = null!;
}
