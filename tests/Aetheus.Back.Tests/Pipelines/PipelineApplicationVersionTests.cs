// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-33: the release version of a versioned pipeline (release_number: allocate and APPLICATION_VERSION_PREFIX), on the
/// real per-series counters (<see cref="ProjectReleaseNumberRepository"/>, EF InMemory): allocated as series.number,
/// replaced by the run parameter applicationVersion, refused when malformed or already published, proposed to the
/// launch dialog without being taken. What it does not prove is the launch itself on PostgreSQL (row lock), which the
/// real candidate run proves.
/// </summary>
public sealed class PipelineApplicationVersionTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectReleaseNumberRepository Repository() => new(_db, TimeProvider.System);

    private static PipelineYamlDefinition Versioned(string? prefix = "0.1", string? releaseNumber = "allocate")
    {
        var variables = new Dictionary<string, string>();
        if (prefix is not null) variables[PipelineApplicationVersion.PrefixVariable] = prefix;
        return new PipelineYamlDefinition { Name = "app-candidate", ReleaseNumber = releaseNumber, Variables = variables };
    }

    private async Task SeedAsync(params string[] publishedVersions)
    {
        _db.Projects.Add(new Project { Id = 1, Name = "app" });
        foreach (var version in publishedVersions)
            _db.Releases.Add(new Release { ProjectId = 1, Version = version });
        await _db.SaveChangesAsync(Ct);
    }

    /// <summary>The allocation and the composition of the launch, in the launcher's order. <paramref name="repositorySeries"/>
    /// stands for the series of the repository's VersionPrefix, read when the definition declares none.</summary>
    private async Task<Dictionary<string, string>> LaunchAsync(
        PipelineYamlDefinition definition, Dictionary<string, string>? parameters = null, string repositorySeries = "")
    {
        IPipelineReleaseNumberRepository repo = Repository();
        var variables = new Dictionary<string, string>();
        var requested = await PipelineApplicationVersion.CheckRequestedAsync(repo, definition, 1, parameters ?? [], Ct);
        var release = requested is not null ? null : await PipelineReleaseNumbers.ResolveAsync(
            repo, definition, 1, variables, PipelineApplicationVersion.DeclaredSeries(definition) ?? repositorySeries, Ct);
        await PipelineApplicationVersion.ApplyAsync(repo, definition, 1, requested, release, variables, Ct);
        return variables;
    }

    [Fact]
    public async Task WithoutRequestedVersion_EachLaunchTakesTheNextVersionOfThePrefixSeries()
    {
        await SeedAsync();

        Assert.Equal("0.1.0", (await LaunchAsync(Versioned()))[PipelineApplicationVersion.VersionVariable]);
        Assert.Equal("0.1.1", (await LaunchAsync(Versioned()))[PipelineApplicationVersion.VersionVariable]);
    }

    [Fact]
    public async Task RaisingThePrefix_OpensANewSeriesCountedFromZero()
    {
        await SeedAsync();
        await LaunchAsync(Versioned("0.1"));

        Assert.Equal("1.0.0", (await LaunchAsync(Versioned("1.0")))[PipelineApplicationVersion.VersionVariable]);
        // An optional patch of the prefix is ignored, as the app-version function ignores the one of VersionPrefix.
        Assert.Equal("1.0.1", (await LaunchAsync(Versioned("1.0.0")))[PipelineApplicationVersion.VersionVariable]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyPrefix_CountsInTheRepositorySeriesAndStillWritesTheVersion(string prefix)
    {
        // R-18: aetheus-candidate on v5 declares the prefix empty; its series stays the VersionPrefix of
        // Directory.Build.props, the one an unprefixed definition counts in, and the template's APPLICATION_VERSION is set.
        await SeedAsync();
        var definition = Versioned(prefix);
        Assert.Null(PipelineApplicationVersion.DeclaredSeries(definition));
        Assert.True(PipelineApplicationVersion.Applies(definition));
        IPipelineReleaseNumberRepository repo = Repository();
        Assert.Equal(0, (await PipelineReleaseNumbers.ResolveAsync(repo, Versioned(prefix: null), 1, new Dictionary<string, string>(), "1.6", Ct))!.Number);

        Assert.Equal("1.6.1", (await LaunchAsync(definition, repositorySeries: "1.6"))[PipelineApplicationVersion.VersionVariable]);
        // The launch dialog cannot read the repository's series, so it proposes nothing.
        Assert.Null(await PipelineApplicationVersion.ProposeAsync(repo, definition, 1, Ct));
    }

    [Fact]
    public async Task AnEmptyPrefixInARepositoryWithoutVersionPrefix_RefusesTheLaunch()
    {
        await SeedAsync();

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => LaunchAsync(Versioned("")));

        Assert.Equal(PipelineProblemCodes.ApplicationVersionSeriesMissing, Assert.Single(refusal.Problems!).Code);
    }

    [Fact]
    public async Task TheWizardPrefix_IsUnchanged()
    {
        await SeedAsync();
        // The prefix the setup wizard writes (PipelineSetupYamlInput.DefaultVersionPrefix).
        var definition = Versioned("0.1");

        Assert.Equal("0.1", PipelineApplicationVersion.DeclaredSeries(definition));
        // The declared series wins over the repository's.
        Assert.Equal("0.1.0", (await LaunchAsync(definition, repositorySeries: "1.6"))[PipelineApplicationVersion.VersionVariable]);
    }

    [Fact]
    public async Task ADeclaredPrefixEqualToTheVersionPrefixSeries_ContinuesItsCounter()
    {
        // R-18: aetheus-candidate on v5 declares the series of its VersionPrefix ("1.6"); the numbers a definition
        // without prefix took in that series (read from Directory.Build.props) go on, and a c-<commit> release does
        // not count as a published version.
        await SeedAsync("c-0123456789abcdef0123456789abcdef01234567");
        IPipelineReleaseNumberRepository repo = Repository();
        var unprefixed = Versioned(prefix: null);
        Assert.Equal(0, (await PipelineReleaseNumbers.ResolveAsync(repo, unprefixed, 1, new Dictionary<string, string>(), "1.6", Ct))!.Number);
        Assert.Equal(1, (await PipelineReleaseNumbers.ResolveAsync(repo, unprefixed, 1, new Dictionary<string, string>(), "1.6", Ct))!.Number);

        Assert.Equal("1.6.2", (await LaunchAsync(Versioned("1.6")))[PipelineApplicationVersion.VersionVariable]);
    }

    [Fact]
    public async Task ARequestedVersion_WinsAndTakesNoNumber()
    {
        await SeedAsync();

        var variables = await LaunchAsync(Versioned(), new Dictionary<string, string> { ["applicationVersion"] = " 1.0.0-rc.1 " });

        Assert.Equal("1.0.0-rc.1", variables[PipelineApplicationVersion.VersionVariable]);
        Assert.Empty(_db.ProjectReleaseNumberAllocations);
        Assert.Equal("0.1.0", (await LaunchAsync(Versioned()))[PipelineApplicationVersion.VersionVariable]);
    }

    [Fact]
    public async Task AnEmptyRequestedVersion_IsTheAllocatedOne()
    {
        await SeedAsync();

        var variables = await LaunchAsync(Versioned(), new Dictionary<string, string> { ["applicationVersion"] = "" });

        Assert.Equal("0.1.0", variables[PipelineApplicationVersion.VersionVariable]);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0 ; rm -rf /")]
    [InlineData("1.0.0-")]
    public async Task AMalformedRequestedVersion_IsRefusedBeforeAnyNumberIsTaken(string requested)
    {
        await SeedAsync();

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() =>
            LaunchAsync(Versioned(), new Dictionary<string, string> { ["applicationVersion"] = requested }));

        Assert.Equal(PipelineProblemCodes.ApplicationVersionInvalid, Assert.Single(refusal.Problems!).Code);
        Assert.Empty(_db.ProjectReleaseNumberAllocations);
    }

    [Fact]
    public async Task ARequestedVersionAlreadyPublished_IsRefusedBeforeAnyNumberIsTaken()
    {
        await SeedAsync("1.0.0");

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() =>
            LaunchAsync(Versioned(), new Dictionary<string, string> { ["applicationVersion"] = "1.0.0" }));

        var problem = Assert.Single(refusal.Problems!);
        Assert.Equal(PipelineProblemCodes.ApplicationVersionAlreadyPublished, problem.Code);
        Assert.Equal("1.0.0", problem.Parameters["version"]);
        Assert.Empty(_db.ProjectReleaseNumberAllocations);
    }

    [Fact]
    public async Task AnAllocatedVersionAlreadyPublished_IsRefused()
    {
        // A version given by hand earlier (0.1.0) that the series reaches later.
        await SeedAsync("0.1.0");

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => LaunchAsync(Versioned()));

        Assert.Equal(PipelineProblemCodes.ApplicationVersionAlreadyPublished, Assert.Single(refusal.Problems!).Code);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("1")]
    [InlineData("1.x")]
    [InlineData("v1.6")]
    public async Task AMalformedPrefix_RefusesTheLaunch(string prefix)
    {
        await SeedAsync();

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => LaunchAsync(Versioned(prefix)));

        Assert.Equal(PipelineProblemCodes.ApplicationVersionPrefixInvalid, Assert.Single(refusal.Problems!).Code);
    }

    [Fact]
    public async Task ADefinitionWithoutPrefixOrAllocation_IsNotVersioned()
    {
        await SeedAsync();
        var passed = new Dictionary<string, string> { [PipelineApplicationVersion.VersionVariable] = "9.9.9" };
        IPipelineReleaseNumberRepository repo = Repository();

        // Aetheus's own candidate: it allocates, but its series is its Directory.Build.props, not a prefix.
        await PipelineApplicationVersion.ApplyAsync(repo, Versioned(prefix: null), 1, null, new ReleaseNumberAssignment("1.6", 3), passed, Ct);
        // A child CI run: it receives the version from the candidate and allocates nothing; the value passed stays.
        await PipelineApplicationVersion.ApplyAsync(repo, Versioned(releaseNumber: null), 1, null, null, passed, Ct);

        Assert.Equal("9.9.9", passed[PipelineApplicationVersion.VersionVariable]);
        Assert.Null(await PipelineApplicationVersion.CheckRequestedAsync(
            repo, Versioned(releaseNumber: null), 1, new Dictionary<string, string> { ["applicationVersion"] = "1.0.0" }, Ct));
    }

    [Fact]
    public async Task AVersionedLaunch_OverwritesAVersionTheCallerPassed()
    {
        await SeedAsync("0.1.0");
        var variables = new Dictionary<string, string> { [PipelineApplicationVersion.VersionVariable] = "0.1.0" };
        IPipelineReleaseNumberRepository repo = Repository();

        await PipelineApplicationVersion.ApplyAsync(repo, Versioned(), 1, null, new ReleaseNumberAssignment("0.1", 4), variables, Ct);

        Assert.Equal("0.1.4", variables[PipelineApplicationVersion.VersionVariable]);
    }

    [Fact]
    public async Task TheProposal_IsTheNextVersionAndTakesNothing()
    {
        await SeedAsync();
        IPipelineReleaseNumberRepository repo = Repository();
        await LaunchAsync(Versioned());

        Assert.Equal("0.1.1", await PipelineApplicationVersion.ProposeAsync(repo, Versioned(), 1, Ct));
        Assert.Equal("0.1.1", await PipelineApplicationVersion.ProposeAsync(repo, Versioned(), 1, Ct));
        Assert.Single(_db.ProjectReleaseNumberAllocations);
        Assert.Null(await PipelineApplicationVersion.ProposeAsync(repo, Versioned("bad"), 1, Ct));
        Assert.Null(await PipelineApplicationVersion.ProposeAsync(repo, Versioned(prefix: null), 1, Ct));
        Assert.Null(await PipelineApplicationVersion.ProposeAsync(repo, Versioned(), 404, Ct));
        Assert.Equal("0.1.1", (await LaunchAsync(Versioned()))[PipelineApplicationVersion.VersionVariable]);
    }

    [Fact]
    public async Task TheFacade_AnswersLikeThePort()
    {
        await SeedAsync("0.1.0");
        IPipelineReleaseNumberRepository facade = new PipelineRepository(
            _db, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new Aetheus.Back.Components.Tasks.PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

        Assert.True(await facade.ReleaseVersionExistsAsync(1, "0.1.0", Ct));
        Assert.False(await facade.ReleaseVersionExistsAsync(1, "0.1.1", Ct));
        Assert.False(await facade.ReleaseVersionExistsAsync(2, "0.1.0", Ct));
        Assert.Equal(0, await facade.PeekNextReleaseNumberAsync(1, "0.1", Ct));
        Assert.Null(await facade.PeekNextReleaseNumberAsync(404, "0.1", Ct));
    }

    public void Dispose() => _db.Dispose();
}
