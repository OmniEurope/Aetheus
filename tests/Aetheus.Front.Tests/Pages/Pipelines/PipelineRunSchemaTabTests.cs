// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-18: a deploy run shows the database's expand/contract tracking in a Schema tab, only when a migrate step
/// reported a gate verdict on it: the verdict card, the migrations it inspected (expand or contract, as a badge) and the
/// contractions still due with the release from which each can ship.
/// </summary>
public sealed class PipelineRunSchemaTabTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunSchemaTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private static PipelineRunDto Run(int id) => new()
    {
        Id = id,
        PipelineId = 5,
        PipelineName = "aetheus-deploy-prod",
        Status = PipelineStatus.Success,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        CompletedAt = DateTime.UtcNow,
        Steps = [new PipelineStepRunDto { Id = 1, StageName = "Deploy", StepName = "Migrate", Status = TaskExecutionStatus.Success }]
    };

    private static PipelineRunSchemaDto Schema(int runId, bool withGate) => new()
    {
        RunId = runId,
        Gates = withGate
            ?
            [
                new PipelineRunMigrationGateDto
                {
                    Id = 1, RunId = runId, StageName = "Deploy", StepName = "Migrate",
                    Outcome = MigrationGateOutcome.Compatible, PendingCount = 1, RecordedAt = DateTime.UtcNow
                }
            ]
            : [],
        Migrations =
        [
            new RunSchemaMigrationDto { MigrationId = "20261021_AddNote", Kind = SchemaMigrationKind.Expand, Applied = true },
            new RunSchemaMigrationDto
            {
                MigrationId = "20261022_DropOld", Kind = SchemaMigrationKind.Contract, Applied = false,
                Violations = ["line 12"]
            }
        ],
        PendingContractions =
        [
            new PendingContractionDto
            {
                Id = "vault", Table = "VaultSecretVersions", Columns = ["EncryptedValue"], Step = SchemaContractionStep.Drop,
                ExpandMigration = "20260924_Expand", RequiredAfterMigration = "20261010_Unmap",
                AppliedInRelease = "1.5.230", PossibleFromRelease = "1.5.231"
            },
            new PendingContractionDto
            {
                Id = "ai-unmap", Table = "AiRunnerProfiles", Columns = ["Binary", "ArgsTemplateJson"], Step = SchemaContractionStep.Unmap,
                ExpandMigration = "20261011_Api", RequiredAfterMigration = "20261011_Api"
            },
            new PendingContractionDto
            {
                Id = "ai-drop", Table = "AiRunnerProfiles", Columns = ["Binary", "ArgsTemplateJson"], Step = SchemaContractionStep.Drop,
                ExpandMigration = "20261011_Api", RequiredAfterMigration = null
            }
        ]
    };

    [Fact]
    public void TheTab_ShowsTheVerdictTheClassifiedMigrationsAndThePendingContractions()
    {
        var cut = Render<PipelineRunSchemaTab>(p => p.Add(c => c.Schema, Schema(7, withGate: true)));

        Assert.NotNull(cut.Find("[data-testid='run-migration-gate']"));

        var migrations = cut.Find("[data-testid='run-schema-migrations']");
        Assert.Contains("20261021_AddNote", migrations.TextContent, StringComparison.Ordinal);
        Assert.Contains("20261022_DropOld", migrations.TextContent, StringComparison.Ordinal);
        // The kind is a localized badge, never the raw enum name.
        Assert.Contains("Enum_SchemaMigrationKind_Expand", migrations.TextContent, StringComparison.Ordinal);
        Assert.Contains("Enum_SchemaMigrationKind_Contract", migrations.TextContent, StringComparison.Ordinal);
        Assert.Contains("SchemaMigrationApplied", migrations.TextContent, StringComparison.Ordinal);
        Assert.Contains("SchemaMigrationNotApplied", migrations.TextContent, StringComparison.Ordinal);

        var pending = cut.Find("[data-testid='schema-pending-contractions']").TextContent;
        Assert.Contains("VaultSecretVersions.EncryptedValue", pending, StringComparison.Ordinal);
        Assert.Contains("AiRunnerProfiles.(Binary, ArgsTemplateJson)", pending, StringComparison.Ordinal);
        Assert.Contains("Enum_SchemaContractionStep_Drop", pending, StringComparison.Ordinal);
        Assert.Contains("Enum_SchemaContractionStep_Unmap", pending, StringComparison.Ordinal);
        // A recorded release is shown as is; without one the condition is stated, not guessed.
        Assert.Contains("1.5.231", pending, StringComparison.Ordinal);
        Assert.Contains("SchemaPendingAfterRelease", pending, StringComparison.Ordinal);
        Assert.Contains("SchemaPendingAfterPreviousStep", pending, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTab_NoPendingContraction_SaysSo()
    {
        var cut = Render<PipelineRunSchemaTab>(p => p.Add(c => c.Schema, Schema(7, withGate: true) with { PendingContractions = [] }));

        Assert.Contains("SchemaNoPendingContractions", cut.Find("[data-testid='schema-pending-contractions']").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SchemaMigrationKind.Expand, OmniTone.Info)]
    [InlineData(SchemaMigrationKind.Contract, OmniTone.Warning)]
    public void AContractIsNeverShownLikeAnExpand(SchemaMigrationKind kind, OmniTone tone) =>
        Assert.Equal(tone, PipelineRunSchemaTab.KindTone(kind));

    [Fact]
    public void ThePage_ShowsTheSchemaTab_ForARunWithAGateRecord()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3100", Run(3100));
        _handler.SetJsonResponse("api/pipelines/runs/3100/schema-tracking", Schema(3100, withGate: true));

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 3100));

        cut.WaitForAssertion(() => Assert.Contains("SchemaTab", cut.Find(".pipeline-run-tabs").TextContent, StringComparison.Ordinal));
        // The verdict card moved out of the overview into the tab.
        Assert.Empty(cut.FindAll(".run-overview-tiles [data-testid='run-migration-gate']"));
    }

    [Fact]
    public void ThePage_OpensTheSchemaTabFromItsAddress()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3101", Run(3101));
        _handler.SetJsonResponse("api/pipelines/runs/3101/schema-tracking", Schema(3101, withGate: true));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/pipelines/runs/3101?tab=schema");

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 3101));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='run-schema-tab']")));
    }

    [Fact]
    public void ThePage_HidesTheSchemaTab_ForARunWithoutAGateRecord()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3102", Run(3102));
        _handler.SetJsonResponse("api/pipelines/runs/3102/schema-tracking", Schema(3102, withGate: false));

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 3102));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".pipeline-run-tabs")));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("/runs/3102/schema-tracking", StringComparison.Ordinal)));
        Assert.DoesNotContain("SchemaTab", cut.Find(".pipeline-run-tabs").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_HidesTheSchemaTab_WhenTheAnswerCannotBeRead()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3103", Run(3103));
        _handler.SetResponse("api/pipelines/runs/3103/schema-tracking", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 3103));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".pipeline-run-tabs")));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("/runs/3103/schema-tracking", StringComparison.Ordinal)));
        Assert.DoesNotContain("SchemaTab", cut.Find(".pipeline-run-tabs").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchemaSlug_FollowsTheYamlTab_AndIsAbsentWithoutAGate()
    {
        var run = Run(1) with { YamlSnapshot = "name: x" };

        Assert.Equal(["overview", "logs", "yaml", "schema"], PipelineRunPresentation.TabSlugs(run, false, true, false, hasSchemaTab: true));
        Assert.Equal(["overview", "logs", "yaml"], PipelineRunPresentation.TabSlugs(run, false, true, false));
    }
}
