// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The diagnostic of a security or memory alert in the recipient's language, built from the structured fields
/// of the payload (the SSH changes, the sudoers files, the killed processes) and the notification resources. The
/// producers' own <c>Message</c> is an English sentence for logs and the live toast: a mail never shows it.
/// </summary>
internal static class AlertDiagnosticText
{
    private const string OomKill = "OomKill";
    private const string SudoersDrift = "SudoersDrift";
    private const string SshConfigChange = "SshConfigChange";

    /// <summary>The localized diagnostic of the alert, or null when the payload holds nothing to word it from.</summary>
    public static string? Build(JsonElement? root, string language)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("Metric", out var metric) || metric.ValueKind != JsonValueKind.String)
            return null;
        return metric.GetString() switch
        {
            OomKill => Oom(obj, language),
            SudoersDrift => Sudoers(obj, language),
            SshConfigChange => Ssh(obj, language),
            _ => null
        };
    }

    private static string? Sudoers(JsonElement obj, string language) =>
        Strings(obj, "Files") is { Count: > 0 } files
            ? Format(language, "SudoersDriftMessage", string.Join(", ", files))
            : null;

    private static string? Oom(JsonElement obj, string language)
    {
        var victims = Strings(obj, "Victims") ?? [];
        var kills = obj.TryGetProperty("KillCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value)
            ? value : victims.Count;
        if (kills <= 0 && victims.Count == 0) return null;
        if (victims.Count == 0)
            return kills == 1
                ? NotificationEmailText.Get("OomKillUnnamedOne", language)
                : Format(language, "OomKillUnnamedMany", kills.ToString(CultureInfo.GetCultureInfo(language)));
        var unnamed = kills - victims.Count;
        return unnamed > 0
            ? Format(language, "OomKillNamedMore", string.Join(", ", victims), unnamed.ToString(CultureInfo.GetCultureInfo(language)))
            : Format(language, "OomKillNamed", string.Join(", ", victims));
    }

    private static string? Ssh(JsonElement obj, string language)
    {
        if (!obj.TryGetProperty("Changes", out var changes) || changes.ValueKind != JsonValueKind.Array) return null;
        var lines = new List<string>();
        foreach (var change in changes.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object || !change.TryGetProperty("Code", out var code)
                || code.ValueKind != JsonValueKind.String
                || NotificationEmailText.Find("SshChange" + code.GetString(), language) is not { } pattern)
                continue;
            var args = Strings(change, "Args") ?? [];
            lines.Add(string.Format(CultureInfo.GetCultureInfo(language), pattern, [.. args]));
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string Format(string language, string key, params object[] args) =>
        string.Format(CultureInfo.GetCultureInfo(language), NotificationEmailText.Get(key, language), args);

    /// <summary>A property holding a list of texts, or one text joined with commas by the producer.</summary>
    private static List<string>? Strings(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty)],
            JsonValueKind.String => [.. (value.GetString() ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries)],
            _ => null
        };
    }
}
