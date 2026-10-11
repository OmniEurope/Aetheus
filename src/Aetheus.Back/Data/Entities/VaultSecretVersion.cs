// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class VaultSecretVersion
{
    public int Id { get; set; }
    public int VaultSecretId { get; set; }
    public string Key { get; set; } = string.Empty;

    // Recette R-287: the history keeps no secret value. The former EncryptedValue column is no longer
    // mapped (20261010094003_UnmapVaultSecretVersionEncryptedValue gave it a database default of '' for the
    // previous colour); a later release drops it (docs/contracts/deployment.md, pending contract steps).
    public int Version { get; set; }
    public DateTime ChangedAt { get; set; }
    public ChangeType ChangeType { get; set; }

    /// <summary>Recette R-287: who made the change. Null on versions written before it was recorded.</summary>
    public string? ChangedBy { get; set; }

    // Navigation
    public VaultSecret? VaultSecret { get; set; }
}
