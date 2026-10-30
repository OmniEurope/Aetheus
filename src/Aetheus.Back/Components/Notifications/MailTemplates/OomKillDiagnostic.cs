// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The diagnostic of an out-of-memory alert that carries the detail of each kill (the <c>Kills</c> array of
/// the payload): whether a container limit or the whole host ran out of memory, which container, and how
/// much memory the killed process held. A payload without that array (an alert raised before the kinds,
/// or from an older agent) has none of it and keeps the plain wording of <see cref="AlertDiagnosticText"/>.
/// </summary>
internal static class OomKillDiagnostic
{
    /// <summary>The localized lines, one per named kill, or null when the payload holds no detail.</summary>
    public static string? Build(JsonElement payload, long killCount, string language)
    {
        var kills = Read(payload);
        if (kills.Count == 0) return null;
        var culture = CultureInfo.GetCultureInfo(language);
        var lines = kills.Select(kill => Line(kill, language, culture)).ToList();
        var unnamed = killCount - kills.Count;
        if (unnamed > 0)
            lines.Add(string.Format(culture, NotificationEmailText.Get("OomKillMoreUnnamed", language), unnamed.ToString(culture)));
        return string.Join("\n", lines);
    }

    // The Notifications module reads the payload only; it does not reference the Servers module's event.
    private sealed record Kill(
        string Process, int Pid, OomKillKind Kind, string? Container, string? Cgroup,
        long? AnonRssKb, long? FileRssKb, long? ShmemRssKb)
    {
        public long? ResidentKb => AnonRssKb is null && FileRssKb is null && ShmemRssKb is null
            ? null
            : (AnonRssKb ?? 0) + (FileRssKb ?? 0) + (ShmemRssKb ?? 0);
    }

    private static List<Kill> Read(JsonElement payload)
    {
        var kills = new List<Kill>();
        if (!payload.TryGetProperty("Kills", out var array) || array.ValueKind != JsonValueKind.Array) return kills;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "Process") is not { } process || Number(item, "Pid") is not { } pid)
                continue;
            var kind = Enum.TryParse<OomKillKind>(Text(item, "Kind"), ignoreCase: true, out var parsed)
                ? parsed
                : OomKillKind.Unknown;
            kills.Add(new Kill(
                process, (int)pid, kind, Text(item, "Container"), Text(item, "Cgroup"),
                Number(item, "AnonRssKb"), Number(item, "FileRssKb"), Number(item, "ShmemRssKb")));
        }
        return kills;
    }

    private static string Line(Kill kill, string language, CultureInfo culture)
    {
        var head = kill.Kind switch
        {
            OomKillKind.Cgroup when kill.Container is not null => Format("OomKillHeadMemcgContainer", language, culture, kill.Container),
            OomKillKind.Cgroup when kill.Cgroup is not null => Format("OomKillHeadMemcgPath", language, culture, kill.Cgroup),
            OomKillKind.Cgroup => Format("OomKillHeadMemcg", language, culture),
            OomKillKind.Global => Format("OomKillHeadGlobal", language, culture),
            OomKillKind.Cpuset or OomKillKind.MemoryPolicy => Format("OomKillHeadRestricted", language, culture),
            _ => Format("OomKillHeadUnknown", language, culture)
        };
        return Format("OomKillKilledLine", language, culture, head, $"{kill.Process} (pid {kill.Pid})", Memory(kill, language, culture));
    }

    private static string Memory(Kill kill, string language, CultureInfo culture)
    {
        if (kill.ResidentKb is not { } resident) return string.Empty;
        var parts = new List<string>(3);
        if (kill.AnonRssKb is { } anon) parts.Add(Format("OomKillMemAnon", language, culture, Mb(anon, culture)));
        if (kill.FileRssKb is { } file) parts.Add(Format("OomKillMemFile", language, culture, Mb(file, culture)));
        if (kill.ShmemRssKb is { } shmem) parts.Add(Format("OomKillMemShmem", language, culture, Mb(shmem, culture)));
        return Format("OomKillMemory", language, culture, Mb(resident, culture), string.Join(", ", parts));
    }

    private static string Mb(long kb, CultureInfo culture) => ((kb + 512) / 1024).ToString("N0", culture); // nearest MB, halves up, as the alert message rounds

    private static string Format(string key, string language, CultureInfo culture, params object[] args) =>
        string.Format(culture, NotificationEmailText.Get(key, language), args);

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;

    private static long? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
}
