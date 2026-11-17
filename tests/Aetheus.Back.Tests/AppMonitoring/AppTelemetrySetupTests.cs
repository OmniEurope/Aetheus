// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>Recette R-47 (user decision 2026-11-03): the pipeline wizard configures the telemetry of a project through its
/// monitored application (the path the deployment already reads to inject the ingest key and the OTLP endpoints), never
/// blocks, and the project's overview warns for as long as the repository inventory (the CVE watch one, already in the
/// database) does not list the <c>Aetheus.Telemetry</c> NuGet package. These tests run the real repository queries on an
/// in-memory EF provider: they prove the filters and the flow, not PostgreSQL's translation.</summary>
public sealed class AppTelemetrySetupTests : IDisposable
{
    private const int ProjectId = 10;

    private readonly AppDbContext _db;
    private readonly AppMonitoringRepository _repository;
    private readonly AppMonitoringService _service;

    public AppTelemetrySetupTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options);
        _repository = new AppMonitoringRepository(_db);
        _service = new AppMonitoringService(
            _repository, Substitute.For<INotificationService>(), Substitute.For<IEntityChangeNotifier>(),
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<AppMonitoringService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task AddProjectAsync(int id = ProjectId, string name = "Atlas")
    {
        _db.Projects.Add(new Project { Id = id, Name = name, OrganizationId = 7 });
        await _db.SaveChangesAsync(Ct);
    }

    private async Task AddInventoryItemAsync(int projectId, string ecosystem, string packageName, int inventoryId = 1)
    {
        if (!_db.ProjectInventories.Any(inventory => inventory.Id == inventoryId))
            _db.ProjectInventories.Add(new ProjectInventory { Id = inventoryId, OrganizationId = 7, ProjectId = projectId });
        _db.ProjectInventoryItems.Add(new ProjectInventoryItem
        {
            OrganizationId = 7,
            ProjectId = projectId,
            ProjectInventoryId = inventoryId,
            Ecosystem = ecosystem,
            PackageName = packageName,
            Version = "1.0.0",
            PackageUrl = $"pkg:{ecosystem}/{packageName}@1.0.0"
        });
        await _db.SaveChangesAsync(Ct);
    }

    // ---------- the inventory read ----------

    [Fact]
    public async Task HasTelemetryPackages_IsFalse_WhenTheInventoryHoldsOtherPackagesOnly()
    {
        await AddProjectAsync();
        await AddInventoryItemAsync(ProjectId, "NuGet", "newtonsoft.json");

        Assert.False(await _repository.HasTelemetryPackagesAsync(ProjectId, Ct));
    }

    [Fact]
    public async Task HasTelemetryPackages_IsFalse_WhenThereIsNoInventoryAtAll()
    {
        await AddProjectAsync();

        Assert.False(await _repository.HasTelemetryPackagesAsync(ProjectId, Ct));
    }

    [Fact]
    public async Task HasTelemetryPackages_IsTrue_WhenTheInventoryListsTheNuGetPackage()
    {
        await AddProjectAsync();
        // The inventory stores the normalized (lower case) NuGet name, which the watch matches advisories by.
        await AddInventoryItemAsync(ProjectId, "NuGet", "aetheus.telemetry");

        Assert.True(await _repository.HasTelemetryPackagesAsync(ProjectId, Ct));
    }

    [Fact]
    public async Task HasTelemetryPackages_IgnoresAnotherEcosystemAndAnotherProject()
    {
        await AddProjectAsync();
        await AddProjectAsync(11, "Other");
        await AddInventoryItemAsync(ProjectId, "npm", "aetheus.telemetry", inventoryId: 1);
        await AddInventoryItemAsync(11, "NuGet", "aetheus.telemetry", inventoryId: 2);

        Assert.False(await _repository.HasTelemetryPackagesAsync(ProjectId, Ct));
        Assert.True(await _repository.HasTelemetryPackagesAsync(11, Ct));
    }

    // ---------- the configuration ----------

    [Fact]
    public async Task Configure_CreatesTheMonitoredApplicationOfTheProject_NamedAfterIt_AndMarksTheTelemetryRequested()
    {
        await AddProjectAsync();

        var setup = await _service.ConfigureTelemetrySetupAsync(ProjectId, Ct);

        var app = await _db.MonitoredApps.SingleAsync(Ct);
        Assert.Equal(ProjectId, app.ProjectId);
        Assert.Equal("Atlas", app.Name);
        Assert.True(app.Enabled);
        Assert.Null(app.ProbeUrl);
        Assert.True(app.TelemetrySetupRequested);
        Assert.NotNull(setup);
        Assert.True(setup!.Requested);
    }

    [Fact]
    public async Task Configure_RunTwice_KeepsOneApplication()
    {
        await AddProjectAsync();

        await _service.ConfigureTelemetrySetupAsync(ProjectId, Ct);
        await _service.ConfigureTelemetrySetupAsync(ProjectId, Ct);

        Assert.Equal(1, await _db.MonitoredApps.CountAsync(Ct));
    }

    [Fact]
    public async Task Configure_ReusesAnApplicationTheProjectAlreadyMonitors_InsteadOfCreatingASecondOne()
    {
        await AddProjectAsync();
        _db.MonitoredApps.Add(new MonitoredApp { Id = 5, ProjectId = ProjectId, Name = "atlas-api", Enabled = true });
        await _db.SaveChangesAsync(Ct);

        await _service.ConfigureTelemetrySetupAsync(ProjectId, Ct);

        var app = await _db.MonitoredApps.SingleAsync(Ct);
        Assert.Equal("atlas-api", app.Name);
        Assert.True(app.TelemetrySetupRequested);
    }

    [Fact]
    public async Task Configure_ReturnsNull_ForAProjectThatDoesNotExist_AndCreatesNothing()
    {
        Assert.Null(await _service.ConfigureTelemetrySetupAsync(99, Ct));
        Assert.Empty(_db.MonitoredApps);
    }

    // ---------- R-26: the application the wizard prepares the collection of ----------

    [Fact]
    public async Task EnsureCollectionApp_CreatesTheApplication_AndSaysItWasCreated_OnceOnly()
    {
        await AddProjectAsync();

        var first = await _service.EnsureCollectionAppAsync(ProjectId, telemetry: true, Ct);
        var second = await _service.EnsureCollectionAppAsync(ProjectId, telemetry: true, Ct);

        Assert.NotNull(first);
        Assert.True(first.Created);
        Assert.NotNull(second);
        Assert.False(second.Created);
        Assert.Equal(first.AppId, second.AppId);
        Assert.Equal(1, await _db.MonitoredApps.CountAsync(Ct));
        Assert.True((await _db.MonitoredApps.SingleAsync(Ct)).Enabled);
    }

    [Fact]
    public async Task EnsureCollectionApp_ForTheAudienceAlone_DoesNotAskTheTelemetryWarning()
    {
        await AddProjectAsync();

        await _service.EnsureCollectionAppAsync(ProjectId, telemetry: false, Ct);

        var app = await _db.MonitoredApps.SingleAsync(Ct);
        Assert.True(app.Enabled);
        Assert.False(app.TelemetrySetupRequested);
    }

    [Fact]
    public async Task EnsureCollectionApp_ReturnsNull_ForAProjectThatDoesNotExist()
    {
        Assert.Null(await _service.EnsureCollectionAppAsync(99, telemetry: true, Ct));
        Assert.Empty(_db.MonitoredApps);
    }

    // ---------- the overview warning ----------

    [Fact]
    public async Task Setup_IsNotRequested_ForAProjectThatNeverChoseTheTelemetry_SoNothingIsPending()
    {
        await AddProjectAsync();
        _db.MonitoredApps.Add(new MonitoredApp { Id = 5, ProjectId = ProjectId, Name = "atlas-probe", Enabled = true });
        await _db.SaveChangesAsync(Ct);

        var setup = await _service.GetTelemetrySetupAsync(ProjectId, Ct);

        Assert.False(setup.Requested);
        Assert.False(setup.Pending);
    }

    [Fact]
    public async Task Setup_IsPending_WhileTheRepositoryInventoryHasNoTelemetryPackage()
    {
        await AddProjectAsync();
        await _service.ConfigureTelemetrySetupAsync(ProjectId, Ct);
        await AddInventoryItemAsync(ProjectId, "NuGet", "newtonsoft.json");

        var setup = await _service.GetTelemetrySetupAsync(ProjectId, Ct);

        Assert.True(setup.Requested);
        Assert.False(setup.PackagesDetected);
        Assert.True(setup.Pending);
    }

    [Fact]
    public async Task Setup_IsNoLongerPending_OnceTheInventoryListsTheTelemetryPackage()
    {
        await AddProjectAsync();
        await _service.ConfigureTelemetrySetupAsync(ProjectId, Ct);
        await AddInventoryItemAsync(ProjectId, "NuGet", "aetheus.telemetry");

        var setup = await _service.GetTelemetrySetupAsync(ProjectId, Ct);

        Assert.True(setup.Requested);
        Assert.True(setup.PackagesDetected);
        Assert.False(setup.Pending);
    }

    [Fact]
    public async Task Setup_IgnoresADisabledApplication()
    {
        await AddProjectAsync();
        _db.MonitoredApps.Add(new MonitoredApp
        {
            Id = 5,
            ProjectId = ProjectId,
            Name = "old",
            Enabled = false,
            TelemetrySetupRequested = true
        });
        await _db.SaveChangesAsync(Ct);

        Assert.False((await _service.GetTelemetrySetupAsync(ProjectId, Ct)).Requested);
    }
}
