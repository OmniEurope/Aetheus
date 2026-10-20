// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Projects;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>The reclamation SQL exception permits only fixed per-table vacuum statements and a size
/// read, callable solely from the administrator endpoint as documented in AGENTS.md.</summary>
public sealed class ReclaimSpaceSqlAuditTests
{
    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine([RepositoryScan.Root, "src", "Aetheus.Back", "Components", "Projects", .. path]));

    [Fact]
    public void TheStatements_AreOneFixedVacuumPerPurgedTable_AndOneSizeRead()
    {
        var source = Source("ReclaimSpaceRepository.cs");
        var statements = Regex.Matches(source, "\"(?<sql>(?:VACUUM|SELECT) [^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"")
            .Select(match => Regex.Unescape(match.Groups["sql"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                "SELECT pg_total_relation_size(to_regclass(@relation)) AS \"Value\"",
                "VACUUM (FULL, ANALYZE) \"AnalysisComponents\"",
                "VACUUM (FULL, ANALYZE) \"AnalysisMetrics\"",
                "VACUUM (FULL, ANALYZE) \"AppLogEntries\"",
                "VACUUM (FULL, ANALYZE) \"AppMetricHourly\"",
                "VACUUM (FULL, ANALYZE) \"AppMetricSamples\""
            },
            statements.Order(StringComparer.Ordinal));
        Assert.Equal(5, ReclaimSpaceRepository.Statements.Count);
        Assert.DoesNotContain("$\"VACUUM", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"VACUUM \" +", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATableOutsideTheList_IsRefusedBeforeAnythingRuns()
    {
        var repository = new ReclaimSpaceRepository(null!);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.VacuumFullAsync("Users; DROP TABLE \"Projects\"", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OnlyTheAdministratorEndpoint_StartsAReclaim()
    {
        var callers = RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back"), "*.cs")
            .Where(file => File.ReadAllText(file).Contains("IReclaimSpaceService", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["IReclaimSpaceService.cs", "ProjectStorageController.cs", "ProjectsModuleExtensions.cs", "ReclaimSpaceService.cs"],
            callers);
        var controller = Source("ProjectStorageController.cs");
        Assert.Equal(2, Regex.Matches(controller, "\\[Authorize\\(Roles = \"Admin\"\\)\\]").Count);
    }
}
