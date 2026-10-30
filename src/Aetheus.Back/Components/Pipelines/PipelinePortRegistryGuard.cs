// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.PortRegistry;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Reads the ports a run is about to bind, checks them against the port registry, and records them
/// once the launch is accepted.
///
/// The failure this exists for: the nightly pipeline died six times in a row on
/// <c>Bind for 0.0.0.0:10031 failed: port is already allocated</c>. The port belonged to another
/// project's production front-end on the same host, both sides were healthy, and neither could see
/// the other. The message named no holder and arrived at <c>docker up</c>, an hour into the run.
/// Refusing at launch with the holder's name is the whole point.
/// </summary>
public interface IPipelinePortRegistryGuard
{
    /// <summary>
    /// What the registry says about the ports this run wants, split by what the operator can act on.
    /// Empty for a redeployment, which re-takes its own ports.
    /// </summary>
    Task<PortConflictReport> FindPortConflictsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        CancellationToken ct);

    /// <summary>Writes what this run is taking into the registry, so the next project can see it.</summary>
    Task DeclareReservationsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        string ownerLabel,
        CancellationToken ct);
}

/// <summary>
/// What the registry has to say about a launch, split by what it does to it: a blocking problem
/// refuses the run, a warning is recorded on it and it proceeds. Keeping them apart is the point -
/// merging them would either block on a stale observation or hide a real declared conflict.
/// </summary>
public sealed record PortConflictReport(
    IReadOnlyList<PipelineProblem> Blocking,
    IReadOnlyList<PipelineProblem> Warnings)
{
    public static PortConflictReport Empty { get; } = new([], []);
}

