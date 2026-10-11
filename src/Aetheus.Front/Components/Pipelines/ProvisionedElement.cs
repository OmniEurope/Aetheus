// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

public enum ProvisionedKind
{
    Library,
    Vault
}

/// <summary>One created library or vault: what the summary lists, and whether the user checked it.</summary>
public sealed class ProvisionedElement(ProvisionedKind kind, int id, string name, int keyCount, bool keysCopied)
{
    public ProvisionedKind Kind { get; } = kind;
    public int Id { get; } = id;
    public string Name { get; } = name;
    public int KeyCount { get; } = keyCount;
    public bool KeysCopied { get; } = keysCopied;
    public bool Checked { get; set; }

    /// <summary>The element's own page.</summary>
    public string Path => Kind == ProvisionedKind.Library ? $"/variable-libraries/{Id}" : $"/vaults/{Id}";
}
