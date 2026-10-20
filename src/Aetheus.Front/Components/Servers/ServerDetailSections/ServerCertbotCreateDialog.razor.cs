// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerCertbotCreateDialog
{
    [Parameter] public int ServerId { get; set; }

    /// <summary>Recette R3-37: the Certbot page's follower, told about the task this dialog queues.</summary>
    [Parameter] public ServerTaskFollower? Follower { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private bool _saving;
    private bool _webroot;
    private readonly CertbotForm _model = new();

    // S-UX-V8R4: mutable form model so the form can drive a LocalizedDataAnnotationsValidator -
    // the annotations mirror CertbotCreateRequest (which is init-only and cannot be two-way bound).
    private sealed class CertbotForm
    {
        [Required]
        [StringLength(1000, MinimumLength = 1)]
        public string Domains { get; set; } = string.Empty;

        [EmailAddress]
        [StringLength(200)]
        public string? Email { get; set; }

        [StringLength(500)]
        public string? WebrootPath { get; set; }
    }

    private void Cancel() => Dialog.Close(false);

    private async Task Create()
    {
        _saving = true;
        try
        {
            var domains = _model.Domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            // The backend names the task "Certbot create - {domains}".
            Follower?.Expect($"{L["NewCertificate"]} {string.Join(", ", domains)}", "Certbot create");
            var success = await Api.ServerTools.CreateCertbotCertificateAsync(ServerId, new CertbotCreateRequest
            {
                Domains = string.Join(",", domains),
                Email = _model.Email,
                Standalone = !_webroot,
                WebrootPath = _webroot ? _model.WebrootPath : null
            });
            if (success)
            {
                Toast.Success(L["TaskQueued"]);
                Dialog.Close(true);
            }
            else
            {
                Follower?.Forget();
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch
        {
            Follower?.Forget();
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _saving = false;
        }
    }
}
