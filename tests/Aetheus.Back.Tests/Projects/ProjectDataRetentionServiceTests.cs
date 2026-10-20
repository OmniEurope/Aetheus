// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary>The « Conservation des données » setting of a project.</summary>
public sealed class ProjectDataRetentionServiceTests
{
    private readonly IProjectDataRetentionRepository _repository = Substitute.For<IProjectDataRetentionRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly Project _project = new() { Id = 4, Name = "Atlas" };

    public ProjectDataRetentionServiceTests() =>
        _repository.FindAsync(4, Arg.Any<CancellationToken>()).Returns(_project);

    private ProjectDataRetentionService Service() =>
        new(_repository, _audit, new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task AProjectWithoutAChoice_IsStandard()
    {
        var dto = await Service().GetAsync(4, TestContext.Current.CancellationToken);

        Assert.Equal(DataRetentionProfile.Standard, dto!.Profile);
        Assert.Equal(new DataRetentionValues(5, 7, 13, 30, 14), dto.Effective);
    }

    [Theory]
    [InlineData(DataRetentionProfile.Light, 15, 2, 3, 7, 7)]
    [InlineData(DataRetentionProfile.Standard, 5, 7, 13, 30, 14)]
    [InlineData(DataRetentionProfile.Complete, 1, 30, 13, 90, 30)]
    public void EachProfile_HasTheValuesOfThePlanTable(
        DataRetentionProfile profile, int export, int detail, int aggregates, int analysis, int logs)
    {
        Assert.Equal(new DataRetentionValues(export, detail, aggregates, analysis, logs),
            DataRetentionProfiles.Resolve(profile, DataRetentionOverrides.None));
    }

    [Fact]
    public async Task AnOverride_ReplacesOnlyItsValue_AndIsAudited()
    {
        var dto = await Service().UpdateAsync(4, new UpdateProjectDataRetentionRequest
        {
            Profile = DataRetentionProfile.Light,
            Overrides = new DataRetentionOverrides { TelemetryExportIntervalMinutes = 1 }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new DataRetentionValues(1, 2, 3, 7, 7), dto!.Effective);
        Assert.Equal(DataRetentionProfile.Light, _project.DataRetentionProfile);
        Assert.Equal(1, _project.TelemetryExportIntervalMinutes);
        Assert.Null(_project.AppLogRetentionDays);
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("DataRetentionUpdated", "Project", 4,
            Arg.Is<string>(details => details.Contains("profile=Light", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearingAnOverride_GoesBackToTheProfileValue()
    {
        _project.DataRetentionProfile = DataRetentionProfile.Complete;
        _project.AppLogRetentionDays = 3;

        var dto = await Service().UpdateAsync(4, new UpdateProjectDataRetentionRequest
        {
            Profile = DataRetentionProfile.Complete,
            Overrides = DataRetentionOverrides.None
        }, TestContext.Current.CancellationToken);

        Assert.Equal(30, dto!.Effective.AppLogRetentionDays);
        Assert.Null(_project.AppLogRetentionDays);
    }

    [Fact]
    public async Task TheStorageBudget_IsSavedWithTheSetting()
    {
        var dto = await Service().UpdateAsync(4, new UpdateProjectDataRetentionRequest
        {
            Profile = DataRetentionProfile.Standard,
            StorageBudgetMegabytes = 500
        }, TestContext.Current.CancellationToken);

        Assert.Equal(500, dto!.StorageBudgetMegabytes);
        Assert.Equal(500, _project.StorageBudgetMegabytes);
        Assert.Equal(500L * 1024 * 1024, ProjectStorageDefaults.BudgetBytes(_project.StorageBudgetMegabytes));
        Assert.Equal(200L * 1024 * 1024, ProjectStorageDefaults.BudgetBytes(null));
    }

    [Fact]
    public async Task AnUnknownProfile_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service().UpdateAsync(4,
            new UpdateProjectDataRetentionRequest { Profile = (DataRetentionProfile)42 },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownProject_ReturnsNull()
    {
        Assert.Null(await Service().GetAsync(99, TestContext.Current.CancellationToken));
    }
}
