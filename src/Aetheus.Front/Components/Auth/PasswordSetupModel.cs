// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Components.Auth;

/// <summary>The password chosen from a welcome mail's link (R-14), typed twice.</summary>
public sealed class PasswordSetupModel
{
    [Required]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword))]
    public string ConfirmPassword { get; set; } = string.Empty;
}
