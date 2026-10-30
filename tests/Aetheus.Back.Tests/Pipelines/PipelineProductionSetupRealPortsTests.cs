// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.VariableLibraries;
using Aetheus.Shared.Components.Vaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-96 lines 5 and 7, against the real port allocation (registry, repository and service over an in-memory
/// database) instead of a double: the production setup of a project whose only server is attached to its production environment,
/// never to the project itself (the W6 shape). The libraries, the vault, Git and the configuration repository stay doubles; the
/// libraries write their entries into the same database so the allocation sees what it must fill.</summary>
public sealed class PipelineProductionSetupRealPortsTests : IDisposable
{
    private const int ProjectId = 7;
    private const int LibraryId = 101;
    private const int VaultId = 201;
    private const int ServerId = 9;
    private const string ServerName = "vps-w6";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly PipelineProductionSetupService _sut;

    public PipelineProductionSetupRealPortsTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, _clock);
        _db.Organizations.Add(new Organization { Id = 1, Name = "Org", Slug = "org" });
        _db.Servers.Add(new Server { Id = ServerId, Name = ServerName, OrganizationId = 1 });
        _db.Projects.Add(new Project { Id = ProjectId, Name = "Wordmorrow", OrganizationId = 1 });
        // The server is attached to the production environment only, never to the project.
        _db.Environments.Add(new Data.Entities.Environment { Id = 62, Name = "prod", ProjectId = ProjectId });
        _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 62, ServerId = ServerId });
        _db.VariableLibraries.Add(new VariableLibrary { Id = LibraryId, Name = "wordmorrow.prod", ProjectId = ProjectId });
        _db.SaveChanges();

        var pipelines = Substitute.For<IPipelineRepository>();
        pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        pipelines.FindEnvironmentByNameAsync("prod", ProjectId, Arg.Any<CancellationToken>()).Returns(new Data.Entities.Environment
        {
            Name = "prod",
            ProjectId = ProjectId,
            Servers = [new EnvironmentServer { ServerId = ServerId, Server = new Server { Id = ServerId, Name = ServerName } }]
        });

        var libraries = Libraries();
        var vaults = Substitute.For<IVaultService>();
        vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto> { Items = [new VaultDto { Id = VaultId, Name = "wordmorrow.prod", ProjectId = ProjectId }] });
        vaults.ExportSecretKeysAsync(VaultId, Arg.Any<CancellationToken>()).Returns([]);

        var configuration = Substitute.For<IProjectConfigRepositoryService>();
        configuration.EnsureAsync(ProjectId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectConfigRepositoryDto { Id = 9, Name = "Wordmorrow configuration", Slug = "wordmorrow-config", Created = true });

        // The real allocation: registry, repository and service, over the same database.
        var ports = new PortAllocationService(
            new PortAllocationRepository(_db),
            new PortRegistryService(new PortRegistryRepository(_db), _clock, NullLogger<PortRegistryService>.Instance),
            libraries,
            Substitute.For<IDbTransactionScope>(),
            NullLogger<PortAllocationService>.Instance);

        _sut = new PipelineProductionSetupService(
            pipelines, Repository(), libraries, vaults, ports, configuration, Substitute.For<IAuditService>(), Substitute.For<IPipelineSetupServerRepository>(),
            NullLogger<PipelineProductionSetupService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private IVariableLibraryService Libraries()
    {
        var libraries = Substitute.For<IVariableLibraryService>();
        libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = LibraryId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(_ =>
            _db.VariableLibraryEntries.AsNoTracking().Where(entry => entry.VariableLibraryId == LibraryId).ToList()
                .Select(entry => new VariableEntryDto { Id = entry.Id, Key = entry.Key, Value = entry.Value }).ToList());
        libraries.ImportEntriesAsync(LibraryId, Arg.Any<List<CreateVariableEntryRequest>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            foreach (var request in call.Arg<List<CreateVariableEntryRequest>>())
                _db.VariableLibraryEntries.Add(new VariableLibraryEntry { VariableLibraryId = LibraryId, Key = request.Key, Value = request.Value });
            return _db.SaveChanges();
        });
        libraries.CreateEntryAsync(LibraryId, Arg.Any<CreateVariableEntryRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<CreateVariableEntryRequest>();
            var entry = new VariableLibraryEntry { VariableLibraryId = LibraryId, Key = request.Key, Value = request.Value };
            _db.VariableLibraryEntries.Add(entry);
            _db.SaveChanges();
            return new VariableEntryDto { Id = entry.Id, Key = entry.Key, Value = entry.Value };
        });
        libraries.UpdateEntryAsync(LibraryId, Arg.Any<int>(), Arg.Any<UpdateVariableEntryRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var entry = _db.VariableLibraryEntries.Single(item => item.Id == call.ArgAt<int>(1));
            entry.Key = call.Arg<UpdateVariableEntryRequest>().Key;
            entry.Value = call.Arg<UpdateVariableEntryRequest>().Value;
            _db.SaveChanges();
            return new VariableEntryDto { Id = entry.Id, Key = entry.Key, Value = entry.Value };
        });
        return libraries;
    }

    private static IGitLightService Repository()
    {
        const string path = PipelineProductionSetupServiceTests.ComposePath;
        var git = Substitute.For<IGitLightService>();
        git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = 5, ProjectId = ProjectId, DefaultBranch = "main" }]);
        git.GetTreeAsync(5, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<string?>(2) switch
        {
            null or "" => [new GitLightTreeEntryDto { Name = "deploy", Path = "deploy", Type = GitTreeEntryType.Tree }],
            "deploy" => [new GitLightTreeEntryDto { Name = "compose", Path = "deploy/compose", Type = GitTreeEntryType.Tree }],
            "deploy/compose" => [new GitLightTreeEntryDto { Name = "remote.compose.yml", Path = path, Type = GitTreeEntryType.Blob }],
            _ => new List<GitLightTreeEntryDto>()
        });
        git.GetBlobAsync(5, "main", path, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = path, Content = PipelineProductionSetupServiceTests.W6Compose });
        return git;
    }

    private static PipelineProductionApplyRequest Request() => new()
    {
        ProjectId = ProjectId,
        ComposeFile = PipelineProductionSetupServiceTests.ComposePath,
        Server = ServerName,
        Values = new() { ["DOMAIN"] = "w6.test" }
    };

    private Dictionary<string, string> LibraryValues() =>
        _db.VariableLibraryEntries.AsNoTracking().Where(entry => entry.VariableLibraryId == LibraryId)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static int PortOf(Dictionary<string, string> values, string key) =>
        int.Parse(values[key], NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>Line 7: the server of the production environment is the project's for the ports, offered and chosen by default.
    /// Line 5: PORT_FRONT and PORT_BACK come from the registry of that server, reserved under the project, not typed.</summary>
    [Fact]
    public async Task ABlankProject_WhoseOnlyServerIsOnItsProductionEnvironment_GetsDistinctPortsFromTheRealAllocation()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);
        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.Equal([ServerName], analysis.Servers.Select(server => server.Name));
        Assert.Equal(ServerName, analysis.DefaultServer);
        Assert.Equal(ProductionEntryOutcome.Allocated, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        Assert.Equal(ProductionEntryOutcome.Allocated, result.Entries.Single(entry => entry.Name == "PORT_BACK").Outcome);
        Assert.False(result.NeedsValues);
        var values = LibraryValues();
        Assert.NotEqual(PortOf(values, "PORT_FRONT"), PortOf(values, "PORT_BACK"));
        Assert.All(new[] { "PORT_FRONT", "PORT_BACK" }, key => Assert.InRange(PortOf(values, key), 1024, 65535));
        Assert.Equal(2, _db.ServerPortReservations.Count(reservation => reservation.ServerId == ServerId));
    }

    /// <summary>Line 5 (R-85): a first run with no server left the two entries empty; the next run, with the server, fills them.</summary>
    [Fact]
    public async Task PortsLeftEmptyByAFirstRunWithoutAServer_AreFilledByTheNextRunWithTheRealAllocation()
    {
        _db.VariableLibraryEntries.Add(new VariableLibraryEntry { VariableLibraryId = LibraryId, Key = "PORT_FRONT", Value = string.Empty });
        _db.VariableLibraryEntries.Add(new VariableLibraryEntry { VariableLibraryId = LibraryId, Key = "PORT_BACK", Value = string.Empty });
        _db.SaveChanges();

        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.False(result.NeedsValues);
        var entries = _db.VariableLibraryEntries.Where(entry => entry.VariableLibraryId == LibraryId && entry.Key.StartsWith("PORT_")).ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.InRange(int.Parse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture), 1024, 65535));
    }

    /// <summary>A port somebody already holds is never reserved again nor overwritten by a later run.</summary>
    [Fact]
    public async Task APortTheLibraryAlreadyHolds_IsKeptByTheNextRun_AndNothingIsReservedTwice()
    {
        await _sut.ApplyAsync(Request(), Ct);
        var first = LibraryValues();

        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.Equal(first["PORT_FRONT"], LibraryValues()["PORT_FRONT"]);
        Assert.Equal(first["PORT_BACK"], LibraryValues()["PORT_BACK"]);
        Assert.Equal(ProductionEntryOutcome.Kept, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        Assert.Equal(2, _db.ServerPortReservations.Count(reservation => reservation.ServerId == ServerId));
    }
}
