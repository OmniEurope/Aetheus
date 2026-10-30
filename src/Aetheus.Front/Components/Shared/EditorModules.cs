// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The same-origin module that drives OE's Monaco components (<c>OmniCodeEditor</c>, <c>OmniDiffViewer</c>) from inside
/// the CSP-friendly frame, handed to their <c>InteropModulePath</c>. Bump the <c>v</c> query when the module changes,
/// so a browser that cached the previous one fetches the new one.
/// </summary>
public static class EditorModules
{
    /// <summary>Path of <c>wwwroot/js/omni-code-editor-frame.js</c>, relative to the page.</summary>
    public const string Frame = "./js/omni-code-editor-frame.js?v=2";
}
