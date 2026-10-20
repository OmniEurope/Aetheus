// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R1-43: the run page shows what the expand/contract gate of each blue-green migrate step
/// concluded, read from the run's endpoint, and shows nothing for a run without a gate.
/// </summary>
public sealed class PipelineRunMigrationGateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunMigrationGateTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineRunDto Run(PipelineStatus status = PipelineStatus.Failed) => new()
    {
        Id = 4242,
        PipelineId = 5,
        PipelineName = "deploy-prod",
        Status = status,
        StartedAt = DateTime.UtcNow.AddMinutes(-5)
    };

    [Fact]
    public void ARefusedGate_ShowsTheVerdictTheStepAndEachMigrationWithItsViolations()
    {
        _handler.SetJsonResponse("api/pipelines/runs/4242/migration-gate", new List<PipelineRunMigrationGateDto>
        {
            new()
            {
                Id = 1, StageName = "Deploy", StepName = "Migrate", Outcome = MigrationGateOutcome.Refused,
                PendingCount = 2, RecordedAt = DateTime.UtcNow,
                Migrations =
                [
                    new MigrationGateMigrationVerdict { MigrationId = "20261013_AddNote", Compatible = true },
                    new MigrationGateMigrationVerdict
                    {
                        MigrationId = "20261014_DropOld",
                        Violations = ["20261014_DropOld: line 12 is not expand-compatible: migrationBuilder.DropColumn("]
                    }
                ]
            }
        });

        var cut = Render<PipelineRunMigrationGate>(p => p.Add(c => c.Run, Run()));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='run-migration-gate']")));
        var verdict = cut.Find("[data-testid='migration-gate-verdict']");
        var badge = verdict.QuerySelector(".omni-badge")!;
        Assert.Equal("Enum_MigrationGateOutcome_Refused", badge.TextContent.Trim());
        Assert.Contains("danger", badge.ClassName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MigrationGateSummaryRefused", verdict.TextContent, StringComparison.Ordinal);
        Assert.Contains("Deploy / Migrate", cut.Markup, StringComparison.Ordinal);
        var migrations = cut.FindAll("[data-testid='migration-gate-migration']");
        Assert.Equal(2, migrations.Count);
        Assert.Contains("20261013_AddNote", migrations[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("MigrationGateMigrationCompatible", migrations[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(migrations[0].QuerySelectorAll(".run-migration-gate-violations li"));
        Assert.Contains("MigrationGateMigrationRefused", migrations[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("not expand-compatible",
            migrations[1].QuerySelector(".run-migration-gate-violations li")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnavailableGate_ShowsItsReason()
    {
        _handler.SetJsonResponse("api/pipelines/runs/4242/migration-gate", new List<PipelineRunMigrationGateDto>
        {
            new()
            {
                Id = 1, StepName = "Migrate", Outcome = MigrationGateOutcome.Unavailable, RecordedAt = DateTime.UtcNow,
                Detail = "Migration sources not found at '/srv/x'"
            }
        });

        var cut = Render<PipelineRunMigrationGate>(p => p.Add(c => c.Run, Run()));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".run-migration-gate-detail")));
        Assert.Equal("Migration sources not found at '/srv/x'", cut.Find(".run-migration-gate-detail").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='migration-gate-migration']"));
    }

    [Fact]
    public void ARunWithoutAGate_RendersNothing()
    {
        _handler.SetJsonResponse("api/pipelines/runs/4242/migration-gate", new List<PipelineRunMigrationGateDto>());

        var cut = Render<PipelineRunMigrationGate>(p => p.Add(c => c.Run, Run(PipelineStatus.Success)));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("/migration-gate", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='run-migration-gate']"));
    }

    [Fact]
    public void AnUnreadableAnswer_RendersNothingInsteadOfFailingThePage()
    {
        _handler.SetResponse("api/pipelines/runs/4242/migration-gate", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<PipelineRunMigrationGate>(p => p.Add(c => c.Run, Run()));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("/migration-gate", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='run-migration-gate']"));
    }

    [Fact]
    public void TheVerdictIsReadAgainWhenTheRunStatusChanges_NotOnEveryRender()
    {
        _handler.SetJsonResponse("api/pipelines/runs/4242/migration-gate", new List<PipelineRunMigrationGateDto>());

        var cut = Render<PipelineRunMigrationGate>(p => p.Add(c => c.Run, Run(PipelineStatus.Running)));
        cut.Render(p => p.Add(c => c.Run, Run(PipelineStatus.Running)));
        cut.Render(p => p.Add(c => c.Run, Run(PipelineStatus.Failed)));

        cut.WaitForAssertion(() => Assert.Equal(2,
            _handler.Requests.Count(r => r.Url.Contains("/migration-gate", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData(MigrationGateOutcome.Compatible, OmniTone.Success)]
    [InlineData(MigrationGateOutcome.Refused, OmniTone.Danger)]
    [InlineData(MigrationGateOutcome.Unavailable, OmniTone.Danger)]
    [InlineData(MigrationGateOutcome.NoPending, OmniTone.Neutral)]
    [InlineData(MigrationGateOutcome.InitialCreation, OmniTone.Accent)]
    public void ARefusalIsNeverGreen(MigrationGateOutcome outcome, OmniTone tone) =>
        Assert.Equal(tone, PipelineRunMigrationGate.OutcomeTone(outcome));
}
