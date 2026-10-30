// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-59: the dialog that fills one empty variable of a library or secret of a vault over the setup wizard.
/// It writes through the same API as the library and vault pages, then closes with <c>true</c> so the wizard reads
/// its readiness again and the item counts as filled, without the user leaving the page.</summary>
public partial class PipelineEmptyValueDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    /// <summary>The library or vault holding the value.</summary>
    [Parameter, EditorRequired] public int OwnerId { get; set; }

    [Parameter] public string OwnerName { get; set; } = string.Empty;

    /// <summary>The variable or secret name.</summary>
    [Parameter, EditorRequired] public string EntryKey { get; set; } = string.Empty;

    /// <summary>True for a secret of a vault, false for an entry of a variable library.</summary>
    [Parameter] public bool IsVault { get; set; }

    private string _value = string.Empty;
    private bool _saving;

    private string Intro => string.Format(L[IsVault ? "PipelineFillVaultIntro" : "PipelineFillLibraryIntro"].Value, EntryKey, OwnerName);

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_value))
            await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_saving || string.IsNullOrWhiteSpace(_value)) return;
        _saving = true;
        try
        {
            var saved = IsVault ? await SaveSecretAsync() : await SaveEntryAsync();
            if (!saved)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            Dialog.Close(true);
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>The entry is found by its key in the library, then updated; one that is not there yet is added.
    /// The library detail carries no entries: they are read from the entries page, searched by the key.</summary>
    private async Task<bool> SaveEntryAsync()
    {
        var page = await Api.Variables.GetVariableEntriesPageAsync(OwnerId, 1, 50, search: EntryKey);
        var entry = page.Items.FirstOrDefault(item => string.Equals(item.Key, EntryKey, StringComparison.Ordinal));
        if (entry is null)
        {
            var created = await Api.Variables.CreateVariableEntryAsync(OwnerId,
                new CreateVariableEntryRequest { Key = EntryKey, Value = _value });
            if (created.IsSuccess) Toast.Success("Saved", "EntryUpdated");
            return created.IsSuccess;
        }

        var outcome = await Api.Variables.UpdateVariableEntryAsync(OwnerId, entry.Id,
            new UpdateVariableEntryRequest { Key = entry.Key, Value = _value });
        if (outcome.IsSuccess) Toast.Success("Saved", "EntryUpdated");
        return outcome.IsSuccess;
    }

    /// <summary>The secret is found by its key in the vault, then updated; one that is not there yet is added.</summary>
    private async Task<bool> SaveSecretAsync()
    {
        var detail = await Api.Variables.GetVaultDetailAsync(OwnerId);
        if (detail is null) return false;
        var secret = detail.Secrets.FirstOrDefault(item => string.Equals(item.Key, EntryKey, StringComparison.Ordinal));
        var saved = secret is null
            ? await Api.Variables.CreateVaultSecretAsync(OwnerId, new CreateVaultSecretRequest { Key = EntryKey, Value = _value })
            : await Api.Variables.UpdateVaultSecretAsync(OwnerId, secret.Id,
                new UpdateVaultSecretRequest { Key = secret.Key, Value = _value, ExpiresAt = secret.ExpiresAt });
        if (saved is not null) Toast.Success("Saved", "SecretUpdated");
        return saved is not null;
    }
}
