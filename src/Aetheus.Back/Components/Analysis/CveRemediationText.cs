// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>The commit message and the pull request of a fix. Git history stays in English, whatever the language of the requester.</summary>
internal static class CveRemediationText
{
    public static string Title(string package, string target, string advisory) => $"fix(deps): bump {package} to {target} ({advisory})";

    public static string Description(CveRemediationFacts facts, VulnerabilityRemediation remediation) =>
        $"{facts.Advisory}: {facts.PackageName} moves from {remediation.FromVersion} to {remediation.ResolvedVersion ?? remediation.TargetVersion}, "
        + $"requested by {remediation.RequestedByUsername} from the vulnerability watch. Review and merge by hand: the match closes on the next inventory.";
}
