// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-65, R-66: a library or a vault is "used by" the pipelines whose definition names
/// it for their project, and the vault a monitored application keeps its keys in is used by that
/// application; a resource nothing reads is absent from the answer.</summary>
public sealed class PipelineResourceUsageServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineResourceUsageRepository _repository = Substitute.For<IPipelineResourceUsageRepository>();
    private readonly IPipelineTemplateRepository _templates = Substitute.For<IPipelineTemplateRepository>();
    private readonly PipelineResourceUsageService _sut;

    public PipelineResourceUsageServiceTests()
    {
        _sut = new PipelineResourceUsageService(_repository, _templates, NullLogger<PipelineResourceUsageService>.Instance);
        _repository.GetLibrariesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);
        _repository.GetVaultsAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns([]);
        _repository.GetAnalyticsAppsAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    private static string Yaml(string libraries, string vaults = "") =>
        "name: p\n"
        + (libraries.Length > 0 ? "variable_libraries:\n" + libraries + "\n" : "")
        + (vaults.Length > 0 ? "vaults:\n" + vaults + "\n" : "")
        + "stages:\n  - name: Deploy\n    steps:\n      - name: render\n        shell: echo hello\n";

    [Fact]
    public async Task ALibraryIsUsedByThePipelinesOfItsProject_AndAnUnreadLibraryIsAbsent()
    {
        _repository.GetLibrariesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new UsageResourceRow(1, "shared.prod", 7),
            new UsageResourceRow(2, "legacy.public", 7),
            new UsageResourceRow(3, "shared.prod", 8)
        ]);
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UsagePipelineRow(10, "deploy", 7, "Aetheus", null, Yaml("  - shared.prod")),
            new UsagePipelineRow(11, "alpha", 7, "Aetheus", null, Yaml("  - shared.prod"))
        ]);

        var usage = await _sut.GetAsync(null, null, null, Ct);

        // The same name owned by another project is not read by these pipelines.
        Assert.Equal(["alpha", "deploy"], usage.Libraries[1].Select(user => user.Name));
        Assert.All(usage.Libraries[1], user => Assert.Equal(ResourceUsageKind.Pipeline, user.Kind));
        Assert.False(usage.Libraries.ContainsKey(2));
        Assert.False(usage.Libraries.ContainsKey(3));
    }

    [Fact]
    public async Task TheProjectSlugExpressionIsReplacedBeforeTheNameIsMatched()
    {
        _repository.GetLibrariesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns([new UsageResourceRow(1, "mon-aetheus.share", 7)]);
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UsagePipelineRow(10, "deploy", 7, "Mon Aetheus", null, Yaml("  - ${{ project.slug }}.share"))
        ]);

        var usage = await _sut.GetAsync(null, null, null, Ct);

        Assert.Equal("deploy", Assert.Single(usage.Libraries[1]).Name);
    }

    [Fact]
    public async Task AVaultIsUsedByPipelinesAndByTheMonitoredApplicationKeepingItsAnalyticsKeysThere()
    {
        _repository.GetVaultsAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new UsageResourceRow(5, "aetheus.analytics", 7),
            new UsageResourceRow(6, "aetheus-web-analytics-1", 7),
            new UsageResourceRow(7, "aetheus.demo", 7)
        ]);
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UsagePipelineRow(10, "demo", 7, "Aetheus", null, Yaml("  - lib.share", "  - aetheus.demo"))
        ]);
        _repository.GetAnalyticsAppsAsync(Arg.Any<CancellationToken>())
            .Returns([new UsageAnalyticsAppRow(40, "Site", 7, "aetheus.analytics")]);

        var usage = await _sut.GetAsync(null, null, null, Ct);

        var app = Assert.Single(usage.Vaults[5]);
        Assert.Equal((ResourceUsageKind.MonitoredApp, 40, "Site", 7), (app.Kind, app.Id, app.Name, app.ProjectId));
        Assert.Equal("demo", Assert.Single(usage.Vaults[7]).Name);
        // The old per-application vault is read by nothing: it is not listed.
        Assert.False(usage.Vaults.ContainsKey(6));
    }

    [Fact]
    public async Task APipelineTheCallerCannotReadIsReportedWithoutItsName_SoTheResourceNeverLooksUnused()
    {
        _repository.GetLibrariesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns([new UsageResourceRow(1, "shared.prod", 7)]);
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UsagePipelineRow(10, "secret-deploy", 7, "Aetheus", null, Yaml("  - shared.prod")),
            new UsagePipelineRow(11, "other-secret", 7, "Aetheus", null, Yaml("  - shared.prod"))
        ]);

        var usage = await _sut.GetAsync(null, null, [], Ct);

        var hidden = Assert.Single(usage.Libraries[1]);
        Assert.Equal(0, hidden.Id);
        Assert.Equal(string.Empty, hidden.Name);
    }

    [Fact]
    public async Task PipelinesExtendingTheSameTemplate_ReadItsRowsOnce_AndStillReportTheUsage()
    {
        var template = new Aetheus.Back.Data.Entities.PipelineTemplate { Id = 3, Name = "base", OrganizationId = 1, LatestVersion = 2 };
        var version = new Aetheus.Back.Data.Entities.PipelineTemplateVersion
        {
            TemplateId = 3,
            Version = 2,
            YamlContent = Yaml("  - shared.prod")
        };
        _templates.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(template);
        _templates.GetTemplateVersionAsync(3, 2, Arg.Any<CancellationToken>()).Returns(version);
        _repository.GetLibrariesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns([new UsageResourceRow(1, "shared.prod", 7)]);
        const string child = "name: c\nextends: base\n";
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UsagePipelineRow(10, "deploy", 7, "Aetheus", 1, child),
            new UsagePipelineRow(11, "alpha", 7, "Aetheus", 1, child)
        ]);

        var usage = await _sut.GetAsync(null, null, null, Ct);

        Assert.Equal(["alpha", "deploy"], usage.Libraries[1].Select(user => user.Name));
        await _templates.Received(1).FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>());
        await _templates.Received(1).GetTemplateVersionAsync(3, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMissingTemplate_IsLookedUpOnce_AndThePipelineFallsBackToItsOwnYaml()
    {
        _repository.GetLibrariesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns([new UsageResourceRow(1, "shared.prod", 7)]);
        var own = "extends: gone\n" + Yaml("  - shared.prod");
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UsagePipelineRow(10, "deploy", 7, "Aetheus", 1, own),
            new UsagePipelineRow(11, "alpha", 7, "Aetheus", 1, own)
        ]);

        var usage = await _sut.GetAsync(null, null, null, Ct);

        Assert.Equal(["alpha", "deploy"], usage.Libraries[1].Select(user => user.Name));
        await _templates.Received(1).FindTemplateByNameAsync("gone", 1, Arg.Any<CancellationToken>());
        await _templates.Received(1).FindSharedTemplateByNameAsync("gone", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APipelineWithoutAProject_OnlyReadsAnOwnerlessResource()
    {
        _repository.GetLibrariesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new UsageResourceRow(1, "global", null),
            new UsageResourceRow(2, "owned", 7)
        ]);
        _repository.GetPipelinesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UsagePipelineRow(10, "orphan", null, null, null, Yaml("  - global\n  - owned"))
        ]);

        var usage = await _sut.GetAsync(null, null, null, Ct);

        Assert.True(usage.Libraries.ContainsKey(1));
        Assert.False(usage.Libraries.ContainsKey(2));
    }
}
