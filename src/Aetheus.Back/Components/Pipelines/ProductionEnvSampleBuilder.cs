// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Writes the versioned environment sample of the production deployment: one <c>NAME=#{KEY}#</c> line per
/// variable the vault or the library <c>prod</c> supplies, and no value (ADR-057). The deployment renders it
/// at each run (<c>env_template</c>) into the file Compose reads with <c>--env-file</c>. What the default
/// deployment provides itself, what is a port, and what the Compose file defaults are not listed: they reach
/// Compose through the process environment or its own defaults.
/// </summary>
internal static class ProductionEnvSampleBuilder
{
    /// <summary>Where the sample lives, in the configuration repository (and in a project's own source).</summary>
    public const string Path = ".pipeline/configs/env/production.env";

    public static string Build(IEnumerable<ProductionVariablePlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var sample = new StringBuilder();
        sample.Append("# Environment of the production deployment, rendered by Aetheus at every deployment into the file\n");
        sample.Append("# Docker Compose reads with --env-file. Each value is a placeholder named after an entry of the vault or of\n");
        sample.Append("# the variable library \"prod\" of the project, which Aetheus replaces: no value belongs in this file. The\n");
        sample.Append("# rendered file is removed when the deployment ends.\n");
        foreach (var plan in plans.Where(Supplied).OrderBy(plan => plan.Name, StringComparer.Ordinal))
            sample.Append(plan.Name).Append("=#{").Append(plan.Key).Append("}#\n");
        return sample.ToString();
    }

    /// <summary>The kinds whose value the vault or the library supplies at deployment.</summary>
    internal static bool Supplied(ProductionVariablePlan plan) =>
        plan.Kind is ProductionVariableKind.GeneratedSecret or ProductionVariableKind.DefaultValue
            or ProductionVariableKind.NeedsValue;
}
