// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// <see cref="RequiredAttribute"/> for a field of <see cref="ServerSetupDialogModel"/> that only the
/// Portsentry setup shows: empty is refused when the model's <see cref="ServerSetupDialogModel.Kind"/>
/// is <see cref="ServerSetupKind.Portsentry"/>, accepted otherwise.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class RequiredForPortsentryAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (validationContext.ObjectInstance is not ServerSetupDialogModel { Kind: ServerSetupKind.Portsentry }
            || !string.IsNullOrWhiteSpace(value as string))
            return ValidationResult.Success;

        return new ValidationResult(ErrorMessageString,
            validationContext.MemberName is { } member ? [member] : null);
    }
}
