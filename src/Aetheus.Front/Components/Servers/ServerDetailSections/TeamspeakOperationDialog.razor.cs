// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class TeamspeakOperationDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Parameter] public TeamspeakDialogMode Mode { get; set; }
    [Parameter] public TeamspeakDialogModel Model { get; set; } = new();
    [Parameter] public IReadOnlyList<TeamspeakChannelDto> Channels { get; set; } = [];
    [Parameter] public string ServerName { get; set; } = string.Empty;

    private TeamspeakDialogModel _model = new();
    private string SubmitIcon => Mode switch { TeamspeakDialogMode.Setup => "install_desktop", TeamspeakDialogMode.Message => "send", TeamspeakDialogMode.SnapshotDeploy => "cloud_upload", _ => "check" };
    private string SubmitText => Mode switch
    {
        TeamspeakDialogMode.Setup => L["Install"],
        TeamspeakDialogMode.Kick => L["Kick"],
        TeamspeakDialogMode.Move => L["Move"],
        TeamspeakDialogMode.GracefulRestart => L["GracefulRestart"],
        TeamspeakDialogMode.Poke => L["Poke"],
        TeamspeakDialogMode.Ban => L["Ban"],
        TeamspeakDialogMode.CreateChannel => L["Create"],
        TeamspeakDialogMode.SnapshotDeploy => L["SnapshotDeploy"],
        TeamspeakDialogMode.Message => L["Send"],
        _ => L["Save"]
    };
    // STD-BTN (revised 2026-09-28): Kick and Ban stay red on the button that confirms them, like the
    // grid button that opened this dialog; in every other mode the submit is the dialog's main action, blue.
    private OmniButtonVariant SubmitStyle => Mode is TeamspeakDialogMode.Kick or TeamspeakDialogMode.Ban
        ? OmniButtonVariant.Danger
        : OmniButtonVariant.Primary;

    // R-511: OmniDialog listens to the keyboard, so every key press re-renders the dialog and hands it the
    // same parameters again. Copying the model there wiped what had just been typed: it is copied once.
    protected override void OnInitialized()
    {
        _model = Model.Clone();
        _model.L = L;
    }

    protected override void OnParametersSet()
    {
        _model.Mode = Mode;
        _model.ExpectedConfirmation = ServerName;
    }

    private void Submit(TeamspeakDialogModel model) => Dialog.Close(model);
}

public enum TeamspeakDialogMode { SnapshotDeploy, Setup, Kick, Move, GracefulRestart, Poke, Ban, CreateChannel, EditChannel, EditServer, Message }

public sealed class TeamspeakDialogModel : IValidatableObject
{
    private IStringLocalizer? _localizer;

    public TeamspeakDialogMode Mode { get; set; }

    /// <summary>
    /// Recette R1-5: the texts of the validation messages, set by the dialog. OE shows an
    /// <see cref="IValidatableObject"/> message as it is, so a key returned here reached the user raw; a model
    /// validated without a localizer fails loudly instead of answering keys.
    /// </summary>
    internal IStringLocalizer L
    {
        get => _localizer ?? throw new InvalidOperationException($"{nameof(TeamspeakDialogModel)}.{nameof(L)} must be set before the model is validated.");
        set => _localizer = value;
    }
    public string ExpectedConfirmation { get; set; } = string.Empty;
    public int ClientId { get; set; }
    public int ChannelId { get; set; }
    public string ClientUniqueId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    [StringLength(4096)] public string InstallPath { get; set; } = "/opt/teamspeak3-server_linux_amd64";
    [Range(1024, 65535)] public int VoicePort { get; set; } = 9987;
    [Range(1024, 65535)] public int QueryPort { get; set; } = 10011;
    [StringLength(1024)] public string Reason { get; set; } = string.Empty;
    public int TargetChannelId { get; set; }
    [StringLength(1024)] public string Password { get; set; } = string.Empty;
    [Range(0, 600)] public int WarningSeconds { get; set; } = 60;
    [StringLength(1024)] public string Message { get; set; } = string.Empty;
    [Range(0, 315360000)] public int Duration { get; set; } = 3600;
    [StringLength(64)] public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    [Range(1, 32)] public int? MaxClients { get; set; }
    public bool IsPermanent { get; set; } = true;
    [StringLength(5000000)] public string SnapshotBlob { get; set; } = string.Empty;
    [StringLength(200)] public string Confirmation { get; set; } = string.Empty;

    public TeamspeakDialogModel Clone() => (TeamspeakDialogModel)MemberwiseClone();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Mode == TeamspeakDialogMode.Setup && string.IsNullOrWhiteSpace(InstallPath))
            yield return new ValidationResult(L["Required"], [nameof(InstallPath)]);
        if (Mode is TeamspeakDialogMode.Poke or TeamspeakDialogMode.Message && string.IsNullOrWhiteSpace(Message))
            yield return new ValidationResult(L["Required"], [nameof(Message)]);
        if (Mode is TeamspeakDialogMode.CreateChannel or TeamspeakDialogMode.EditChannel && string.IsNullOrWhiteSpace(Name))
            yield return new ValidationResult(L["Required"], [nameof(Name)]);
        if (Mode == TeamspeakDialogMode.SnapshotDeploy && string.IsNullOrWhiteSpace(SnapshotBlob))
            yield return new ValidationResult(L["Required"], [nameof(SnapshotBlob)]);
        if (Mode == TeamspeakDialogMode.SnapshotDeploy && !string.Equals(Confirmation, ExpectedConfirmation, StringComparison.Ordinal))
            yield return new ValidationResult(L["SnapshotConfirmationMismatch"], [nameof(Confirmation)]);
    }
}
