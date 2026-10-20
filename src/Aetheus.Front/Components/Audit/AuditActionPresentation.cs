// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Audit;

/// <summary>Share localized audit-action labels and change-type colours across views, with
/// AuditActionLabelCoverageTests checking resource coverage. Unknown actions fall back to readable
/// names rather than raw resource keys.</summary>
internal static partial class AuditActionPresentation
{
    // Words of the action name, in reading order ("RotatedIngestKeyForDeploy" -> Rotated, Ingest...).
    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex Words();

    private static readonly HashSet<string> DangerWords = new(StringComparer.Ordinal)
    {
        "Deleted", "Delete", "Removed", "Remove", "Revoked", "Revoke", "Purged", "Uninstall", "Failed",
        "Blocked", "Ban", "Kick", "Retired", "Unregistered"
    };

    private static readonly HashSet<string> WarningWords = new(StringComparer.Ordinal)
    {
        "Rotated", "Reset", "Rollback", "Revealed", "Unmasked", "Anomaly", "Unlocked", "Disabled", "Disable",
        "Stop", "Unblock", "Unlisted", "Detached", "Optional", "Compensated", "Unlinked", "Recovery"
    };

    private static readonly HashSet<string> SuccessWords = new(StringComparer.Ordinal)
    {
        "Created", "Create", "Added", "Add", "Registered", "Enabled", "Enable", "Succeeded", "Published",
        "Login", "Confirmed", "Install", "Installed", "Setup", "Start", "Imported", "Authorized", "Revived",
        "Relisted", "Attached", "Linked", "Required", "Reconciled"
    };

    private static readonly HashSet<string> AccentWords = new(StringComparer.Ordinal)
    {
        "Updated", "Update", "Configured", "Configure", "Save", "Edit", "Changed", "Moved", "Merged", "Synced",
        "Adopted", "Advanced", "Applied", "Assigned", "Cloned", "Duplicated", "Restart", "Reload", "Renew"
    };

    /// <summary>The localized label, or the action name spelled out when no label exists yet.</summary>
    internal static string Label(IStringLocalizer<AppStrings> localizer, string action)
    {
        var text = localizer[$"AuditAction_{action}"];
        return text.ResourceNotFound ? Spell(action) : text.Value;
    }

    /// <summary>
    /// The badge colour from the kind of change, the strongest signal first: a failure, a deletion or
    /// a block is red even when the name also says "update" ("AgentUpdateConfirmationFailed").
    /// </summary>
    internal static OmniTone Badge(string action)
    {
        var words = Words().Matches(action).Select(match => match.Value).ToList();
        if (words.Any(DangerWords.Contains)) return OmniTone.Danger;
        if (words.Any(WarningWords.Contains)) return OmniTone.Warning;
        if (words.Any(SuccessWords.Contains)) return OmniTone.Success;
        if (words.Any(AccentWords.Contains)) return OmniTone.Accent;
        return OmniTone.Neutral;
    }

    /// <summary>"RotatedIngestKeyForDeploy" reads "Rotated ingest key for deploy"; "Role.Cloned" reads
    /// "Role: cloned".</summary>
    internal static string Spell(string action)
    {
        var parts = action.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => string.Join(' ', Words().Matches(part).Select(match => match.Value)))
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count == 0) return action;
        var text = string.Join(": ", parts);
        return text[..1] + LowerWords(text[1..]);
    }

    // Lower-cases each word but keeps acronyms ("DKIM", "AI") as written.
    private static string LowerWords(string text) =>
        Regex.Replace(text, @"\b([A-Z])([a-z]+)\b", match => char.ToLowerInvariant(match.Groups[1].Value[0]) + match.Groups[2].Value);
}
