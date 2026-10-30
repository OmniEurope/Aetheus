// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Shared.Components.Organizations;
namespace Aetheus.Front.Components.Organizations;

internal sealed class OrganizationFormModel
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(100)]
    [RegularExpression("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$",
        ErrorMessage = "SlugFormatError")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    internal CreateOrganizationRequest ToCreateRequest() => new()
    {
        Name = Name,
        Slug = Slug,
        Description = Description
    };

    internal UpdateOrganizationRequest ToUpdateRequest() => new()
    {
        Name = Name,
        Slug = Slug,
        Description = Description
    };
}
