// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerSshConfigCard
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The stored snapshot; null until the agent reported one.</summary>
    [Parameter] public SshConfigStatusDto? Status { get; set; }

    /// <summary>R-48: the watched files, grouped under the account that owns them (a file the host does not have is left out).</summary>
    private static IEnumerable<(string User, List<AuthorizedKeysFileDto> Files)> KeyAccounts(SshConfigDataDto snapshot) =>
        snapshot.AuthorizedKeys
            .Where(f => f.State != AuthorizedKeysFileState.Absent)
            .GroupBy(f => f.User, StringComparer.Ordinal)
            .Select(g => (g.Key, g.ToList()));

    private string SettingText(SshSettingDto setting) => setting.Value ?? L["SshUnknown"];

    /// <summary>R-50: an unknown value names the file the agent could not read (what it lacks), not a bare "Unknown".</summary>
    private string SourceText(SshConfigDataDto snapshot, SshSettingDto setting)
    {
        var source = setting.Value is null
            ? UnknownSourceText(snapshot)
            : setting.Source == "default" ? L["SshDefaultSource"].Value : setting.Source ?? string.Empty;
        return setting.OverriddenInMatch ? $"{source} {L["SshAlsoInMatch"]}" : source;
    }

    private string UnknownSourceText(SshConfigDataDto snapshot) => snapshot.UnreadableFiles.Count > 0
        ? string.Format(L["SshUnknownSourceFiles"], string.Join(", ", snapshot.UnreadableFiles))
        : L["SshUnknownSource"].Value;

    /// <summary>R-50: when the agent last sent its SSH report; a state stored before the date existed says so.</summary>
    private string LastReportText() => Status?.LastReportedAt is { } at
        ? string.Format(L["SshLastReport"], at.ToString("g"), RelativeTime.FormatAgo(L, at))
        : L["SshLastReportUnknown"].Value;

    private string FileStateText(AuthorizedKeysFileDto file) => file.State switch
    {
        AuthorizedKeysFileState.Read => string.Format(L["SshKeyCount"], file.Keys.Count),
        _ => L["SshUnknown"]
    };

    internal static OmniTone RootLoginTone(string? value) => value switch
    {
        null => OmniTone.Warning,
        "yes" => OmniTone.Danger,
        "no" => OmniTone.Success,
        _ => OmniTone.Neutral
    };

    internal static OmniTone PasswordTone(string? value) => value switch
    {
        null => OmniTone.Warning,
        "yes" => OmniTone.Warning,
        _ => OmniTone.Success
    };

    internal static OmniTone ChangeTone(string severity) => severity switch
    {
        "Critical" => OmniTone.Danger,
        "Warning" => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private string ChangeSeverityText(string severity) => severity switch
    {
        "Critical" => L["SshChangeCritical"],
        "Warning" => L["SshChangeWarning"],
        _ => L["SshChangeInfo"]
    };
}
