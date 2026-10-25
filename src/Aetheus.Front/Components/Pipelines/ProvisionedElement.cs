// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

public enum ProvisionedKind
{
    Library,
    Vault
}

/// <summary>One created library or vault: what the summary lists as created.</summary>
public sealed class ProvisionedElement(ProvisionedKind kind, int id, string name, int keyCount, bool keysCopied, int filledCount = 0)
{
    public ProvisionedKind Kind { get; } = kind;
    public int Id { get; } = id;
    public string Name { get; } = name;
    public int KeyCount { get; } = keyCount;
    public bool KeysCopied { get; } = keysCopied;

    /// <summary>How many keys received a value at creation (generated secrets, ports, derived names).</summary>
    public int FilledCount { get; } = filledCount;
}
