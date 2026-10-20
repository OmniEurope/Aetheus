// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Mail;

/// <summary>The settings an external mail client needs, read from the backend.</summary>
public partial class MailClientSettings
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>The address of one mailbox, written as the user name; empty for the general help.</summary>
    [Parameter] public string? Email { get; set; }

    private MailClientSettingsDto? _settings;
    private bool _failed;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var result = await Api.Mailbox.GetClientSettingsAsync(ServerId);
            _settings = result.Value;
            _failed = !result.Ok;
        }
        catch (HttpRequestException)
        {
            // An expired session is redirected to the login page; the card says the read failed meanwhile.
            _failed = true;
        }
    }
}
