// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Environments;
using Aetheus.Front.Tests.Architecture;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests;

/// <summary>R-69 and R-70 (recette of 2026-10-25): the "start from an existing environment" card of the create page.</summary>
public class EnvironmentEditCopySourceTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public EnvironmentEditCopySourceTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static EnvironmentDto Environment(int id, string name, int? projectId, string? projectName) => new()
    {
        Id = id,
        Name = name,
        Type = EnvironmentType.Staging,
        ProjectId = projectId,
        ProjectName = projectName,
        Servers = []
    };

    private IRenderedComponent<EnvironmentEdit> RenderCreatePage(params EnvironmentDto[] environments)
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [new ProjectDto { Id = 1, Name = "P1" }], TotalCount = 1 });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>());
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto> { Items = [.. environments], TotalCount = environments.Length });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/environments/new");
        var cut = Render<EnvironmentEdit>();
        cut.WaitForAssertion(() => Assert.Contains("StartFromExistingEnvironment", cut.Markup));
        return cut;
    }

    [Fact]
    public void R69_TheCardTitle_IsACardHeading_NextToItsIcon_NotAPageTitle()
    {
        var cut = RenderCreatePage(Environment(1, "prod", 1, "Aetheus"));

        var card = cut.Find("[data-testid='environment-copy-source']");
        var title = card.QuerySelector("h5");
        Assert.NotNull(title);
        Assert.Equal("StartFromExistingEnvironment", title.TextContent.Trim());
        Assert.Null(card.QuerySelector("h1, h2, h3"));
        // The icon and the title are the two children of one centred line, so they share a baseline.
        var line = title.ParentElement!;
        Assert.NotNull(line.QuerySelector(":scope > .omni-icon, :scope > svg"));
    }

    [Fact]
    public void R69_TheCardHasNoGradient_OrTintedBorder()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        Assert.DoesNotContain(".environment-copy-source {", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".environment-copy-source__icon", css, StringComparison.Ordinal);
    }

    [Fact]
    public void R70_TheSourceOptions_AreGroupedByProject_AndSearchableUnderTheirProjectHeadings()
    {
        var cut = RenderCreatePage(
            Environment(1, "prod", 1, "Aetheus"),
            Environment(2, "staging", 1, "Aetheus"),
            Environment(3, "prod", 2, "W6"),
            Environment(4, "orphan", null, null));

        var source = cut.FindComponents<OmniDropDown<int?>>()
            .Single(dropdown => dropdown.Instance.Placeholder == "CreateEnvironmentFromScratch");

        Assert.Equal(["NoProject", "Aetheus", "Aetheus", "W6"], source.Instance.Options.Select(option => option.Group));
        // OE 1.9.2 renders the groups in the searchable list, so the selector keeps its search (R-70).
        Assert.True(source.Instance.Filterable);
        source.Find("input").Click();
        cut.WaitForAssertion(() =>
            Assert.Equal(["NoProject", "Aetheus", "W6"],
                source.FindAll(".omni-autocomplete__group-heading").Select(heading => heading.TextContent.Trim())));
    }

}
