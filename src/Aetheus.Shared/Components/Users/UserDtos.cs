// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Users;

/// <summary>The <see cref="Aetheus.Shared.Components.Shared.ApiError.Code"/> of a refused user creation or
/// edit, which the front says in the user's language (PLAN-003 P3-2).</summary>
public static class UserErrorCodes
{
    public const string EmailInUse = "UserEmailInUse";
}

public sealed record UserDto
{
    public int Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public bool TotpEnabled { get; init; }
    public bool MustChangePassword { get; init; }
    /// <summary>Site-wide roles (only Admin is honoured, ADR-059).</summary>
    public List<string> Roles { get; init; } = [];
    /// <summary>The roles held for one organization each (R-47).</summary>
    public List<OrganizationRoleAssignmentDto> OrganizationRoles { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }

    /// <summary>Only on the answer to a creation (R-14): what became of the welcome mail. The account
    /// exists whatever it says; null on every other read.</summary>
    public WelcomeMailResultDto? WelcomeMail { get; init; }
}

/// <summary>What became of the welcome mail of a created account (R-14), said as it happened.</summary>
public sealed record WelcomeMailResultDto
{
    public WelcomeMailStatus Status { get; init; }

    /// <summary>Why a mail was not attempted (<see cref="WelcomeMailStatus.NotConfigured"/>), so the page can say
    /// it in the reader's language; <see cref="Message"/> stays the technical detail.</summary>
    public WelcomeMailReason Reason { get; init; }

    /// <summary>The address the mail went to, or would have gone to.</summary>
    [StringLength(200)]
    public string? To { get; init; }

    /// <summary>Why nothing left, or the SMTP server's own text on a failure; the sender on a success.</summary>
    [StringLength(2000)]
    public string Message { get; init; } = string.Empty;
}

/// <summary>Why nothing was attempted for a <see cref="WelcomeMailStatus.NotConfigured"/> mail.</summary>
public enum WelcomeMailReason
{
    /// <summary>No specific reason: the status says it all.</summary>
    None = 0,

    /// <summary>No sender is set up: the platform mailbox is missing or incomplete.</summary>
    NoSender = 1,

    /// <summary>No public address of the application to link to.</summary>
    NoPublicAddress = 2
}

public enum WelcomeMailStatus
{
    /// <summary>The SMTP server accepted the mail.</summary>
    Sent = 1,

    /// <summary>Nothing was attempted: no sender, or no public address of the application to link to.</summary>
    NotConfigured = 2,

    /// <summary>The mail was attempted and not accepted, or could not be written.</summary>
    Failed = 3
}

public sealed record CreateUserRequest
{
    [Required]
    [StringLength(50)]
    public string Username { get; init; } = string.Empty;

    /// <summary>The sign-in identifier (PLAN-003 P3-2): required, unique case-insensitively. No password is
    /// given at creation (owner decision 2026-10-28, R-14): the welcome mail sent to this address carries the
    /// link with which the person chooses theirs.</summary>
    [Required]
    [StringLength(200)]
    [EmailAddress]
    public string? Email { get; init; }

    /// <summary>Site-wide roles: only Admin may be held site-wide; any other role is refused here
    /// and goes in <see cref="OrganizationRoles"/> (ADR-059).</summary>
    [MaxLength(50)]
    [MaxItemStringLength(50)]
    public List<string> Roles { get; init; } = [];

    /// <summary>The roles the new user holds, each for one organization; each makes the user a
    /// member of that organization (R-47).</summary>
    [MaxLength(200)]
    public List<OrganizationRoleAssignmentDto> OrganizationRoles { get; init; } = [];
}

public sealed record UpdateUserRequest
{
    [Required]
    [StringLength(50)]
    public string Username { get; init; } = string.Empty;

    /// <summary>The sign-in identifier (PLAN-003 P3-2): required, unique case-insensitively.</summary>
    [Required]
    [StringLength(200)]
    [EmailAddress]
    public string? Email { get; init; }

    public bool IsActive { get; init; }

    /// <summary>
    /// S-FEAT-UE4K: when true, the user is forced to change their password at next login. Toggling it
    /// on from the edit screen re-arms the requirement (an admin password reset re-arms it too).
    /// </summary>
    public bool MustChangePassword { get; init; }

    /// <summary>Site-wide roles: only Admin may be held site-wide (ADR-059).</summary>
    [MaxLength(50)]
    [MaxItemStringLength(50)]
    public List<string> Roles { get; init; } = [];

    /// <summary>The complete set of roles the user holds per organization (R-47), or null to leave
    /// them unchanged. An organization the set no longer names loses the user as a member.</summary>
    [MaxLength(200)]
    public List<OrganizationRoleAssignmentDto>? OrganizationRoles { get; init; }

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

/// <summary>A role held for one organization (ADR-059, R-47).</summary>
public sealed record OrganizationRoleAssignmentDto
{
    [Range(1, int.MaxValue)]
    public int OrganizationId { get; init; }

    [Required]
    [StringLength(50)]
    public string Role { get; init; } = string.Empty;

    /// <summary>Filled by the server on reads; ignored on writes.</summary>
    [StringLength(100)]
    public string? OrganizationName { get; init; }
}
