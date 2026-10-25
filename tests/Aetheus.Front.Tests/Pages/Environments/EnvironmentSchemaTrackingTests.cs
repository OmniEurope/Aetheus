// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Environments;
using Bunit;

namespace Aetheus.Front.Tests.Pages.EnvironmentsTests;

/// <summary>
/// Recette R-18: a production environment's page shows the database's expand/contract tracking: the last gate verdict of a
/// migrate step that ran on its servers and the contractions still due. Other environments show nothing of it.
/// </summary>
public sealed class EnvironmentSchemaTrackingTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public EnvironmentSchemaTrackingTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [new ProjectDto { Id = 1, Name = "P" }], TotalCount = 1 });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>());
    }

    private static EnvironmentSchemaDto Schema(bool withGate) => new()
    {
        EnvironmentId = 1,
        LastGate = withGate
            ? new PipelineRunMigrationGateDto
            {
                Id = 4,
                RunId = 2819,
                StageName = "Deploy",
                StepName = "Migrate",
                Outcome = MigrationGateOutcome.NoPending,
                RecordedAt = DateTime.UtcNow
            }
            : null,
        PendingContractions =
        [
            new PendingContractionDto
            {
                Id = "vault", Table = "VaultSecretVersions", Columns = ["EncryptedValue"], Step = SchemaContractionStep.Drop,
                ExpandMigration = "20260924_Expand", RequiredAfterMigration = "20261010_Unmap"
            }
        ]
    };

    [Fact]
    public void ShowsTheLastVerdictItsRunAndThePendingContractions()
    {
        _handler.SetJsonResponse("api/environments/1/schema-tracking", Schema(withGate: true));

        var cut = Render<EnvironmentSchemaTracking>(p => p.Add(c => c.EnvironmentId, 1));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='environment-schema-tracking']")));
        Assert.Contains("Enum_MigrationGateOutcome_NoPending", cut.Find("[data-testid='migration-gate-verdict']").TextContent, StringComparison.Ordinal);
        Assert.Equal("/pipelines/runs/2819?tab=schema", cut.Find("[data-testid='environment-schema-run-link']").GetAttribute("href"));
        Assert.Contains("VaultSecretVersions.EncryptedValue", cut.Find("[data-testid='schema-pending-contractions']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAVerdict_SaysSoAndStillListsThePendingContractions()
    {
        _handler.SetJsonResponse("api/environments/1/schema-tracking", Schema(withGate: false));

        var cut = Render<EnvironmentSchemaTracking>(p => p.Add(c => c.EnvironmentId, 1));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='environment-schema-no-gate']")));
        Assert.Empty(cut.FindAll("[data-testid='run-migration-gate']"));
        Assert.NotNull(cut.Find("[data-testid='schema-pending-contractions']"));
    }

    [Fact]
    public void AnUnreadableAnswer_RendersNothingInsteadOfFailingThePage()
    {
        _handler.SetResponse("api/environments/1/schema-tracking", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<EnvironmentSchemaTracking>(p => p.Add(c => c.EnvironmentId, 1));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("/environments/1/schema-tracking", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='environment-schema-tracking']"));
    }

    [Theory]
    [InlineData(EnvironmentType.Production, true)]
    [InlineData(EnvironmentType.Staging, false)]
    public void ThePage_AsksForTheTrackingOfAProductionEnvironmentOnly(EnvironmentType type, bool expected)
    {
        _handler.SetJsonResponse("api/environments/1", new EnvironmentDto { Id = 1, Name = "env", Type = type, ProjectId = 1, ProjectName = "P" });
        _handler.SetJsonResponse("api/environments/1/schema-tracking", Schema(withGate: true));

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/environments/1", StringComparison.Ordinal)));
        if (expected)
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='environment-schema-tracking']")));
        else
        {
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("form")));
            Assert.Empty(cut.FindAll("[data-testid='environment-schema-tracking']"));
            Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("schema-tracking", StringComparison.Ordinal));
        }
    }
}
