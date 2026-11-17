// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// R-19, R-20: the one rule that splits a server's host vulnerabilities into "fixable" and "without a published fix", used by the
/// grade, the two finding tabs and the two overview tables, so every level reads the same split. A vulnerability is fixable
/// when its advisory names a patched version (the back counts the same: <c>FixedVersion == null</c> is "without a fix").
/// </summary>
public static class ServerHostFixability
{
    /// <summary>True when a fix is published for the finding.</summary>
    public static bool IsFixable(ServerHostFindingDto finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.FixedVersion is not null;
    }

    /// <summary>The open vulnerabilities that have a published fix, by severity: every open one minus those with no fix.</summary>
    public static AnalysisGradeFindingCountsDto Fixable(AnalysisGradeFindingCountsDto open, AnalysisGradeFindingCountsDto withoutFix)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(withoutFix);
        return new()
        {
            Critical = Math.Max(0, open.Critical - withoutFix.Critical),
            High = Math.Max(0, open.High - withoutFix.High),
            Medium = Math.Max(0, open.Medium - withoutFix.Medium),
            Low = Math.Max(0, open.Low - withoutFix.Low),
            Info = Math.Max(0, open.Info - withoutFix.Info)
        };
    }

    /// <summary>The server's open vulnerabilities that have a published fix, by severity.</summary>
    public static AnalysisGradeFindingCountsDto Fixable(ServerQualityDto server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Fixable(server.Counts, server.CountsWithoutFix);
    }
}
