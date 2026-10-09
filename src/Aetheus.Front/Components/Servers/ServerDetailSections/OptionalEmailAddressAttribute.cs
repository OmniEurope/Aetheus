// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// <see cref="EmailAddressAttribute"/> for an optional address: an empty or blank value is accepted
/// (the field was left empty, or the dialog does not show it), any other value must be an address.
/// The plain attribute refuses an empty string, which blocked a form whose address field is optional
/// or hidden (FRONT-019).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class OptionalEmailAddressAttribute : ValidationAttribute
{
    private static readonly EmailAddressAttribute Address = new();

    public override bool IsValid(object? value) =>
        value is not string text || string.IsNullOrWhiteSpace(text) || Address.IsValid(text.Trim());
}
