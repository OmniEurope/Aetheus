// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;

namespace Aetheus.Front.Components.MailTemplates;

/// <summary>
/// The templates of the platform mails (recette R1-75): one kind in one language at a time, edited with
/// <see cref="OmniMailTemplateEditor"/>, previewed with the server's sample values in the administrator's
/// own colours (the colours a recipient's mail takes from their appearance, recette R1-74).
/// </summary>
public partial class MailTemplates
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private OmniAppearanceState Appearance { get; set; } = default!;

    private MailTemplateKind _kind = MailTemplateKind.NotificationChannelTest;
    private string _language = MailTemplateLanguages.English;
    private MailTemplateDto? _template;
    private string? _subject;
    private string? _body;
    private IReadOnlyList<OmniMailVariable> _variables = [];
    private List<OmniOption<MailTemplateKind>> _kinds = [];
    private List<OmniOption<string>> _languages = [];
    private OmniMailTheme _theme = OmniMailTheme.Default;
    private bool _loading;
    private bool _saving;

    private bool IsDirty => _template is not null
        && (!string.Equals(_subject ?? string.Empty, _template.Subject, StringComparison.Ordinal)
            || !string.Equals(_body ?? string.Empty, _template.Body, StringComparison.Ordinal));

    private bool CanSave => IsDirty && !_saving && !_loading && !string.IsNullOrWhiteSpace(_subject) && !string.IsNullOrWhiteSpace(_body);

    private string? StateText => _template is { IsCustomized: true, UpdatedAt: { } updatedAt }
        ? string.Format(L["MailTemplateChangedBy"].Value, _template.UpdatedBy ?? "-", updatedAt.ToString("g"))
        : null;

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin)
        {
            Nav.NavigateTo("/");
            return;
        }

        Breadcrumb.Set(
            new BreadcrumbItem(L["Administration"], "/admin"),
            new BreadcrumbItem(L["MailTemplates"]));
        _kinds = [.. Enum.GetValues<MailTemplateKind>().Select(kind => new OmniOption<MailTemplateKind>(kind, L.Localize(kind)))];
        _languages = [.. MailTemplateLanguages.All.Select(language => new OmniOption<string>(language, L["MailTemplateLanguage_" + language]))];
        _language = MailTemplateLanguages.Normalize(System.Globalization.CultureInfo.CurrentUICulture.Name);
        _theme = ThemeOf(Appearance);
        await LoadAsync();
    }

    /// <summary>The colours of the administrator's appearance, as the server resolves a recipient's.</summary>
    internal static OmniMailTheme ThemeOf(OmniAppearanceState appearance)
    {
        var preset = appearance.Preset ?? OmniThemePresets.All[0];
        return OmniMailTheme.From(appearance.Palette is { } palette ? preset.With(palette) : preset);
    }

    private async Task SelectAsync(MailTemplateKind kind, string language)
    {
        if (IsDirty)
        {
            var leave = await Dialog.Confirm(L["MailTemplateDiscardConfirm"].Value, L["MailTemplateDiscard"].Value,
                new OmniConfirmOptions { Destructive = true, OkButtonText = L["MailTemplateDiscard"].Value, CancelButtonText = L["GoBack"].Value });
            if (leave != true) return;
        }

        _kind = kind;
        _language = language;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            Show(await Api.MailTemplates.GetAsync(_kind, _language));
        }
        catch (HttpRequestException)
        {
            _template = null;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(MailTemplateDto? template)
    {
        _template = template;
        _subject = template?.Subject;
        _body = template?.Body;
        _variables = template is null
            ? []
            : [.. template.Variables.Select(variable => new OmniMailVariable(variable.Name, L["MailVariable_" + variable.Name])
            {
                SampleValue = variable.SampleValue,
                Kind = KindOf(variable.Kind),
                Description = L["MailVariable_" + variable.Name + "_Help"]
            })];
    }

    /// <summary>A link is offered as the target of an action button, a block as an HTML fragment in a paragraph of its own.</summary>
    internal static OmniMailVariableKind KindOf(MailTemplateVariableKind kind) => kind switch
    {
        MailTemplateVariableKind.Link => OmniMailVariableKind.Link,
        MailTemplateVariableKind.Block => OmniMailVariableKind.Block,
        _ => OmniMailVariableKind.Text
    };

    private async Task SaveAsync()
    {
        if (!CanSave) return;
        _saving = true;
        try
        {
            var outcome = await Api.MailTemplates.SaveAsync(_kind, _language, Draft());
            if (outcome.Value is { } saved)
            {
                Show(saved);
                Toast.Success("Saved");
            }
            else
            {
                Toast.ErrorRaw(L["SaveFailed"].Value, outcome.ErrorMessage ?? string.Empty);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task ResetAsync()
    {
        if (_template is not { IsCustomized: true }) return;
        var confirmed = await Dialog.Confirm(L["MailTemplateRestoreDefaultConfirm"].Value, L["MailTemplateRestoreDefault"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["MailTemplateRestore"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        var outcome = await Api.MailTemplates.ResetAsync(_kind, _language);
        if (outcome.Value is { } restored)
        {
            Show(restored);
            Toast.Success("MailTemplateRestored");
        }
        else
        {
            Toast.ErrorRaw(L["SaveFailed"].Value, outcome.ErrorMessage ?? string.Empty);
        }
    }

    /// <summary>The editor rendered the draft for its preview; the server renders and sends it, so the mail received is the one recipients get.</summary>
    internal async Task SendTestAsync(OmniMailRenderResult _)
    {
        var outcome = await Api.MailTemplates.SendTestAsync(_kind, _language, Draft());
        if (outcome.Value is not { } result)
        {
            Toast.ErrorRaw(L["MailTemplateTestFailed"].Value, outcome.ErrorMessage ?? string.Empty);
            return;
        }

        if (result.Status == NotificationTestStatus.Sent)
            Toast.Notify(OmniSeverity.Success, "MailTemplateTestSent", string.Format(L["MailTemplateTestSentTo"].Value, result.To));
        else
            Toast.Notify(result.Status == NotificationTestStatus.NotConfigured ? OmniSeverity.Warning : OmniSeverity.Danger,
                "MailTemplateTestFailed", result.Message ?? string.Empty);
    }

    private void OnSendTestError(Exception exception) =>
        Toast.ErrorRaw(L["MailTemplateTestFailed"].Value, exception.Message);

    private SaveMailTemplateRequest Draft() => new() { Subject = _subject ?? string.Empty, Body = _body ?? string.Empty };
}
