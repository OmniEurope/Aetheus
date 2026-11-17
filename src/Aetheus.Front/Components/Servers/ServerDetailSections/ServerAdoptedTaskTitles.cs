// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>One way to name an adopted task: when the backend name starts with <see cref="Prefix"/>, <see cref="Title"/>
/// gets what follows it (trimmed) and returns the title, or null to let the next rule try.</summary>
internal readonly record struct AdoptedTitleRule(string Prefix, Func<string, string?> Title);

/// <summary>
/// Recette R-12: the shared way a server page names a task it adopted on arrival (<see cref="ServerTaskFollower.AdoptedTitle"/>).
/// The backend names its tasks in English ("Cron delete - j1"); the page names the task it launches itself with its own
/// resources ("Delete cron job"). A page lists its rules, most specific first, and the first rule that matches and
/// answers wins. A name no rule knows gets null, and the follower then shows it as it is.
/// </summary>
internal static class ServerAdoptedTaskTitles
{
    /// <summary>The title of the first rule whose prefix starts <paramref name="taskName"/> and which answers.</summary>
    public static string? Resolve(string taskName, IReadOnlyList<AdoptedTitleRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!taskName.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var title = rule.Title(taskName[rule.Prefix.Length..].Trim());
            if (title is not null) return title;
        }
        return null;
    }

    /// <summary>The task whose name starts with <paramref name="prefix"/> is called by the resource <paramref name="key"/>.</summary>
    public static AdoptedTitleRule Key(IStringLocalizer<AppStrings> l, string prefix, string key) =>
        new(prefix, _ => l[key].Value);

    /// <summary>As <see cref="Key"/>, followed by what comes after the prefix: "Delete 8080/tcp".</summary>
    public static AdoptedTitleRule KeyWith(IStringLocalizer<AppStrings> l, string prefix, string key) =>
        new(prefix, rest => $"{l[key]} {rest}");

    /// <summary>As <see cref="KeyWith"/>, but only the part of what follows the prefix that comes before
    /// <paramref name="separator"/> (an alias "a@x → b@x" is called by its source).</summary>
    public static AdoptedTitleRule KeyWithHead(IStringLocalizer<AppStrings> l, string prefix, string key, string separator) =>
        new(prefix, rest =>
        {
            var cut = rest.IndexOf(separator, StringComparison.Ordinal);
            return $"{l[key]} {(cut < 0 ? rest : rest[..cut])}";
        });

    /// <summary>A name of the form "{Action} - {target}" (the target is optional): <paramref name="title"/> names the
    /// action, null when the first word is not an action of <typeparamref name="TAction"/>.</summary>
    public static string? Action<TAction>(string rest, Func<TAction, string?, string> title) where TAction : struct, Enum
    {
        var parts = rest.Split(" - ", 2, StringSplitOptions.TrimEntries);
        var word = parts[0];
        if (word.Length == 0 || !char.IsLetter(word[0]) || !Enum.TryParse<TAction>(word, out var action)) return null;
        return title(action, parts.Length > 1 ? parts[1] : null);
    }
}
