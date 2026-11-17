// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-51 (user decision 2026-11-03): the wizard no longer offers the "container images" tile nor the mandatory archives
/// field, which blocked "Next" and was not understood. Nothing in the wizard asks for images now; the security pipeline
/// keeps the template's defaults (image checks off). The option may come back later without any typing or blocking,
/// switched on by itself when the project is detected to deliver images; <see cref="PipelineSetupYamlInput.ImageArchives"/>
/// stays the seam it will use.
/// </summary>
internal static class PipelineSetupImages
{
    /// <summary>The unpacked name of an archive, as <c>prepare-image-inputs</c> writes it (<c>x.tar.gz</c> becomes <c>x.tar</c>).</summary>
    public static string UnpackedName(string archive) => archive[..^".gz".Length];
}