public sealed class PipelinePortRegistryGuard(
    IPipelineDispatchServerResolver dispatchServers,
    IPortRegistryService portRegistry,
    ILogger<PipelinePortRegistryGuard> logger) : IPipelinePortRegistryGuard
{
    /// <summary>
    /// Prefix of the run variables read as port declarations. Deliberately narrow: it is the
    /// convention the deployment pipelines already use (<c>PORT_FRONT</c>, <c>PORT_BACK</c>), and a
    /// looser rule such as "any variable whose name contains PORT" would sweep in system values like
    /// <c>BUILD_RUN_PORT</c> and refuse launches over ports nothing binds.
    /// </summary>
    internal const string PortVariablePrefix = "PORT_";

    public async Task<PortConflictReport> FindPortConflictsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var ownerKey = OwnerKeyFor(projectId, resolvedVariables);
        if (ownerKey is null) return PortConflictReport.Empty;

        var policy = ParseObservedPortPolicy(definition.ObservedPortPolicy);
        var claims = await ResolveClaimsAsync(
            definition, resolvedVariables, organizationId, projectId, ct).ConfigureAwait(false);

        var blocking = new List<PipelineProblem>();
        var warnings = new List<PipelineProblem>();
        foreach (var claim in claims)
        {
            var conflicts = await portRegistry
                .FindConflictsAsync(claim.ServerId, claim.Ports, ownerKey, ct).ConfigureAwait(false);
            foreach (var conflict in conflicts)
            {
                // A DECLARED or MANUAL holder always refuses: somebody stated they own that port, which
                // is the fact the registry exists to enforce. Only an OBSERVATION - a listener nobody
                // claimed - is governed by the pipeline's policy, because it may be stale and refusing
                // on it alone would block a deployment over a scan nobody confirmed.
                if (conflict.Source != PortReservationSource.Observed)
                {
                    blocking.Add(DescribeDeclared(conflict, claim.ServerName));
                    continue;
                }

                if (policy == ObservedPortPolicy.Ignore) continue;
                var message = DescribeObserved(conflict, claim.ServerName);
                if (policy == ObservedPortPolicy.Error) blocking.Add(message);
                else warnings.Add(message);
            }
        }
        return new PortConflictReport(blocking, warnings);
    }

    private static PipelineProblem DescribeDeclared(PortConflictDto conflict, string serverName) =>
        PipelineProblem.Of(
            PipelineProblemCodes.PortRegisteredToAnother,
            $"Port {conflict.Port} on server '{serverName}' is already registered to "
            + $"'{conflict.OwnerLabel}' (declared {conflict.DeclaredAt:yyyy-MM-dd}). "
            + "Choose another port or release that reservation before deploying.",
            ("port", conflict.Port), ("server", serverName), ("owner", conflict.OwnerLabel),
            ("declaredAt", conflict.DeclaredAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    private static PipelineProblem DescribeObserved(PortConflictDto conflict, string serverName) =>
        PipelineProblem.Of(
            PipelineProblemCodes.PortSeenListening,
            $"Port {conflict.Port} on server '{serverName}' was seen listening, held by "
            + $"'{conflict.OwnerLabel}', and no project declared it. The deployment will fail to bind it "
            + "unless that listener is stopped. Set 'observed_port_policy: error' to refuse the launch, "
            + "or 'ignore' to stop reporting it.",
            ("port", conflict.Port), ("server", serverName), ("owner", conflict.OwnerLabel));

    /// <summary>
    /// Reads the YAML <c>observed_port_policy:</c>. An unrecognised value falls back to the default
    /// rather than refusing the run: a typo in an advisory setting must not block a deployment, and the
    /// value is echoed in the log so it can be corrected.
    /// </summary>
    internal ObservedPortPolicy ParseObservedPortPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ObservedPortPolicy.Warning;

        var trimmed = value.Trim();
        if (Enum.TryParse<ObservedPortPolicy>(trimmed, ignoreCase: true, out var parsed)) return parsed;

        logger.LogWarning(
            "Unknown observed_port_policy '{Value}'; using '{Default}'. Expected error, warning or ignore.",
            trimmed, ObservedPortPolicy.Warning);
        return ObservedPortPolicy.Warning;
    }

    public async Task DeclareReservationsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        string ownerLabel,
        CancellationToken ct)
    {
        var ownerKey = OwnerKeyFor(projectId, resolvedVariables);
        if (ownerKey is null) return;

        var claims = await ResolveClaimsAsync(
            definition, resolvedVariables, organizationId, projectId, ct).ConfigureAwait(false);
        foreach (var claim in claims)
        {
            await portRegistry
                .DeclareAsync(claim.ServerId, claim.Ports, ownerKey, ownerLabel, projectId, ct)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Registered ports {Ports} on server {ServerId} for '{Owner}'.",
                string.Join(", ", claim.Ports), claim.ServerId, ownerLabel);
        }
    }

    /// <summary>
    /// The stable identity the conflict rule compares. The project is the unit that owns a port on a
    /// shared host, so two pipelines of one project redeploy the same app rather than fight over it;
    /// a pipeline with no project falls back to its own id. A run whose variables carry neither is not
    /// checked at all - inventing an identity there would refuse launches on a made-up owner.
    /// </summary>
    internal static string? OwnerKeyFor(int? projectId, IReadOnlyDictionary<string, string> variables)
    {
        if (projectId is { } id) return "project:" + id.ToString(CultureInfo.InvariantCulture);
        return variables is not null
               && variables.TryGetValue("BUILD_PIPELINEID", out var pipelineId)
               && !string.IsNullOrWhiteSpace(pipelineId)
            ? "pipeline:" + pipelineId.Trim()
            : null;
    }

    /// <summary>Per deployment stage: which server it lands on and which ports it will bind there.</summary>
    private async Task<List<PortClaim>> ResolveClaimsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resolvedVariables);

        var substitutions = resolvedVariables as Dictionary<string, string>
            ?? new Dictionary<string, string>(resolvedVariables, StringComparer.OrdinalIgnoreCase);
        var variablePorts = VariablePorts(substitutions);

        var byServer = new Dictionary<int, PortClaim>();
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
        {
            var ports = StagePorts(stage, substitutions);
            if (!IsDeploymentStage(stage)) continue;
            ports.UnionWith(variablePorts);
            if (ports.Count == 0) continue;

            var server = await dispatchServers.ResolveServerForTargetAsync(
                stage, resolvedVariables, organizationId, deploymentStage: true, projectId, ct).ConfigureAwait(false);
            // An unresolved server is the runner-configuration preflight's problem, not this one;
            // refusing here too would report the same fault twice under a misleading heading.
            if (server is null) continue;

            if (byServer.TryGetValue(server.Id, out var existing))
                existing.Ports.UnionWith(ports);
            else
                byServer[server.Id] = new PortClaim(server.Id, server.Name, ports);
        }

        return [.. byServer.Values];
    }

    private static bool IsDeploymentStage(PipelineStageDefinition stage) =>
        stage.Steps.Exists(step =>
            string.Equals(step.Type, "deploy", StringComparison.OrdinalIgnoreCase)
            || BlueGreenStepBinding.OperationFor(step.Type ?? string.Empty) != OperationKind.None);

    /// <summary>The four blue-green ports a stage declares in its own <c>ports:</c> lists.</summary>
    private static HashSet<int> StagePorts(
        PipelineStageDefinition stage, Dictionary<string, string> substitutions)
    {
        var ports = new HashSet<int>();
        foreach (var step in stage.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Ports)) continue;
            if (BlueGreenStepBinding.OperationFor(step.Type ?? string.Empty) == OperationKind.None) continue;
            var resolved = PipelineRunHelpers.SubstituteVariables(step.Ports, substitutions);
            foreach (var candidate in resolved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (TryPort(candidate, out var port)) ports.Add(port);
        }
        return ports;
    }

    private static HashSet<int> VariablePorts(Dictionary<string, string> substitutions)
    {
        var ports = new HashSet<int>();
        foreach (var (name, value) in substitutions)
            if (name.StartsWith(PortVariablePrefix, StringComparison.OrdinalIgnoreCase)
                && TryPort(value, out var port))
            {
                ports.Add(port);
            }
        return ports;
    }

    private static bool TryPort(string value, out int port) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is > 0 and <= 65535;

    private sealed record PortClaim(int ServerId, string ServerName, HashSet<int> Ports);
}
