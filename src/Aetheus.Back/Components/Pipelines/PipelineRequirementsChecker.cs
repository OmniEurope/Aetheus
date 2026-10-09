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

public interface IPipelineRequirementsChecker
{
    Task<IReadOnlyList<PipelineRequirementOutcome>> CheckAsync(
        PipelineRequiresDefinition? requires,
        int? projectId,
        int? organizationId,
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
