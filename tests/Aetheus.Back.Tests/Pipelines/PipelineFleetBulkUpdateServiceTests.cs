// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>R-8, R-45: "Update all pipelines" moves the pipelines pinned on an older version of a shipped template,
/// generic or versioned; the pipelines are in-memory fakes, no repository file is read.</summary>
public sealed class PipelineFleetBulkUpdateServiceTests
{
    private const int CustomerId = 7;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Func<int, CancellationToken, Task<bool>> Allowed = (_, _) => Task.FromResult(true);

    private readonly FakeRepository _repository = new();
    private readonly IPipelineFleetService _fleet = Substitute.For<IPipelineFleetService>();
    private readonly PipelineFleetBulkUpdateService _service;

    public PipelineFleetBulkUpdateServiceTests()
    {
        _service = new PipelineFleetBulkUpdateService(
            _repository, _fleet, NullLogger<PipelineFleetBulkUpdateService>.Instance);
        _fleet.PreviewUpdateAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Preview(call.ArgAt<int>(0), call.ArgAt<int>(1)));
    }

    [Fact]
    public void Select_MovesOnlyPipelinesPinnedOnAnOlderVersionOfAShippedGenericTemplate()
    {
        var snapshot = Snapshot(
            [
                Candidate(1, "application-ci", 1),
                Candidate(2, "application-deploy-prod-front-back", 3),
                Candidate(3, "application-ci", 3),
                Candidate(4, "application-ci", 1, imported: true),
                Candidate(5, "application-ci", 1, organizationId: 9),
                Candidate(6, "application-candidate", 2),
                Candidate(7, "application-ci", 1, organizationId: 0),
                Candidate(8, "application-ci", 4),
                Candidate(9, "legacy-application-ci", 1)
            ],
            owned: [(9, "application-ci")]);

        var moves = GenericTemplatePinAdvancePlan.Select(snapshot);

        Assert.Equal(
            [
                new GenericTemplatePinMove(1, "pipeline-1", "application-ci", 1, 3),
                new GenericTemplatePinMove(2, "pipeline-2", "application-deploy-prod-front-back", 3, 5),
                new GenericTemplatePinMove(6, "pipeline-6", "application-candidate", 2, 5)
            ],
            moves);
    }

    [Fact]
    public void ShippedTemplateNames_CoverTheGenericAndTheVersionedTemplates()
    {
        Assert.Contains("application-deploy-prod-front-back", GenericTemplatePinAdvancePlan.ShippedTemplateNames);
        Assert.Contains("application-ci", GenericTemplatePinAdvancePlan.ShippedTemplateNames);
        Assert.Contains("application-qa", GenericTemplatePinAdvancePlan.ShippedTemplateNames);
        Assert.Contains("application-candidate", GenericTemplatePinAdvancePlan.ShippedTemplateNames);
        Assert.Contains("host-bluegreen-deploy", GenericTemplatePinAdvancePlan.ShippedTemplateNames);
        Assert.Contains("host-bluegreen-revert", GenericTemplatePinAdvancePlan.ShippedTemplateNames);
        Assert.DoesNotContain("legacy-application-ci", GenericTemplatePinAdvancePlan.ShippedTemplateNames);
    }

    [Fact]
    public async Task UpdateGenericTemplatesAsync_AdvancesAPipelinePinnedOnAVersionedTemplate()
    {
        _repository.Snapshot = Snapshot([Candidate(1, "application-candidate", 4)]);
        _fleet.PreviewUpdateAsync(1, 5, Arg.Any<CancellationToken>())
            .Returns(Preview(1, 5) with { TemplateName = "application-candidate", CurrentVersion = 4 });
        _fleet.UpdateAsync(1, Arg.Any<PipelineFleetUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineDto());

        var result = await _service.UpdateGenericTemplatesAsync(null, null, null, Allowed, Ct);

        var item = Assert.Single(result.Items);
        Assert.Equal(PipelineFleetBulkUpdateOutcome.Updated, item.Outcome);
        Assert.Equal(("application-candidate", 4, 5), (item.TemplateName, item.FromVersion, item.ToVersion));
        await _fleet.Received(1).UpdateAsync(1, Arg.Is<PipelineFleetUpdateRequest>(request =>
            request.TargetVersion == 5 && !request.AcknowledgeOrphanOverrides), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateGenericTemplatesAsync_RefusesAVersionedTemplateMoveThatOrphansAnOverride()
    {
        _repository.Snapshot = Snapshot([Candidate(1, "host-bluegreen-deploy", 1)]);
        _fleet.PreviewUpdateAsync(1, 2, Arg.Any<CancellationToken>())
            .Returns(Preview(1, 2) with
            {
                TemplateName = "host-bluegreen-deploy",
                OrphanOverrides = ["stage:cutover/step:removed-in-v2"]
            });

        var result = await _service.UpdateGenericTemplatesAsync(null, null, null, Allowed, Ct);

        var item = Assert.Single(result.Items);
        Assert.Equal(PipelineFleetBulkUpdateOutcome.Refused, item.Outcome);
        Assert.Equal(
            "the new version no longer has what the pipeline overrides: stage:cutover/step:removed-in-v2", item.Reason);
        await _fleet.DidNotReceive().UpdateAsync(1, Arg.Any<PipelineFleetUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateGenericTemplatesAsync_AppliesTheManualUpdateWithThePreviewHashAndIsIdempotent()
    {
        _repository.Snapshot = Snapshot([Candidate(1, "application-ci", 1)]);
        _fleet.UpdateAsync(1, Arg.Any<PipelineFleetUpdateRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // The real update rewrites the pin: the next read sees the pipeline on the latest version.
            _repository.Snapshot = Snapshot([Candidate(1, "application-ci", 3)]);
            return new PipelineDto();
        });

        var first = await _service.UpdateGenericTemplatesAsync(null, null, null, Allowed, Ct);
        var second = await _service.UpdateGenericTemplatesAsync(null, null, null, Allowed, Ct);

        var item = Assert.Single(first.Items);
        Assert.Equal(PipelineFleetBulkUpdateOutcome.Updated, item.Outcome);
        Assert.Equal((1, 3), (item.FromVersion, item.ToVersion));
        Assert.Empty(second.Items);
        await _fleet.Received(1).UpdateAsync(1, Arg.Is<PipelineFleetUpdateRequest>(request =>
            request.TargetVersion == 3 && !request.AcknowledgeOrphanOverrides
            && request.ExpectedSourceYamlHash == new string('A', 64)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateGenericTemplatesAsync_PassesTheScopeToTheRepository()
    {
        _repository.Snapshot = Snapshot([]);
        int[] organizations = [CustomerId];
        int[] accessible = [1, 2];

        await _service.UpdateGenericTemplatesAsync(12, organizations, accessible, Allowed, Ct);

        Assert.Equal(new GenericTemplatePinScope(12, organizations, accessible), _repository.LastScope);
    }

    [Fact]
    public async Task UpdateGenericTemplatesAsync_ReportsEachRefusalAndCarriesOnWithTheOthers()
    {
        _repository.Snapshot = Snapshot(
        [
            Candidate(1, "application-ci", 1),
            Candidate(2, "application-ci", 1),
            Candidate(3, "application-ci", 1),
            Candidate(4, "application-ci", 1),
            Candidate(5, "application-ci", 1)
        ]);
        _fleet.PreviewUpdateAsync(2, 3, Arg.Any<CancellationToken>())
            .Returns(Preview(2, 3) with { OrphanOverrides = ["stage:build/step:removed"] });
        _fleet.PreviewUpdateAsync(4, 3, Arg.Any<CancellationToken>()).Returns(Preview(4, 3) with { CurrentVersion = 2 });
        _fleet.UpdateAsync(3, Arg.Any<PipelineFleetUpdateRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("changed in the repository"));

        var result = await _service.UpdateGenericTemplatesAsync(
            null, null, null, (id, _) => Task.FromResult(id != 5), Ct);

        Assert.Equal(
            [
                (1, PipelineFleetBulkUpdateOutcome.Updated, (string?)null),
                (2, PipelineFleetBulkUpdateOutcome.Refused, "the new version no longer has what the pipeline overrides: stage:build/step:removed"),
                (3, PipelineFleetBulkUpdateOutcome.Refused, "changed in the repository"),
                (4, PipelineFleetBulkUpdateOutcome.Changed, null),
                (5, PipelineFleetBulkUpdateOutcome.Forbidden, null)
            ],
            result.Items.Select(item => (item.PipelineId, item.Outcome, item.Reason)));
        await _fleet.DidNotReceive().UpdateAsync(2, Arg.Any<PipelineFleetUpdateRequest>(), Arg.Any<CancellationToken>());
        await _fleet.DidNotReceive().UpdateAsync(4, Arg.Any<PipelineFleetUpdateRequest>(), Arg.Any<CancellationToken>());
        await _fleet.DidNotReceive().PreviewUpdateAsync(5, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateGenericTemplatesAsync_WithoutPlatformOrganization_UpdatesNothing()
    {
        var result = await _service.UpdateGenericTemplatesAsync(null, null, null, Allowed, Ct);

        Assert.Empty(result.Items);
        await _fleet.DidNotReceiveWithAnyArgs().PreviewUpdateAsync(default, default, Ct);
    }

    private static GenericTemplatePinCandidate Candidate(
        int id, string template, int pinned, bool imported = false, int organizationId = CustomerId) =>
        new(id, $"pipeline-{id}", template, pinned, organizationId, imported);

    private static GenericTemplatePinSnapshot Snapshot(
        IReadOnlyList<GenericTemplatePinCandidate> candidates, IEnumerable<(int, string)>? owned = null) =>
        new(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["application-ci"] = 3,
            ["application-deploy-prod-front-back"] = 5,
            ["application-candidate"] = 5,
            ["host-bluegreen-deploy"] = 2,
            ["legacy-application-ci"] = 4
        },
            candidates,
            (owned ?? []).ToHashSet());

    private static PipelineFleetUpdatePreviewDto Preview(int id, int target) => new()
    {
        PipelineId = id,
        TemplateName = "application-ci",
        CurrentVersion = 1,
        TargetVersion = target,
        SourceYamlHash = new string('A', 64)
    };

    private sealed class FakeRepository : IGenericTemplatePinRepository
    {
        public GenericTemplatePinSnapshot? Snapshot { get; set; }
        public GenericTemplatePinScope? LastScope { get; private set; }

        public Task<GenericTemplatePinSnapshot?> LoadAsync(
            IReadOnlyCollection<string> templateNames, GenericTemplatePinScope scope, CancellationToken ct)
        {
            LastScope = scope;
            return Task.FromResult(Snapshot);
        }
    }
}
