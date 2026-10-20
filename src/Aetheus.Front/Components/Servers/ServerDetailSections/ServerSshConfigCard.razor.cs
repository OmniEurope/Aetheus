// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerSshConfigCard
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The stored snapshot; null until the agent reported one.</summary>
    [Parameter] public SshConfigStatusDto? Status { get; set; }

    private string SettingText(SshSettingDto setting) => setting.Value ?? L["SshUnknown"];

    private string SourceText(SshSettingDto setting)
    {
        var source = setting.Value is null
            ? L["SshUnknownSource"].Value
            : setting.Source == "default" ? L["SshDefaultSource"].Value : setting.Source ?? string.Empty;
        return setting.OverriddenInMatch ? $"{source} {L["SshAlsoInMatch"]}" : source;
    }

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
