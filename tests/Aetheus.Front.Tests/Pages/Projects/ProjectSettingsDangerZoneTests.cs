// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Components.Projects.Sections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// Recette R3-71: the project's settings page keeps its one irreversible action, deleting the project, in
/// an OE danger zone at the end of the page, and that action still opens the project's own deletion
/// dialog (R2-122), not a plain yes or no.
/// </summary>
public sealed class ProjectSettingsDangerZoneTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ProjectSettingsDangerZoneTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    [Fact]
    public void TheDeletion_SitsInTheDangerZone_AndOpensTheProjectDeletionDialog()
    {
        _handler.SetJsonResponse("api/projects/7", new ProjectDetailDto { Id = 7, Name = "North", Tags = [] });
        _handler.SetPaginatedJsonResponse("api/releases", new List<ReleaseDto>());
        _handler.SetJsonResponse("api/projects/7/data-retention", new ProjectDataRetentionDto());
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());

        var cut = Render<Edit>(parameters => parameters.Add(page => page.Id, 7));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-settings-danger-zone button")));
        var delete = cut.Find(".omni-settings-danger-zone button");
        Assert.Contains("Delete", delete.TextContent, StringComparison.Ordinal);
        delete.Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(ProjectDeleteDialog), _dialog.LastComponent));
        // The dialog was dismissed (no result): the project stays and the page stays.
        Assert.DoesNotContain(_handler.Requests, request => request.Method == HttpMethod.Delete.Method);
    }
}
