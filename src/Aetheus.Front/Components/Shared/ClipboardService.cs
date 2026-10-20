// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Centralizes Clipboard API writes with one success toast or a graceful warning for
/// unavailable or denied clipboard access.</summary>
public class ClipboardService(IJSRuntime js, NotifyHelper toast)
{
    /// <summary>
    /// Copies <paramref name="text"/> and shows a success toast. <paramref name="successDetail"/> is the
    /// already-resolved detail line (a localized string or the copied value); pass <c>null</c> for none.
    /// </summary>
    public async Task CopyAsync(string? text, string? successDetail = null)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text ?? string.Empty);
            toast.Notify(OmniSeverity.Success, "Copied", successDetail ?? string.Empty);
        }
        catch (Exception ex)
        {
            // Clipboard API unavailable (insecure context / denied permission) - warn once, never throw.
            toast.Warning("CopyFailed", "ClipboardUnavailable");
            System.Diagnostics.Debug.WriteLine($"[Clipboard] failed: {ex.Message}");
        }
    }
}
