// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class MailAlias
{
    public int Id { get; set; }
    public int MailDomainId { get; set; }
    public string SourceEmail { get; set; } = string.Empty;
    public string DestinationEmail { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public MailRecordSource Source { get; set; } = MailRecordSource.Aetheus;

    /// <summary>Set by the heartbeat reconciliation when the server stopped reporting the alias.</summary>
    public DateTime? MissingSince { get; set; }

    // Navigation
    public MailDomain MailDomain { get; set; } = null!;
}
