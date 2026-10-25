// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;

namespace Aetheus.Back.Components.Pipelines;

/// <param name="Kind">The requirement class, matching the recorded preflight kinds.</param>
/// <param name="Name">What was required.</param>
/// <param name="Problem">Null when satisfied; otherwise the readable refusal.</param>
public sealed record PipelineRequirementOutcome(string Kind, string Name, string? Problem)
{
    public bool Satisfied => Problem is null;
}

/// <param name="Kind"><see cref="PipelineRequirementsChecker.LibraryKind"/> or
/// <see cref="PipelineRequirementsChecker.VaultKind"/>.</param>
/// <param name="Key">The variable or secret name whose value is empty.</param>
/// <param name="OwnerId">The id of the library or vault holding that value, when it could be told.</param>
/// <param name="OwnerName">The name of that library or vault, when it could be told.</param>
public sealed record PipelineEmptyValue(string Kind, string Key, int? OwnerId = null, string? OwnerName = null);

public interface IPipelineRequirementsChecker
{
    Task<IReadOnlyList<PipelineRequirementOutcome>> CheckAsync(
        PipelineRequiresDefinition? requires,
        int? projectId,
        int? organizationId,
        CancellationToken ct = default);

    /// <summary>
    /// The variables and secrets, among the libraries and vaults a project's pipelines read, whose value
    /// is EMPTY as a run of that project would resolve them (same precedence, same cross-project access).
    /// A library or vault that does not exist contributes nothing: <see cref="CheckAsync"/> reports it.
    /// </summary>
    Task<IReadOnlyList<PipelineEmptyValue>> FindEmptyValuesAsync(
        IReadOnlyCollection<string> libraryNames,
        IReadOnlyCollection<string> vaultNames,
        int projectId,
        CancellationToken ct = default);
}

