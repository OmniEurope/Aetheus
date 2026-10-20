// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Hubs;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>AUD-07: the real container builds PipelineHub with the run-lookup port, so the narrowed
/// dependency is wired in DI and not only in a unit test's hand-made constructor call.</summary>
public sealed class PipelineHubCompositionTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public void TheApplicationContainer_BuildsPipelineHubThroughTheRunLookupPort()
    {
        using var scope = factory.Services.CreateScope();

        Assert.IsType<PipelineRunLookupRepository>(scope.ServiceProvider.GetRequiredService<IPipelineRunLookupReader>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<PipelineHub>(scope.ServiceProvider));
    }
}
