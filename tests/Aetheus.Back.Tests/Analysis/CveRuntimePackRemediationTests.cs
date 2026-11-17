// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// R-48: a CVE of a .NET runtime pack read from an SBOM. The recommendation names the SDK and the base image the project's inventory
/// knows, never the SBOM; the refusal says its reason once; no <c>dotnet add package</c> is offered. Real EF rows (in memory), the real
/// recommendation and remediation code up to the refusal; the registry, the git write and the pipeline launch are never reached here.
/// </summary>
public sealed class CveRuntimePackRemediationTests : IDisposable
{
    private const string Pack = "Microsoft.NETCore.App.Runtime.linux-x64";
    private const string Ranges = """[{"type":"ECOSYSTEM","events":[{"introduced":"10.0.0"},{"fixed":"10.0.14"}]}]""";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, TimeProvider.System);

    private readonly ICveWatchQueryService _query = Substitute.For<ICveWatchQueryService>();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProjectInventoryItem Item(
        int id, ProjectInventoryItemKind kind, string ecosystem, string name, string version, string source) =>
        new() { Id = id, OrganizationId = 2, ProjectId = 3, Kind = kind, Ecosystem = ecosystem, PackageName = name, Version = version, SourcePath = source };

    /// <summary>One open match on <paramref name="item"/>, in an inventory that also holds <paramref name="siblings"/>.</summary>
    private async Task<long> SeedAsync(ProjectInventoryItem item, string ecosystem, string package, string version, int? repositoryId, params ProjectInventoryItem[] siblings)
    {
        var advisory = new VulnerabilityAdvisory { Id = 1, Source = "osv", ExternalId = "GHSA-test", PrimaryCveId = "CVE-2026-1" };
        _db.VulnerabilityAffectedPackages.Add(new VulnerabilityAffectedPackage
        {
            AdvisoryId = 1,
            Advisory = advisory,
            Ecosystem = ecosystem,
            PackageName = package,
            RangesJson = Ranges
        });
        var inventory = new ProjectInventory { Id = 5, OrganizationId = 2, ProjectId = 3, GitInternalRepoId = repositoryId, Items = [item, .. siblings] };
        _db.ProjectInventories.Add(inventory);
        var match = new VulnerabilityMatch
        {
            Id = 1,
            OrganizationId = 2,
            ProjectId = 3,
            AdvisoryId = 1,
            Advisory = advisory,
            ProjectInventoryItem = item,
            ProjectInventoryItemId = item.Id,
            Ecosystem = ecosystem,
            PackageName = package,
            Version = version,
            Status = VulnerabilityMatchStatus.Affected
        };
        _db.VulnerabilityMatches.Add(match);
        await _db.SaveChangesAsync(Ct);
        return match.Id;
    }

    private CveWatchRecommendationService Recommendations()
    {
        var cycles = Substitute.For<IEndOfLifeCycleSource>();
        cycles.GetCyclesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<VulnerabilityEndOfLifeCycle>>([]));
        return new CveWatchRecommendationService(new CveWatchRecommendationRepository(_db), null!, new CveWatchEndOfLife(cycles, TimeProvider.System));
    }

    private static CveWatchMatchDto Row(long id, string ecosystem, string package, string version) =>
        new() { MatchId = id, Ecosystem = ecosystem, PackageName = package, Version = version, Status = VulnerabilityMatchStatus.Affected };

    private async Task<CveRemediationOutcome> RequestAsync(long matchId)
    {
        var ledger = new CveRemediationLedger(new CveRemediationRepository(_db), Substitute.For<IAuditService>(), TimeProvider.System, NullLogger<CveRemediationLedger>.Instance);
        var service = new CveRemediationService(new CveRemediationRepository(_db), _query, null!, null!, null!, ledger, TimeProvider.System);
        return await service.RequestAsync(new CveRemediationScope(matchId, 2, 3), new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], "test")), Ct);
    }

    private void Recommend(CveRecommendationKind kind, string target) =>
        _query.GetDetailAsync(Arg.Any<long>(), null, null, Arg.Any<CancellationToken>())
            .Returns(new CveWatchMatchDto
            {
                MatchId = 1,
                Recommendation = new CveWatchRecommendationDto { Kind = kind, TargetVersion = target }
            });

    [Fact]
    public async Task ARuntimePackFromTheSbom_IsRaisedFromTheSdkAndTheBaseImageOfTheInventory_NeverFromTheSbom()
    {
        var pack = Item(1, ProjectInventoryItemKind.Runtime, "NuGet", Pack, "10.0.5", "sbom");
        var matchId = await SeedAsync(
            pack, "NuGet", Pack, "10.0.5", 7,
            Item(2, ProjectInventoryItemKind.Runtime, "runtime", "dotnet", "10.0.100", "global.json"),
            Item(3, ProjectInventoryItemKind.BaseImage, "docker", "mcr.microsoft.com/dotnet/aspnet", "10.0.5", "deploy/docker/Dockerfile.back"),
            Item(4, ProjectInventoryItemKind.BaseImage, "docker", "node", "20.1.0", "server:vps-1"),
            Item(6, ProjectInventoryItemKind.Runtime, "NuGet", "Microsoft.AspNetCore.App.Runtime.linux-x64", "10.0.5", "sbom"));

        var rows = await Recommendations().AddFromAdvisoriesAsync([Row(matchId, "NuGet", Pack, "10.0.5")], Ct);

        var recommendation = Assert.Single(rows).Recommendation!;
        Assert.Equal(CveRecommendationKind.BumpRuntime, recommendation.Kind);
        Assert.Equal("10.0.14", recommendation.TargetVersion);
        Assert.Equal(["global.json", "deploy/docker/Dockerfile.back"], recommendation.Files);
    }

    [Fact]
    public async Task ARuntimePackFromTheSbom_WithNoSdkNorImageFileInTheInventory_NamesNoFile()
    {
        var matchId = await SeedAsync(Item(1, ProjectInventoryItemKind.Runtime, "NuGet", Pack, "10.0.5", "sbom"), "NuGet", Pack, "10.0.5", 7);

        var rows = await Recommendations().AddFromAdvisoriesAsync([Row(matchId, "NuGet", Pack, "10.0.5")], Ct);

        var recommendation = Assert.Single(rows).Recommendation!;
        Assert.Equal(CveRecommendationKind.BumpRuntime, recommendation.Kind);
        Assert.Empty(recommendation.Files);
    }

    [Fact]
    public async Task APrepareOnARuntimePack_IsRefusedWithItsReasonOnly_NoRepeatedEnglishDetail_NoDotnetAddPackage()
    {
        var matchId = await SeedAsync(Item(1, ProjectInventoryItemKind.Runtime, "NuGet", Pack, "10.0.5", "sbom"), "NuGet", Pack, "10.0.5", 7);
        Recommend(CveRecommendationKind.BumpRuntime, "10.0.14");

        var outcome = await RequestAsync(matchId);

        Assert.Equal(CveRemediationAnswer.Fallback, outcome.Answer);
        var fallback = outcome.Remediation.Fallback!;
        Assert.Equal(CveRemediationFallbackReason.NoFileToChange, fallback.Reason);
        Assert.Equal(string.Empty, fallback.Detail);
        Assert.Null(fallback.Command);
        Assert.Empty(fallback.Files);
    }

    [Fact]
    public async Task ADependencyWithAFileButNoRepository_IsRefusedWithItsReasonOnly_AndKeepsItsCommand()
    {
        var matchId = await SeedAsync(
            Item(1, ProjectInventoryItemKind.Dependency, "NuGet", "Serilog", "4.0.0", "src/App/App.csproj"), "NuGet", "Serilog", "4.0.0", null);
        Recommend(CveRecommendationKind.BumpDependency, "4.0.1");

        var outcome = await RequestAsync(matchId);

        var fallback = outcome.Remediation.Fallback!;
        Assert.Equal(CveRemediationFallbackReason.NoRepository, fallback.Reason);
        Assert.Equal(string.Empty, fallback.Detail);
        Assert.Equal("dotnet add package Serilog --version 4.0.1", fallback.Command);
        Assert.Equal(["src/App/App.csproj"], fallback.Files);
    }
}
