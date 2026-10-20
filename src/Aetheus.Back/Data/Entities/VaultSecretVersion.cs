// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class VaultSecretVersion
{
    public int Id { get; set; }
    public int VaultSecretId { get; set; }
    public string Key { get; set; } = string.Empty;

    // Secret version history stores no secret values. The unmapped EncryptedValue database column
    // retains an empty default for blue-green compatibility; see pending contract steps in
    // docs/contracts/deployment.md.
    public int Version { get; set; }
    public DateTime ChangedAt { get; set; }
    public ChangeType ChangeType { get; set; }

    /// <summary>Who made the change. Null on versions written before it was recorded.</summary>
    public string? ChangedBy { get; set; }

    // Navigation
    public VaultSecret? VaultSecret { get; set; }
}
