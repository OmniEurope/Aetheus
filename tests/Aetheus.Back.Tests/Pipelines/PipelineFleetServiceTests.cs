// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public sealed class PipelineFleetServiceTests
{
    private readonly FakePipelineFleetRepository _fleetRepository = new();
    private readonly IPipelineRepository _pipelineRepository = Substitute.For<IPipelineRepository>();
    private readonly IPipelineService _pipelineService = Substitute.For<IPipelineService>();
    private readonly IPipelineTemplateService _templateService = Substitute.For<IPipelineTemplateService>();
    private readonly IPipelineTemplateResolver _resolver = Substitute.For<IPipelineTemplateResolver>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly PipelineFleetService _service;

    public PipelineFleetServiceTests()
    {
        _resolver.ResolveAsync(
                Arg.Is<string>(yaml => yaml.StartsWith("name: template-resolution", StringComparison.Ordinal)),
                4,
                Arg.Is<IReadOnlyDictionary<string, string>?>(parameters => parameters == null),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).Contains("ci@1", StringComparison.Ordinal)
                ? Resolution(Template().Versions.Single(version => version.Version == 1).YamlContent)
                : Resolution(Template().Versions.Single(version => version.Version == 2).YamlContent));
        _service = new PipelineFleetService(
            _fleetRepository, _pipelineRepository, _pipelineService, _templateService, _resolver, _audit);
    }

    [Fact]
    public async Task GetAsync_ComputesCurrentOutdatedAndOffCatalog()
    {
        _pipelineRepository.GetTemplatesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineTemplate { Id = 10, Name = "ci", OrganizationId = 4, LatestVersion = 3 }
        ]);
        _fleetRepository.Rows =
        [
            Row(1, "current", "extends: ci@3"),
            Row(2, "outdated", "extends: ci@1"),
            Row(3, "free", "name: free\nstages: []")
        ];

        var result = await _service.GetAsync(new PipelineFleetPaginationRequest(), null, null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(PipelineFleetFreshness.Current, result.Items.Single(item => item.PipelineId == 1).Freshness);
        Assert.Equal(PipelineFleetFreshness.Outdated, result.Items.Single(item => item.PipelineId == 2).Freshness);
        Assert.Equal(PipelineFleetFreshness.OffCatalog, result.Items.Single(item => item.PipelineId == 3).Freshness);
    }

    [Fact]
    public async Task GetAsync_PaginatesWithoutMaterializingAClientSideCandidateSet()
    {
        _fleetRepository.Rows = Enumerable.Range(1, 10_001)
            .Select(id => Row(id, $"pipeline-{id}", "stages: []"))
            .ToList();

        var result = await _service.GetAsync(new PipelineFleetPaginationRequest
        {
            Page = 401,
            PageSize = 25
        }, null, null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(10_001, result.TotalCount);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task GetAsync_SortsGloballyBeforeTakingRequestedPage()
    {
        _pipelineRepository.GetTemplatesAsync(Arg.Any<CancellationToken>()).Returns([]);
        _fleetRepository.Rows =
        [
            Row(1, "alpha", "stages: []"),
            Row(2, "zulu", "stages: []")
        ];

        var result = await _service.GetAsync(new PipelineFleetPaginationRequest
        {
            Page = 1,
            PageSize = 1,
            SortBy = "PipelineName",
            SortDescending = true
        }, null, null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal("zulu", Assert.Single(result.Items).PipelineName);
    }

    [Fact]
    public async Task PreviewUpdateAsync_ReturnsResolvedDiffAndOrphanOverride()
    {
        var row = Row(2, "outdated", "name: child\nextends: ci@1\nstages:\n  - name: build\n    steps:\n      - name: removed\n        shell: echo local");
        _fleetRepository.Rows = [row];
        _pipelineRepository.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>()).Returns(Template());
        _resolver.ResolveAsync(Arg.Is<string>(yaml => yaml.Contains("ci@1")), 4,
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).StartsWith("name: template-resolution", StringComparison.Ordinal)
                ? Resolution(Template().Versions.Single(version => version.Version == 1).YamlContent)
                : Resolution("old resolved"));
        _resolver.ResolveAsync(Arg.Is<string>(yaml => yaml.Contains("ci@2")), 4,
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).StartsWith("name: template-resolution", StringComparison.Ordinal)
                ? Resolution(Template().Versions.Single(version => version.Version == 2).YamlContent)
                : Resolution("new resolved"));

        var result = await _service.PreviewUpdateAsync(2, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal("old resolved", result.CurrentResolvedYaml);
        Assert.Equal("new resolved", result.TargetResolvedYaml);
        Assert.Contains("stage:build/step:removed", result.OrphanOverrides);
    }

    [Fact]
    public async Task PreviewUpdateAsync_ListsEveryOrphanOfAStageJobOrStep_AndNothingTheTargetStillHasOrNeverHad()
    {
        const string oldBase = """
            name: ci
            stages:
              - name: build
                steps:
                  - name: keep
                    shell: echo
                  - name: dropped
                    shell: echo
                jobs:
                  - name: compile
                    steps:
                      - name: a
                        shell: echo
                      - name: gone
                        shell: echo
                  - name: lint
                    steps:
                      - name: l
                        shell: echo
              - name: obsolete
                steps:
                  - name: x
                    shell: echo
            """;
        const string newBase = """
            name: ci
            stages:
              - name: build
                steps:
                  - name: keep
                    shell: echo
                jobs:
                  - name: compile
                    steps:
                      - name: a
                        shell: echo
            """;
        const string child = """
            name: child
            extends: ci@1
            stages:
              - name: BUILD
                steps:
                  - name: keep
                    shell: echo local
                  - name: dropped
                    shell: echo local
                jobs:
                  - name: compile
                    steps:
                      - name: a
                        shell: echo local
                      - name: gone
                        shell: echo local
                  - name: lint
                    steps:
                      - name: l
                        shell: echo local
                  - name: extra
                    steps:
                      - name: e
                        shell: echo local
              - name: obsolete
                steps:
                  - name: x
                    shell: echo local
              - name: local
                steps:
                  - name: y
                    shell: echo local
            """;
        _fleetRepository.Rows = [Row(2, "outdated", child)];
        _pipelineRepository.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>()).Returns(Template());
        _resolver.ResolveAsync(Arg.Any<string>(), 4,
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).StartsWith("name: template-resolution", StringComparison.Ordinal)
                ? Resolution(call.ArgAt<string>(0).Contains("ci@1", StringComparison.Ordinal) ? oldBase : newBase)
                : Resolution("resolved"));

        var result = await _service.PreviewUpdateAsync(2, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["stage:BUILD/step:dropped", "stage:BUILD/job:compile/step:gone", "stage:BUILD/job:lint", "stage:obsolete"],
            result.OrphanOverrides);
        Assert.True(result.RequiresOrphanAcknowledgement);
    }

    [Fact]
    public async Task UpdateAsync_OrphanWithoutAcknowledgement_IsRejectedBeforeWrite()
    {
        var row = Row(2, "outdated", "name: child\nextends: ci@1\nstages:\n  - name: build\n    steps:\n      - name: removed\n        shell: echo local");
        _fleetRepository.Rows = [row];
        _pipelineRepository.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>()).Returns(Template());
        _resolver.ResolveAsync(Arg.Any<string>(), 4, Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>()).Returns(call =>
            call.ArgAt<string>(0).StartsWith("name: template-resolution", StringComparison.Ordinal)
                ? Resolution(call.ArgAt<string>(0).Contains("ci@1", StringComparison.Ordinal)
                    ? Template().Versions.Single(version => version.Version == 1).YamlContent
                    : Template().Versions.Single(version => version.Version == 2).YamlContent)
                : Resolution("resolved"));

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _service.UpdateAsync(2, new PipelineFleetUpdateRequest
            {
                TargetVersion = 2,
                ExpectedSourceYamlHash = Hash(row.YamlDefinition)
            }, ct: TestContext.Current.CancellationToken));

        await _pipelineService.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Acknowledged_RewritesPinThroughPipelineService()
    {
        var row = Row(2, "outdated", "name: child\nextends: ci@1\nstages: []");
        _fleetRepository.Rows = [row];
        _pipelineRepository.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>()).Returns(Template());
        _resolver.ResolveAsync(Arg.Any<string>(), 4, Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>()).Returns(call =>
            call.ArgAt<string>(0).StartsWith("name: template-resolution", StringComparison.Ordinal)
                ? Resolution(call.ArgAt<string>(0).Contains("ci@1", StringComparison.Ordinal)
                    ? Template().Versions.Single(version => version.Version == 1).YamlContent
                    : Template().Versions.Single(version => version.Version == 2).YamlContent)
                : Resolution("resolved"));
        _pipelineService.UpdatePipelineAsync(2, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineDto { Id = 2 });

        await _service.UpdateAsync(2, new PipelineFleetUpdateRequest
        {
            TargetVersion = 2,
            AcknowledgeOrphanOverrides = true,
            ExpectedSourceYamlHash = Hash(row.YamlDefinition)
        }, ct: TestContext.Current.CancellationToken);

        await _pipelineService.Received(1).UpdatePipelineAsync(2,
            Arg.Is<UpdatePipelineRequest>(request => request.YamlDefinition.Contains("ci@2")),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "TemplateVersionAdopted", "Pipeline", 2,
            Arg.Is<string>(details => details.Contains("v1 -> v2", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_StalePreviewHash_IsRejectedBeforeWrite()
    {
        var row = Row(2, "outdated", "name: child\nextends: ci@1\nstages: []");
        _fleetRepository.Rows = [row];
        _pipelineRepository.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>())
            .Returns(Template());
        _resolver.ResolveAsync(
                Arg.Any<string>(), 4, Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).StartsWith("name: template-resolution", StringComparison.Ordinal)
                ? Resolution(call.ArgAt<string>(0).Contains("ci@1", StringComparison.Ordinal)
                    ? Template().Versions.Single(version => version.Version == 1).YamlContent
                    : Template().Versions.Single(version => version.Version == 2).YamlContent)
                : Resolution("resolved"));

        await Assert.ThrowsAsync<ConflictException>(() => _service.UpdateAsync(2,
            new PipelineFleetUpdateRequest
            {
                TargetVersion = 2,
                ExpectedSourceYamlHash = new string('A', 64)
            }, ct: TestContext.Current.CancellationToken));

        await _pipelineService.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_CreatesVersionOneAndOptionallyRewritesPipelineToPin()
    {
        var row = Row(3, "free", "name: free\nstages: []");
        _fleetRepository.Rows = [row];
        _templateService.CreateTemplateAsync(Arg.Any<CreatePipelineTemplateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateDto { Id = 12, Name = "shared-ci", Version = 1 });
        _pipelineService.UpdatePipelineAsync(3, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineDto { Id = 3 });

        var result = await _service.ExtractAsync(3, new ExtractPipelineTemplateRequest
        {
            TemplateName = "shared-ci",
            Category = "CI",
            RewritePipeline = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Version);
        await _templateService.Received(1).CreateTemplateAsync(
            Arg.Is<CreatePipelineTemplateRequest>(request => request.OrganizationId == 4
                && request.YamlContent == row.YamlDefinition), Arg.Any<CancellationToken>());
        await _pipelineService.Received(1).UpdatePipelineAsync(3,
            Arg.Is<UpdatePipelineRequest>(request => request.YamlDefinition.Contains("shared-ci@1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_PublicationFailure_RestoresAuthoritativePipeline()
    {
        var row = Row(3, "free", "name: free\nstages: []");
        _fleetRepository.Rows = [row];
        _pipelineService.UpdatePipelineAsync(3, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineDto { Id = 3 });
        _templateService.CreateTemplateAsync(
                Arg.Any<CreatePipelineTemplateRequest>(), Arg.Any<CancellationToken>())
            .Returns<PipelineTemplateDto>(_ => throw new InvalidOperationException("publication failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ExtractAsync(3, new ExtractPipelineTemplateRequest
            {
                TemplateName = "shared-ci",
                Category = "CI",
                RewritePipeline = true
            }, ct: TestContext.Current.CancellationToken));

        await _pipelineService.Received(2).UpdatePipelineAsync(
            3, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
        await _pipelineService.Received(1).UpdatePipelineAsync(
            3,
            Arg.Is<UpdatePipelineRequest>(request => request.YamlDefinition == row.YamlDefinition),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "TemplatePublicationCompensated",
            "Pipeline",
            3,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ReplayedSuccessfulPublication_IsIdempotent()
    {
        var template = Template();
        template.Name = "shared-ci";
        template.LatestVersion = 1;
        template.Versions.RemoveAll(version => version.Version != 1);
        template.Versions[0].ChangelogEntry = "Extracted from pipeline 'free'";
        _fleetRepository.Rows =
        [
            Row(3, "free", "name: free\nextends: shared-ci@1\nstages: []")
        ];
        _pipelineRepository.FindTemplateByNameAsync("shared-ci", 4, Arg.Any<CancellationToken>())
            .Returns(template);
        _templateService.GetTemplateAsync(10, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateDto { Id = 10, Name = "shared-ci", Version = 1 });

        var result = await _service.ExtractAsync(3, new ExtractPipelineTemplateRequest
        {
            TemplateName = "shared-ci",
            Category = "CI",
            RewritePipeline = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Version);
        await _templateService.DidNotReceive().CreateTemplateAsync(
            Arg.Any<CreatePipelineTemplateRequest>(), Arg.Any<CancellationToken>());
        await _pipelineService.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteAsync_PublishesNextVersionAndRebasesSourceWhenRequested()
    {
        var row = Row(2, "divergent", "name: child\nextends: ci@1\nstages: []");
        _fleetRepository.Rows = [row];
        _pipelineRepository.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>()).Returns(Template());
        _templateService.UpdateTemplateAsync(10, Arg.Any<UpdatePipelineTemplateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateDto { Id = 10, Name = "ci", Version = 3 });
        _pipelineService.UpdatePipelineAsync(2, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineDto { Id = 2 });

        var result = await _service.PromoteAsync(2, new PromotePipelineTemplateRequest
        {
            YamlContent = "name: ci\nstages: []",
            ChangelogEntry = "Adopt improvement",
            RebaseSourcePipeline = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Version);
        await _templateService.Received(1).UpdateTemplateAsync(10,
            Arg.Is<UpdatePipelineTemplateRequest>(request => request.ChangelogEntry == "Adopt improvement"),
            Arg.Any<CancellationToken>());
        await _pipelineService.Received(1).UpdatePipelineAsync(2,
            Arg.Is<UpdatePipelineRequest>(request => request.YamlDefinition.Contains("ci@3")
                && !request.YamlDefinition.Contains("echo local")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteAsync_ReplayedSuccessfulPublication_IsIdempotent()
    {
        var template = Template();
        var latest = template.Versions.Single(version => version.Version == 2);
        latest.ChangelogEntry = "Already published";
        _fleetRepository.Rows =
        [
            Row(2, "rebased", "name: child\nextends: ci@2\nstages: []")
        ];
        _pipelineRepository.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>())
            .Returns(template);
        _templateService.GetTemplateAsync(10, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateDto
            {
                Id = 10,
                Name = "ci",
                Version = 2,
                YamlContent = latest.YamlContent
            });

        var result = await _service.PromoteAsync(2, new PromotePipelineTemplateRequest
        {
            YamlContent = latest.YamlContent,
            ChangelogEntry = latest.ChangelogEntry,
            RebaseSourcePipeline = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Version);
        await _templateService.DidNotReceive().UpdateTemplateAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineTemplateRequest>(), Arg.Any<CancellationToken>());
        await _pipelineService.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    private static PipelineFleetRow Row(int id, string name, string yaml) =>
        new(id, name, "description", yaml, "main", 5, 5, null, null, "Toto", "Project", 4);

    private static string Hash(string yaml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));

    private static PipelineTemplate Template() => new()
    {
        Id = 10,
        Name = "ci",
        OrganizationId = 4,
        LatestVersion = 2,
        Versions =
        [
            new PipelineTemplateVersion
            {
                Version = 1,
                YamlContent = "name: ci\nstages:\n  - name: build\n    steps:\n      - name: removed\n        shell: echo old"
            },
            new PipelineTemplateVersion
            {
                Version = 2,
                YamlContent = "name: ci\nstages:\n  - name: build\n    steps:\n      - name: replacement\n        shell: echo new"
            }
        ]
    };

    private static PipelineTemplateResolution Resolution(string yaml) =>
        new(new PipelineYamlDefinition { Name = "resolved" }, yaml, "ci", 1, 1, false);

    private sealed class FakePipelineFleetRepository : IPipelineFleetRepository
    {
        public List<PipelineFleetRow> Rows { get; set; } = [];

        public Task<List<string>> GetTemplateNamesAsync(
            IReadOnlyCollection<int>? organizationIds,
            IReadOnlyCollection<int>? accessiblePipelineIds,
            CancellationToken ct) => Task.FromResult(new List<string>());

        public Task<PaginatedResult<PipelineFleetItemDto>> GetPageAsync(
            PipelineFleetPaginationRequest request,
            IReadOnlyCollection<int>? organizationIds,
            IReadOnlyCollection<int>? accessiblePipelineIds,
            CancellationToken ct)
        {
            var items = Rows.Where(row => organizationIds is null || organizationIds.Contains(row.OrganizationId))
                .Where(row => accessiblePipelineIds is null || accessiblePipelineIds.Contains(row.PipelineId))
                .Where(row => request.ProjectId is null || row.OwnerProjectId == request.ProjectId)
                .Select(row =>
                {
                    var marker = "extends:";
                    var line = row.YamlDefinition.Split('\n')
                        .FirstOrDefault(value => value.TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase));
                    var reference = line?[line.IndexOf(marker, StringComparison.OrdinalIgnoreCase)..]
                        .Split(':', 2)[1].Trim().Trim('\'', '"');
                    var separator = reference?.LastIndexOf('@') ?? -1;
                    var name = separator > 0 ? reference![..separator] : reference;
                    var pinned = separator > 0 && int.TryParse(reference![(separator + 1)..], out var version)
                        ? version
                        : (int?)null;
                    int? latest = name is null ? null : 3;
                    return new PipelineFleetItemDto
                    {
                        PipelineId = row.PipelineId,
                        PipelineName = row.PipelineName,
                        ProjectId = row.ProjectId,
                        OwnerName = row.OwnerName,
                        OwnerType = row.OwnerType,
                        OrganizationId = row.OrganizationId,
                        TemplateId = name is null ? null : 10,
                        TemplateName = name,
                        PinnedVersion = pinned,
                        LatestVersion = latest,
                        UsesLegacyReference = name is not null && pinned is null,
                        Freshness = name is null
                            ? PipelineFleetFreshness.OffCatalog
                            : (pinned ?? latest) == latest
                                ? PipelineFleetFreshness.Current
                                : PipelineFleetFreshness.Outdated
                    };
                });
            items = request.SortBy?.Equals("PipelineName", StringComparison.OrdinalIgnoreCase) == true
                ? request.SortDescending
                    ? items.OrderByDescending(item => item.PipelineName)
                    : items.OrderBy(item => item.PipelineName)
                : items.OrderBy(item => item.PipelineId);
            var all = items.ToList();
            var (page, pageSize) = request.Normalize();
            return Task.FromResult(new PaginatedResult<PipelineFleetItemDto>
            {
                Items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                TotalCount = all.Count,
                Page = page,
                PageSize = pageSize
            });
        }

        public Task<PipelineFleetRow?> GetAsync(int pipelineId, CancellationToken ct) =>
            Task.FromResult(Rows.FirstOrDefault(row => row.PipelineId == pipelineId));
    }
}
