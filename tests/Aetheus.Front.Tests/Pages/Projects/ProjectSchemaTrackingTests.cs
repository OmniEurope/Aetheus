// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// Recette R-10: the project overview's "Expand/contract" tab groups the database's expand/contract tracking by
/// environment, production first. Each group is the tracking of the environment page, read through the
/// environment's own endpoint.
/// </summary>
public sealed class ProjectSchemaTrackingTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectSchemaTrackingTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static EnvironmentDto Environment(int id, string name, EnvironmentType type) =>
        new() { Id = id, Name = name, Type = type, ProjectId = 1, ProjectName = "P" };

    private static EnvironmentSchemaDto WithGate(int environmentId, int runId, int pending = 1) => new()
    {
        EnvironmentId = environmentId,
        LastGate = new PipelineRunMigrationGateDto
        {
            Id = environmentId,
            RunId = runId,
            RunNumber = runId,
            StageName = "Deploy",
            StepName = "Migrate",
            Outcome = MigrationGateOutcome.Compatible,
            RecordedAt = DateTime.UtcNow
        },
        PendingContractions =
        [
            .. Enumerable.Range(1, pending).Select(index => new PendingContractionDto
            {
                Id = $"vault-{index}", Table = "VaultSecretVersions", Columns = [$"EncryptedValue{index}"], Step = SchemaContractionStep.Drop,
                ExpandMigration = "20260924_Expand", RequiredAfterMigration = "20261010_Unmap"
            })
        ]
    };

    private static EnvironmentSchemaDto WithoutGate(int environmentId) => new() { EnvironmentId = environmentId };

    private void ProjectHas(params EnvironmentDto[] environments) =>
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/environments?", environments);

    [Fact]
    public void GroupsTheEnvironmentsProductionFirst_ThenByName()
    {
        // Listed in an order that is neither alphabetical nor production first.
        ProjectHas(
            Environment(11, "Alpha dev", EnvironmentType.Development),
            Environment(12, "Zulu prod", EnvironmentType.Production),
            Environment(13, "Beta staging", EnvironmentType.Staging));
        _handler.SetJsonResponse("api/environments/11/schema-tracking", WithoutGate(11));
        _handler.SetJsonResponse("api/environments/12/schema-tracking", WithGate(12, 2247));
        _handler.SetJsonResponse("api/environments/13/schema-tracking", WithGate(13, 2251));

        var cut = Render<ProjectSchemaTracking>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='project-schema-environment']").Count));
        Assert.Equal(
            ["Zulu prod", "Alpha dev", "Beta staging"],
            cut.FindAll("[data-testid='project-schema-environment-name']").Select(name => name.TextContent.Trim()));
    }

    [Fact]
    public void OnlyTheProductionGroupIsOpen()
    {
        ProjectHas(Environment(11, "Dev", EnvironmentType.Development), Environment(12, "Prod", EnvironmentType.Production));
        _handler.SetJsonResponse("api/environments/11/schema-tracking", WithGate(11, 2251));
        _handler.SetJsonResponse("api/environments/12/schema-tracking", WithGate(12, 2247));

        var cut = Render<ProjectSchemaTracking>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='project-schema-environment']").Count));
        var groups = cut.FindAll("[data-testid='project-schema-environment']");
        // The OE collapsible is a native details inside the fieldset that carries the test id.
        Assert.True(groups[0].QuerySelector("details")!.HasAttribute("open"));
        Assert.False(groups[1].QuerySelector("details")!.HasAttribute("open"));
    }

    [Fact]
    public void EachGroupShowsTheTrackingOfItsOwnEnvironment_WithTheRunOfItsOwnVerdict()
    {
        ProjectHas(Environment(11, "Dev", EnvironmentType.Development), Environment(12, "Prod", EnvironmentType.Production));
        _handler.SetJsonResponse("api/environments/11/schema-tracking", WithGate(11, 2251));
        _handler.SetJsonResponse("api/environments/12/schema-tracking", WithGate(12, 2247));

        var cut = Render<ProjectSchemaTracking>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='environment-schema-run-link']").Count));
        Assert.Equal(
            ["/pipelines/runs/2247?tab=schema", "/pipelines/runs/2251?tab=schema"],
            cut.FindAll("[data-testid='environment-schema-run-link']").Select(link => link.GetAttribute("href")));
        Assert.Equal(2, cut.FindAll("[data-testid='schema-pending-contractions']").Count);
        // The environment is named once by its group: the block does not repeat its own title.
        Assert.DoesNotContain("EnvironmentSchemaTrackingTitle", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvironmentWithoutAGate_ShowsAnEmptyStateInsteadOfThePlanOfAnotherEnvironment()
    {
        ProjectHas(Environment(11, "Dev", EnvironmentType.Development), Environment(12, "Prod", EnvironmentType.Production));
        _handler.SetJsonResponse("api/environments/11/schema-tracking", WithoutGate(11));
        _handler.SetJsonResponse("api/environments/12/schema-tracking", WithGate(12, 2247));

        var cut = Render<ProjectSchemaTracking>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='environment-schema-tracking-empty']")));
        Assert.Single(cut.FindAll("[data-testid='schema-pending-contractions']"));
        Assert.Contains("SchemaTrackingNoVerdictBadge", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTilesCountTheContractionsAndTheEnvironmentsWithAGate()
    {
        ProjectHas(
            Environment(11, "Dev", EnvironmentType.Development),
            Environment(12, "Prod", EnvironmentType.Production),
            Environment(13, "Staging", EnvironmentType.Staging));
        _handler.SetJsonResponse("api/environments/11/schema-tracking", WithoutGate(11));
        _handler.SetJsonResponse("api/environments/12/schema-tracking", WithGate(12, 2247, pending: 3));
        _handler.SetJsonResponse("api/environments/13/schema-tracking", WithGate(13, 2251, pending: 3));

        var cut = Render<ProjectSchemaTracking>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='project-schema-pending-tile']")));
        // The environment without a gate owes nothing: the plan is not its own.
        Assert.Contains("3", cut.Find("[data-testid='project-schema-pending-tile']").TextContent, StringComparison.Ordinal);
        // The localizer double answers the format key itself; the figures are what the test can read.
        Assert.Contains("SchemaTrackingVerdictsValue", cut.Find("[data-testid='project-schema-verdicts-tile']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvironmentWhoseAnswerCannotBeRead_ShowsAWarningAndTheOthersStayReadable()
    {
        ProjectHas(Environment(11, "Dev", EnvironmentType.Development), Environment(12, "Prod", EnvironmentType.Production));
        _handler.SetResponse("api/environments/11/schema-tracking", System.Net.HttpStatusCode.InternalServerError);
        _handler.SetJsonResponse("api/environments/12/schema-tracking", WithGate(12, 2247));

        var cut = Render<ProjectSchemaTracking>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='project-schema-environment']").Count));
        Assert.Single(cut.FindAll("[data-testid='environment-schema-tracking']"));
        Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectWithoutEnvironment_ShowsAnEmptyState()
    {
        ProjectHas();

        var cut = Render<ProjectSchemaTracking>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='project-schema-tracking-empty']")));
        Assert.Empty(cut.FindAll("[data-testid='project-schema-environment']"));
    }
}
