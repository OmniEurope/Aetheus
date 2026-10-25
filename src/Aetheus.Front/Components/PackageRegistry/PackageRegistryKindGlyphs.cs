// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.PackageRegistry;

/// <summary>
/// Small single-colour marks for the NuGet, npm and Maven badges, drawn by Aetheus on a 24 unit grid
/// (OE ships no brand logos). They are simple geometric shapes of our own (a block N, three circles, a block M)
/// that evoke each ecosystem without copying a trademarked artwork, so no third-party licence applies.
/// </summary>
public static class PackageRegistryKindGlyphs
{
    private const int ViewBox = 24;

    private static readonly OmniIconGlyph NuGetGlyph = new(
        "M15 9.5a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11zM7 3.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7zM5.5 15a2 2 0 1 0 0 4 2 2 0 0 0 0-4z",
        ViewBox);

    private static readonly OmniIconGlyph NpmGlyph = new("M4 20V4h4l8 11V4h4v16h-4L8 9v11z", ViewBox);

    private static readonly OmniIconGlyph MavenGlyph = new("M3 20V4h3.5L12 12.5 17.5 4H21v16h-3.5v-9.5L12 19 6.5 10.5V20z", ViewBox);

    public static OmniIconGlyph For(PackageRegistryKind kind) => kind switch
    {
        PackageRegistryKind.NuGet => NuGetGlyph,
        PackageRegistryKind.Npm => NpmGlyph,
        _ => MavenGlyph
    };
}
