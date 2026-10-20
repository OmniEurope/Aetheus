// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerSetupDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Parameter] public ServerSetupKind Kind { get; set; }
    [Parameter, EditorRequired] public ServerSetupDialogModel Model { get; set; } = new();

    private ServerSetupDialogModel _model = new();
    // Copy the model once so repeated keyboard-driven dialog updates preserve typed input.
    protected override void OnInitialized() => _model = new ServerSetupDialogModel
    {
        Kind = Kind,
        Mode = Model.Mode,
        TcpPorts = Model.TcpPorts,
        UdpPorts = Model.UdpPorts,
        MailOnWarning = Model.MailOnWarning
    };
    private void Submit(ServerSetupDialogModel model) => Dialog.Close(model);
}

public enum ServerSetupKind { Portsentry, Rkhunter }

public sealed class ServerSetupDialogModel
{
    [Required, StringLength(32)] public string Mode { get; set; } = "atcp";
    /// <summary>Which setup the dialog collects; the port lists are only asked (and required) for Portsentry.</summary>
    public ServerSetupKind Kind { get; set; }
    // FRONT-019: the RKHunter dialog hides the port fields, so a plain [Required] on them made its form
    // impossible to submit. They are required for Portsentry only.
    [RequiredForPortsentry(ErrorMessage = "Required"), StringLength(1024)] public string TcpPorts { get; set; } = string.Empty;
    [RequiredForPortsentry(ErrorMessage = "Required"), StringLength(1024)] public string UdpPorts { get; set; } = string.Empty;
    [OptionalEmailAddress(ErrorMessage = "MailEmailInvalid"), StringLength(320)] public string MailOnWarning { get; set; } = string.Empty;
}
