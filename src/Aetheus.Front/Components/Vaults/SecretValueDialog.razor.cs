// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Components.Vaults;

public partial class SecretValueDialog
{
    [Parameter] public string SecretKey { get; set; } = string.Empty;

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private string _value = string.Empty;

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_value))
            Dialog.Close(_value);
        else if (e.Key == "Escape")
            Dialog.Close();
    }

    private void Save() => Dialog.Close(_value);

    /// <summary>The recipe of the secret's key, as when adding a secret.</summary>
    private void GenerateForKey() => _value = VaultSecretGenerator.Generate(SecretKey);

    private void GenerateWith(SecretGenerationChoices.Choice choice) => _value = choice.Generate();

    /// <summary>Names the recipe a click on Generate applies to this key.</summary>
    private string GenerateHint => VaultSecretGenerator.ProfileFor(SecretKey) is { Key.Length: > 0 } profile
        ? string.Format(L["GenerateSecretValueForKey"], profile.Key)
        : L["GenerateSecretValue"];
}
