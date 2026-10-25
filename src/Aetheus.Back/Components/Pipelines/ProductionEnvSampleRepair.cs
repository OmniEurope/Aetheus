// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Reads, and mends, an environment sample written before the wizard held a refused Compose variable under a scoped
/// name (recette R-5): <c>ENV=#{ENV}#</c> made every step of the deployment refused at its preparation, because the
/// library entry <c>ENV</c> reaches the step as an environment variable the agent blocks. The mended line reads
/// <c>ENV=#{DEPLOY_ENV}#</c>: the variable Compose reads keeps its name, only the placeholder that names the library
/// entry changes. The deployment renders every placeholder of the file, a comment's included, so every line is read.
/// Pure text work; the configuration repository and the library are handled by the setup service.
/// </summary>
internal static partial class ProductionEnvSampleRepair
{
    [GeneratedRegex(@"#\{([A-Za-z_][A-Za-z0-9_.-]*)\}#", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Placeholder();

    /// <summary>Every placeholder of the sample that names a variable the agent refuses to hand a step, one per
    /// occurrence, with its 1-based line and the variable that line defines (empty on a line that defines none).</summary>
    public static IReadOnlyList<ProductionRefusedName> Find(string sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var found = new List<ProductionRefusedName>();
        var lines = sample.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var variable = line.Contains('=', StringComparison.Ordinal) ? line.Split('=', 2)[0].Trim() : string.Empty;
            foreach (Match match in Placeholder().Matches(line))
            {
                var name = match.Groups[1].Value;
                if (AgentEnvironmentPolicy.DangerousNames.Contains(name))
                    found.Add(new ProductionRefusedName(index + 1, variable, name, AgentEnvironmentPolicy.StepSafeName(name)));
            }
        }
        return found;
    }

    /// <summary>The sample with each refused placeholder replaced by its scoped name, every other byte unchanged (line
    /// endings and comments included). A sample with no refused placeholder comes back identical, so mending twice
    /// changes nothing.</summary>
    public static string Rewrite(string sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Placeholder().Replace(sample, match =>
            AgentEnvironmentPolicy.DangerousNames.Contains(match.Groups[1].Value)
                ? "#{" + AgentEnvironmentPolicy.StepSafeName(match.Groups[1].Value) + "}#"
                : match.Value);
    }
}

/// <summary>A placeholder of an environment sample that names a refused variable.</summary>
/// <param name="Line">The 1-based line of the sample.</param>
/// <param name="Variable">The variable the line defines, as Compose reads it.</param>
/// <param name="Name">The library entry the placeholder names, a name the agent refuses.</param>
/// <param name="ScopedName">The name the entry travels under instead.</param>
internal sealed record ProductionRefusedName(int Line, string Variable, string Name, string ScopedName);
