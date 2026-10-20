// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Vaults;

/// <summary>Provides labelled generation choices for adding, updating or rotating a secret. The
/// primary Generate action uses <see cref="VaultSecretGenerator.Generate(string?)"/> for the key
/// recipe.</summary>
internal static class SecretGenerationChoices
{
    internal sealed record Choice(string LabelKey, Func<string> Generate);

    internal static IReadOnlyList<Choice> All { get; } =
    [
        new("SecretGenerateCharacters32", () => VaultSecretGenerator.GenerateCharacters(32)),
        new("SecretGenerateCharacters64", () => VaultSecretGenerator.GenerateCharacters(64)),
        new("SecretGenerateHex32Bytes", () => VaultSecretGenerator.Generate(
            new VaultSecretGenerator.Profile(string.Empty, 32, VaultSecretGenerator.Encoding.Hex, "32 random bytes, hexadecimal")))
    ];
}
