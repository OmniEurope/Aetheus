// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// An organization's exclusion rule of the system logs (R-28): the entries whose message contains
/// <see cref="Pattern"/> (and whose category contains <see cref="Category"/> when it is not empty) move to the
/// second tab of the logs page. The rule filters the view only; the log files keep every line.
/// </summary>
public class SystemLogExclusionRule
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}
