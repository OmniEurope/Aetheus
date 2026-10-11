// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class BranchProtectionRule
{
    private BranchProtectionLevel _protectionLevel;

    public int Id { get; set; }
    public int GitInternalRepoId { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public bool PreventDeletion { get; set; } = true;
    public bool PreventForcePush { get; set; } = true;

    /// <summary>Decision D13 (BACK-013): who may move a matching branch. EF materializes the field
    /// directly, so loading a row leaves both columns as stored.</summary>
    public BranchProtectionLevel ProtectionLevel
    {
        get => _protectionLevel;
        set
        {
            _protectionLevel = value;
            RequirePullRequest = value != BranchProtectionLevel.NotProtected;
        }
    }

    /// <summary>
    /// Expand step of D13 (migration <c>BranchProtectionLevels</c>): the column the previous colour (V-1)
    /// still reads and enforces. Nothing in this version reads it; setting <see cref="ProtectionLevel"/>
    /// keeps it in step, so V-1 still refuses a direct push to a branch this version protects. The
    /// contract step drops it (docs/contracts/deployment.md, pending contract steps).
    /// </summary>
    public bool RequirePullRequest { get; private set; }

    public DateTime CreatedAt { get; set; }

    // Navigation
    public GitInternalRepo Repo { get; set; } = null!;
}
