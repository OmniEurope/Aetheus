// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class Organization
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>When on, a project of this organization whose internal repository gets its first commit also gets a
    /// <c>develop</c> branch cut from the default branch (R-76, owner decision 2026-10-28). Off by default: only the
    /// platform organization "Aetheus" starts with it on.</summary>
    public bool AutoCreateDevelopBranch { get; set; }

    public List<OrganizationMember> Members { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
}
