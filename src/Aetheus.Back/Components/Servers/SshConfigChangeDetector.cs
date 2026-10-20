// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Servers;

/// <summary>One difference between two SSH snapshots. <c>Info</c> is recorded but never alerted.</summary>
internal sealed record SshConfigChange(string Severity, string Message)
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
                    $"{name} is now unknown: a configuration file read before it became unreadable to the agent"));
            }
            else
            {
                var severity = criticalValue is null || string.Equals(after.Value, criticalValue, StringComparison.Ordinal)
                    ? SshConfigChange.Critical
                    : SshConfigChange.Warning;
                changes.Add(new(severity, before.Value is null
                    ? $"{name} is readable again: {after.Value}"
                    : $"{name} changed to {after.Value} (was {before.Value})"));
            }
        }
        if (!before.OverriddenInMatch && after.OverriddenInMatch)
            changes.Add(new(SshConfigChange.Warning, $"{name} is now also set in a Match block"));
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
    }

    private static void CompareFile(List<SshConfigChange> changes, AuthorizedKeysFileDto? old, AuthorizedKeysFileDto file)
    {
        var oldState = old?.State ?? AuthorizedKeysFileState.Absent;
        switch (oldState, file.State)
        {
            case (AuthorizedKeysFileState.Unreadable, AuthorizedKeysFileState.Read):
                changes.Add(new(SshConfigChange.Info,
                    $"{file.Path} is readable again: its {file.Keys.Count} key(s) become the reference"));
                return;
            case (_, AuthorizedKeysFileState.Unreadable) when oldState != AuthorizedKeysFileState.Unreadable:
                changes.Add(new(SshConfigChange.Warning, old is null
                    ? $"{file.Path} is now watched but unreadable by the agent: its keys are unknown"
                    : $"{file.Path} is no longer readable by the agent: its keys are unknown"));
                return;
            case (AuthorizedKeysFileState.Read, AuthorizedKeysFileState.Absent):
                changes.Add(new(SshConfigChange.Warning, $"{file.Path} was removed"));
                return;
            case (_, AuthorizedKeysFileState.Read):
                var known = (old?.Keys ?? []).Select(k => k.Fingerprint).ToHashSet(StringComparer.Ordinal);
                var now = file.Keys.Select(k => k.Fingerprint).ToHashSet(StringComparer.Ordinal);
                foreach (var key in file.Keys.Where(k => !known.Contains(k.Fingerprint)).DistinctBy(k => k.Fingerprint))
                    changes.Add(new(SshConfigChange.Critical,
                        $"new key added to {file.Path} ({key.KeyType} {key.Fingerprint})"));
                foreach (var key in (old?.Keys ?? []).Where(k => !now.Contains(k.Fingerprint)).DistinctBy(k => k.Fingerprint))
                    changes.Add(new(SshConfigChange.Warning,
                        $"key removed from {file.Path} ({key.KeyType} {key.Fingerprint})"));
                return;
        }
    }
}