/// <summary>
/// Checks a definition's <c>requires:</c> block against what the installation actually has.
///
/// A generic template cannot name a host, a port or a domain: it is meant for any project. What it
/// CAN state is which library, vault and environment the installation must supply, and that
/// declaration is the only thing both the launch preflight and the setup wizard need to read. Before
/// it, each of them inferred the same answer from stage selectors and from which <c>$(...)</c>
/// happened to be unresolvable, which is why a template's needs were discovered at the fourth stage.
///
/// Every name here is verified. A capability the control plane cannot check is refused rather than
/// accepted, because a declaration nobody verifies reads as a guarantee and is not one.
/// </summary>
public sealed class PipelineRequirementsChecker(
    IPipelineRepository repo,
    IVariableLibraryService libraries,
    IVaultService vaults) : IPipelineRequirementsChecker
{
    public const string LibraryKind = "library";
    public const string VaultKind = "vault";
    public const string EnvironmentKind = "environment";
    public const string CapabilityKind = "capability";

    public async Task<IReadOnlyList<PipelineRequirementOutcome>> CheckAsync(
        PipelineRequiresDefinition? requires,
        int? projectId,
        int? organizationId,
        CancellationToken ct = default)
    {
        if (requires is null || requires.IsEmpty) return [];

        var outcomes = new List<PipelineRequirementOutcome>();
        await CheckLibrariesAsync(requires, projectId, outcomes, ct).ConfigureAwait(false);
        await CheckVaultsAsync(requires, projectId, outcomes, ct).ConfigureAwait(false);
        await CheckEnvironmentsAsync(requires, projectId, outcomes, ct).ConfigureAwait(false);
        await CheckCapabilitiesAsync(requires, organizationId, outcomes, ct).ConfigureAwait(false);
        return outcomes;
    }

    public async Task<IReadOnlyList<PipelineEmptyValue>> FindEmptyValuesAsync(
        IReadOnlyCollection<string> libraryNames,
        IReadOnlyCollection<string> vaultNames,
        int projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraryNames);
        ArgumentNullException.ThrowIfNull(vaultNames);
        var empty = new List<PipelineEmptyValue>();
        if (libraryNames.Count > 0)
        {
            var (variables, _) = await libraries
                .ResolveLibrariesWithCrossAccessAndNamesAsync([.. libraryNames], projectId, ct).ConfigureAwait(false);
            var owners = await libraries
                .ResolveEntryOwnersWithCrossAccessAsync([.. libraryNames], projectId, ct).ConfigureAwait(false);
            var emptyKeys = variables.Where(pair => string.IsNullOrEmpty(pair.Value)).Select(pair => pair.Key).ToList();
            var names = await NamesOfAsync(emptyKeys.Select(key => OwnerOf(owners, key)),
                async id => (await libraries.GetLibraryDetailAsync(id, ct).ConfigureAwait(false))?.Name).ConfigureAwait(false);
            empty.AddRange(emptyKeys.Select(key => OwnedValue(LibraryKind, key, OwnerOf(owners, key), names)));
        }
        if (vaultNames.Count > 0)
        {
            var (secrets, _) = await vaults
                .ResolveVaultSecretsWithCrossAccessAndNamesAsync([.. vaultNames], projectId, ct).ConfigureAwait(false);
            var owners = await vaults
                .ResolveSecretOwnersWithCrossAccessAsync([.. vaultNames], projectId, ct).ConfigureAwait(false);
            var emptyKeys = secrets.Where(pair => string.IsNullOrEmpty(pair.Value)).Select(pair => pair.Key).ToList();
            var names = await NamesOfAsync(emptyKeys.Select(key => OwnerOf(owners, key)),
                async id => (await vaults.GetVaultDetailAsync(id, ct).ConfigureAwait(false))?.Name).ConfigureAwait(false);
            empty.AddRange(emptyKeys.Select(key => OwnedValue(VaultKind, key, OwnerOf(owners, key), names)));
        }
        return empty;
    }

    /// <summary>The name of each distinct owner, read once, so a table of empty values can say the library
    /// or vault by name (R-10).</summary>
    private static async Task<Dictionary<int, string>> NamesOfAsync(
        IEnumerable<int?> ownerIds, Func<int, Task<string?>> nameOf)
    {
        var names = new Dictionary<int, string>();
        foreach (var id in ownerIds.OfType<int>().Distinct())
            if (await nameOf(id).ConfigureAwait(false) is { Length: > 0 } name)
                names[id] = name;
        return names;
    }

    private static PipelineEmptyValue OwnedValue(string kind, string key, int? ownerId, Dictionary<int, string> names) =>
        new(kind, key, ownerId, ownerId is { } id && names.TryGetValue(id, out var name) ? name : null);

    private static int? OwnerOf(Dictionary<string, int>? owners, string key) =>
        owners is not null && owners.TryGetValue(key, out var id) ? id : null;

    private async Task CheckLibrariesAsync(
        PipelineRequiresDefinition requires, int? projectId,
        List<PipelineRequirementOutcome> outcomes, CancellationToken ct)
    {
        if (requires.Libraries.Count == 0) return;

        var (_, found) = await libraries
            .ResolveLibrariesWithNamesAsync([.. requires.Libraries], projectId, ct).ConfigureAwait(false);
        foreach (var name in requires.Libraries)
            outcomes.Add(new PipelineRequirementOutcome(
                LibraryKind, name,
                found.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : $"Variable library '{name}' is required by this pipeline and does not exist here. "
                      + "Create it with the entries the pipeline reads, then launch."));
    }

    private async Task CheckVaultsAsync(
        PipelineRequiresDefinition requires, int? projectId,
        List<PipelineRequirementOutcome> outcomes, CancellationToken ct)
    {
        if (requires.Vaults.Count == 0) return;

        var names = await vaults.GetVaultNamesAsync(projectId, ct: ct).ConfigureAwait(false);
        foreach (var name in requires.Vaults)
            outcomes.Add(new PipelineRequirementOutcome(
                VaultKind, name,
                names.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : $"Vault '{name}' is required by this pipeline and does not exist here. "
                      + "Create it and add the secrets the pipeline reads, then launch."));
    }

    private async Task CheckEnvironmentsAsync(
        PipelineRequiresDefinition requires, int? projectId, List<PipelineRequirementOutcome> outcomes, CancellationToken ct)
    {
        foreach (var name in requires.Environments)
        {
            var environment = await repo.FindEnvironmentByNameAsync(name, projectId, ct).ConfigureAwait(false);
            outcomes.Add(new PipelineRequirementOutcome(
                EnvironmentKind, name,
                environment is not null
                    ? null
                    : $"Environment '{name}' is required by this pipeline and does not exist here."));
        }
    }

    private async Task CheckCapabilitiesAsync(
        PipelineRequiresDefinition requires, int? organizationId,
        List<PipelineRequirementOutcome> outcomes, CancellationToken ct)
    {
        foreach (var capability in requires.Capabilities)
        {
            if (!PipelineCapabilities.Known.Contains(capability))
            {
                // Refused, not ignored: accepting an unknown name would let a template declare a
                // guarantee nothing behind it verifies.
                outcomes.Add(new PipelineRequirementOutcome(
                    CapabilityKind, capability,
                    $"Capability '{capability}' is not one this control plane can verify "
                    + $"(known: {string.Join(", ", PipelineCapabilities.Known)})."));
                continue;
            }

            var satisfied = await repo.HasRunnerWithDockerAsync(organizationId, ct).ConfigureAwait(false);
            outcomes.Add(new PipelineRequirementOutcome(
                CapabilityKind, capability,
                satisfied ? null : "No enrolled runner reports Docker available."));
        }
    }
}
