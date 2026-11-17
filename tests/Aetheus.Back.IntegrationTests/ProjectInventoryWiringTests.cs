// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aetheus.Back.IntegrationTests;

/// <summary>The real host's container: the handler hears both announcements, the worker is hosted, and the
/// services resolve with the real Git module beneath them.</summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class ProjectInventoryWiringTests(ApiSmokeFixture fixture)
{
    [Fact]
    public void TheHost_WiresThePushToTheInventory()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.Contains(services.GetServices<IDomainEventHandler<GitPushProcessedEvent>>(), handler => handler is GitBranchInventoryHandler);
        Assert.Contains(services.GetServices<IDomainEventHandler<GitBranchMovedEvent>>(), handler => handler is GitBranchInventoryHandler);
        Assert.Contains(fixture.Factory.Services.GetServices<IHostedService>(), service => service is ProjectInventoryRefreshService);
        Assert.NotNull(services.GetRequiredService<IProjectInventoryService>());
        Assert.NotNull(services.GetRequiredService<IVulnerabilityMatchService>());
        Assert.IsType<GitProjectSourceReader>(services.GetRequiredService<IProjectSourceReader>());
    }

    [Fact]
    public void TheHost_WiresTheMatchingPassToThePublicationOfTheFindings()
    {
        using var scope = fixture.Factory.Services.CreateScope();

        Assert.Contains(
            scope.ServiceProvider.GetServices<IDomainEventHandler<VulnerabilityMatchesChangedEvent>>(),
            handler => handler is CveWatchPublication);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>());
    }

    [Fact]
    public void TheHost_AnnouncesAfterItHasPublished()
    {
        using var scope = fixture.Factory.Services.CreateScope();

        // The dispatcher runs the handlers in this order, and only a match that has its finding is announced. The
        // automatic dependency updates (PLAN-007) follow each matching pass and are registered after the analysis module
        // on purpose: they start from the stored matches, once the publication and the announcement have run.
        var order = scope.ServiceProvider.GetServices<IDomainEventHandler<VulnerabilityMatchesChangedEvent>>()
            .Select(handler => handler.GetType()).ToList();

        Assert.Equal(
            [typeof(CveWatchPublication), typeof(CveWatchAnnouncement), typeof(DependencyUpdatePass)],
            order);
    }
}
