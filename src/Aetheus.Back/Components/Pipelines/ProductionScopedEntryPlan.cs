// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What mending the environment sample does to the library entry of one refused name.</summary>
internal enum ScopedEntryAction
{
    /// <summary>Only the scoped entry exists: nothing to do.</summary>
    Keep,

    /// <summary>Neither exists: the scoped entry is created (the convention's value when there is one, else empty).</summary>
    Create,

    /// <summary>Only the refused entry exists: it becomes the scoped entry, value included.</summary>
    Rename,

    /// <summary>Both exist and the scoped one is empty: it takes the refused entry's value, which is then removed.</summary>
    Merge,

    /// <summary>Both exist and the refused one holds nothing the scoped one does not: it is removed.</summary>
    DropRefused
}

/// <param name="Name">The refused name the sample's placeholder carries.</param>
/// <param name="ScopedName">The name the entry travels under instead.</param>
/// <param name="Refused">The library entry that carries the refused name, when there is one.</param>
/// <param name="Scoped">The library entry that already carries the scoped name, when there is one.</param>
/// <param name="Value">The value a created entry starts with.</param>
internal sealed record ScopedEntryPlan(
    string Name, string ScopedName, ScopedEntryAction Action, VariableEntryDto? Refused, VariableEntryDto? Scoped, string Value);

/// <summary>
/// Decides, for a refused name of the environment sample, what the library <c>prod</c> does so that no value is lost:
/// the refused entry (<c>ENV</c>) must leave the library, since every library entry reaches the steps as an environment
/// variable and the agent refuses that name, but its value moves to the scoped entry (<c>DEPLOY_ENV</c>) first.
/// </summary>
internal static class ProductionScopedEntries
{
    public static ScopedEntryPlan Plan(string name, IReadOnlyDictionary<string, VariableEntryDto> entries, string slug)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var scopedName = AgentEnvironmentPolicy.StepSafeName(name);
        entries.TryGetValue(name, out var refused);
        entries.TryGetValue(scopedName, out var scoped);

        if (refused is null && scoped is null)
            return new(name, scopedName, ScopedEntryAction.Create, null, null,
                ProductionVariableClassifier.ConventionFor(name, slug) ?? string.Empty);
        if (refused is null) return new(name, scopedName, ScopedEntryAction.Keep, null, scoped, string.Empty);
        if (scoped is null) return new(name, scopedName, ScopedEntryAction.Rename, refused, null, string.Empty);

        if (scoped.Value.Length == 0 && refused.Value.Length > 0)
            return new(name, scopedName, ScopedEntryAction.Merge, refused, scoped, string.Empty);
        if (refused.Value.Length == 0 || string.Equals(refused.Value, scoped.Value, StringComparison.Ordinal))
            return new(name, scopedName, ScopedEntryAction.DropRefused, refused, scoped, string.Empty);

        // Two different values: removing either would lose one, and which one is right is the project's to say.
        throw new ConflictException(
            $"The library holds both '{name}' and '{scopedName}' with different values: keep the one the deployment "
            + $"must use in '{scopedName}' and remove '{name}' from the library, then mend the sample again.");
    }
}
