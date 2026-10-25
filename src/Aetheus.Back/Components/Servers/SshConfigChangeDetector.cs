// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Servers;

/// <summary>One difference between two SSH snapshots. <c>Info</c> is recorded but never alerted. <c>Code</c> and
/// <c>Args</c> are the language-neutral form of <c>Message</c>: a mail words the change itself in the recipient's language.</summary>
internal sealed record SshConfigChange(string Severity, string Message, string Code = "", IReadOnlyList<string>? Args = null)
{
    public const string Critical = "Critical";
    public const string Warning = "Warning";
    public const string Info = "Info";

    public bool IsAlert => Severity is Critical or Warning;
}

/// <summary>
/// Compares a reported SSH snapshot with the stored one. Root login or password login opened, and
/// a key added to an authorized_keys file, are critical; a value or a file that became unreadable
/// is a warning, because the watch has gone blind there; a file readable again only becomes the
/// new reference, since what changed while it was unreadable cannot be told.
/// </summary>
internal static class SshConfigChangeDetector
{
    public static List<SshConfigChange> Compare(SshConfigDataDto before, SshConfigDataDto after)
    {
        var changes = new List<SshConfigChange>();
        CompareSetting(changes, "PermitRootLogin", before.PermitRootLogin, after.PermitRootLogin, criticalValue: "yes");
        CompareSetting(changes, "PasswordAuthentication", before.PasswordAuthentication, after.PasswordAuthentication, criticalValue: "yes");
        CompareSetting(changes, "AuthorizedKeysFile", before.AuthorizedKeysFile, after.AuthorizedKeysFile, criticalValue: null);
        CompareKeys(changes, before.AuthorizedKeys, after.AuthorizedKeys);
        return changes;
    }

    private static void CompareSetting(
        List<SshConfigChange> changes, string name, SshSettingDto before, SshSettingDto after, string? criticalValue)
    {
        if (!string.Equals(before.Value, after.Value, StringComparison.Ordinal))
        {
            if (after.Value is null)
            {
                changes.Add(new(SshConfigChange.Warning,
                    $"{name} is now unknown: a configuration file read before it became unreadable to the agent",
                    "SettingUnknown", [name]));
            }
            else
            {
                var severity = criticalValue is null || string.Equals(after.Value, criticalValue, StringComparison.Ordinal)
                    ? SshConfigChange.Critical
                    : SshConfigChange.Warning;
                changes.Add(before.Value is null
                    ? new(severity, $"{name} is readable again: {after.Value}", "SettingReadable", [name, after.Value])
                    : new(severity, $"{name} changed to {after.Value} (was {before.Value})", "SettingChanged",
                        [name, after.Value, before.Value]));
            }
        }
        if (!before.OverriddenInMatch && after.OverriddenInMatch)
            changes.Add(new(SshConfigChange.Warning, $"{name} is now also set in a Match block", "SettingMatchBlock", [name]));
    }

    private static void CompareKeys(
        List<SshConfigChange> changes, List<AuthorizedKeysFileDto> before, List<AuthorizedKeysFileDto> after)
    {
        var previous = before
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var file in after)
        {
            previous.TryGetValue(file.Path, out var old);
            CompareFile(changes, old, file);
        }
        // R-50: an account deleted on the host drops out of the report; its files vanish instead of turning Absent.
        foreach (var gone in GonePaths(before, after))
        {
            var old = previous[gone];
            if (old.State == AuthorizedKeysFileState.Read)
                changes.Add(new(SshConfigChange.Warning, $"{gone} was removed", "KeysFileRemoved", [gone]));
            else if (old.State == AuthorizedKeysFileState.Unreadable)
                changes.Add(new(SshConfigChange.Info, $"{gone} is no longer watched", "KeysFileNoLongerWatched", [gone]));
        }
    }

    /// <summary>The authorized_keys paths the previous snapshot held and the new one no longer reports.</summary>
    public static List<string> GonePaths(List<AuthorizedKeysFileDto> before, List<AuthorizedKeysFileDto> after)
    {
        var reported = after.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        return [.. before
            .Where(f => f.State != AuthorizedKeysFileState.Absent && !reported.Contains(f.Path))
            .Select(f => f.Path)
            .Distinct(StringComparer.Ordinal)];
    }

    private static void CompareFile(List<SshConfigChange> changes, AuthorizedKeysFileDto? old, AuthorizedKeysFileDto file)
    {
        var oldState = old?.State ?? AuthorizedKeysFileState.Absent;
        switch (oldState, file.State)
        {
            case (AuthorizedKeysFileState.Unreadable, AuthorizedKeysFileState.Read):
                changes.Add(new(SshConfigChange.Info,
                    $"{file.Path} is readable again: its {file.Keys.Count} key(s) become the reference",
                    "KeysFileReadable", [file.Path, file.Keys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
                return;
            case (_, AuthorizedKeysFileState.Unreadable) when oldState != AuthorizedKeysFileState.Unreadable:
                changes.Add(old is null
                    ? new(SshConfigChange.Warning, $"{file.Path} is now watched but unreadable by the agent: its keys are unknown",
                        "KeysFileWatchedUnreadable", [file.Path])
                    : new(SshConfigChange.Warning, $"{file.Path} is no longer readable by the agent: its keys are unknown",
                        "KeysFileUnreadable", [file.Path]));
                return;
            case (AuthorizedKeysFileState.Read, AuthorizedKeysFileState.Absent):
                changes.Add(new(SshConfigChange.Warning, $"{file.Path} was removed", "KeysFileRemoved", [file.Path]));
                return;
            case (_, AuthorizedKeysFileState.Read):
                var known = (old?.Keys ?? []).Select(k => k.Fingerprint).ToHashSet(StringComparer.Ordinal);
                var now = file.Keys.Select(k => k.Fingerprint).ToHashSet(StringComparer.Ordinal);
                foreach (var key in file.Keys.Where(k => !known.Contains(k.Fingerprint)).DistinctBy(k => k.Fingerprint))
                    changes.Add(new(SshConfigChange.Critical,
                        $"new key added to {file.Path} ({key.KeyType} {key.Fingerprint})",
                        "KeyAdded", [file.Path, key.KeyType, key.Fingerprint]));
                foreach (var key in (old?.Keys ?? []).Where(k => !now.Contains(k.Fingerprint)).DistinctBy(k => k.Fingerprint))
                    changes.Add(new(SshConfigChange.Warning,
                        $"key removed from {file.Path} ({key.KeyType} {key.Fingerprint})",
                        "KeyRemoved", [file.Path, key.KeyType, key.Fingerprint]));
                return;
        }
    }
}
