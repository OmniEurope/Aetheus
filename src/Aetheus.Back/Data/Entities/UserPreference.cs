// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// One preference of one user (recette R1-66): an opaque JSON value under a key chosen by the browser
/// (OE keys such as <c>omni.appearance</c>, Aetheus keys such as <c>aetheus.appearance</c>). A null
/// <see cref="Value"/> is a tombstone: the preference was removed, and the row stays so the removal
/// reaches the user's other devices. <see cref="ChangedAt"/> is the date of the change as the writing
/// device stamped it; the server keeps the later of two writes. It is not named UpdatedAt on purpose:
/// AppDbContext stamps every UpdatedAt with the server clock on save, which would erase that date.
/// </summary>
public class UserPreference
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTime ChangedAt { get; set; }

    public User User { get; set; } = default!;
}
