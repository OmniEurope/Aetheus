// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Port verdicts are relative to the requesting project, including listeners belonging to
/// its own declared deployment.</summary>
public class PortVerdictBadgeTests : BunitContext
{
    private IRenderedComponent<PortVerdictBadge> RenderFor(PortCheckEntryDto entry)
    {
        BunitTestHelper.RegisterServices(this);
        return Render<PortVerdictBadge>(parameters => parameters.Add(component => component.Entry, entry));
    }

    [Fact]
    public void APortTheAskingProjectDeclaredAndIsListeningOn_IsItsOwn()
    {
        var cut = RenderFor(new PortCheckEntryDto
        {
            Port = 10031,
            IsFree = false,
            OwnerLabel = "portfolio-prod-front",
            OwnerProjectId = 4,
            ContextProjectId = 4,
            Observation = PortObservationState.Listening,
            ObservedHolder = "portfolio-prod-front"
        });

        Assert.Contains("PortVerdictUsedByThisProject", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("omni-badge--success", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSamePortAskedAboutByAnotherProject_IsAnObstacle()
    {
        var cut = RenderFor(new PortCheckEntryDto
        {
            Port = 10031,
            IsFree = false,
            OwnerLabel = "portfolio-prod-front",
            OwnerProjectId = 4,
            ContextProjectId = 9,
            Observation = PortObservationState.Listening
        });

        Assert.Contains("PortVerdictTakenBy", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("omni-badge--danger", cut.Markup, StringComparison.Ordinal);
        // The holder has to be readable somewhere, and the badge stays one phrase.
        Assert.Contains("portfolio-prod-front", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void NobodyDeclaredItButSomethingAnswers_IsAmber()
    {
        var cut = RenderFor(new PortCheckEntryDto
        {
            Port = 10041,
            IsFree = true,
            ContextProjectId = 4,
            Observation = PortObservationState.Listening,
            ObservedHolder = "grafana"
        });

        Assert.Contains("PortVerdictListeningUndeclared", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("omni-badge--warning", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("grafana", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherDeclaredNorListening_IsFree()
    {
        var cut = RenderFor(new PortCheckEntryDto
        {
            Port = 10041,
            IsFree = true,
            ContextProjectId = 4,
            Observation = PortObservationState.NotListening
        });

        Assert.Contains("PortVerdictFree", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("omni-badge--success", cut.Markup, StringComparison.Ordinal);
    }
}
