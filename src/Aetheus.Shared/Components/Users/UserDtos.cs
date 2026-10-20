// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Users;

public sealed record UserDto
{
    public int Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public bool TotpEnabled { get; init; }
    public bool MustChangePassword { get; init; }
    public List<string> Roles { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record CreateUserRequest
{
    [Required]
    [StringLength(50)]
    public string Username { get; init; } = string.Empty;

    [Required]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength)]
    public string Password { get; init; } = string.Empty;

    [StringLength(200)]
    [EmailAddress]
    public string? Email { get; init; }

    /// <summary>
    /// When true (the default for admin-provisioned accounts), the new user is forced to change
    /// their password at first login before they can use the app.
    /// </summary>
    public bool MustChangePassword { get; init; } = true;

    [MaxLength(50)]
    [MaxItemStringLength(50)]
    public List<string> Roles { get; init; } = [];
}

public sealed record UpdateUserRequest
{
    [Required]
    [StringLength(50)]
    public string Username { get; init; } = string.Empty;

    [StringLength(200)]
    [EmailAddress]
    public string? Email { get; init; }

    public bool IsActive { get; init; }

    /// <summary>
    /// S-FEAT-UE4K: when true, the user is forced to change their password at next login. Toggling it
    /// on from the edit screen re-arms the requirement (an admin password reset re-arms it too).
    /// </summary>
    public bool MustChangePassword { get; init; }

    [MaxLength(50)]
    [MaxItemStringLength(50)]
    public List<string> Roles { get; init; } = [];

    /// <summary> When this request deactivates a user who owns pipelines, the active user that
    /// takes them over first. Without it (or <see cref="ForceWithOwnedPipelines"/>) the
    /// deactivation is refused (409). </summary>
    [StringLength(50)]
    public string? TransferPipelinesToUsername { get; init; }

    /// <summary>Deactivates a pipeline owner without transferring the pipelines; their chained runs
    /// are then refused by the F-EXEC-1b gate until an administrator changes the owner.</summary>
    public bool ForceWithOwnedPipelines { get; init; }
}

public sealed record ChangeUserPasswordRequest
{
    /// <summary>
    /// Current password - required for self-service changes. May be left empty by an Admin
    /// performing a password reset for another user; the service enforces the right combination.
    /// </summary>
    [StringLength(100)]
    public string? CurrentPassword { get; init; }

    [Required]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength)]
    public string NewPassword { get; init; } = string.Empty;
}
