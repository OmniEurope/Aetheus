// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.PackageRegistry;

public partial class PackageRegistryKindBadge : ComponentBase
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PackageRegistryKind Kind { get; set; }

    private string CssClass => $"package-kind-badge package-kind-badge--{Kind.ToString().ToLowerInvariant()}";
}
