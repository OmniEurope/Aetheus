// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary> Recette R-97: the deletion confirmation lists, per server and environment, what the project deployed
/// (read from the agents before anything is deleted), asks for an explicit acknowledgement when Docker volumes
/// (the project's data) are listed, removes them on the servers first, and shows resource by resource what was
/// removed and what failed, never deleting the project silently after a failure. </summary>
public sealed class ProjectDeleteDialogServerResourcesTests : BunitContext
{
    private const string Discovery = "api/projects/7/deletion/discovery";
    private const string Cleanup = "api/projects/7/deletion/cleanup";
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDeleteDialogServerResourcesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ProjectDeletionPreviewDto Preview(ProjectDeletionServerState state = ProjectDeletionServerState.Ready) => new()
    {
        ProjectId = 7,
        ProjectName = "Portfolio",
        Items = [new ProjectDeletionItemDto(ProjectDeletionItemKind.Pipelines, 4)],
        Servers =
        [
            new ProjectDeletionServerDto
            {
                ServerId = 3,
                ServerName = "vps-prod",
                State = state,
                Environments =
                [
                    new ProjectDeletionEnvironmentDto
                    {
                        Name = "prod", ComposeProject = "portfolio-prod", Hosts = ["app.portfolio.example"],
                        StateDirectory = "/var/lib/portfolio-production"
                    }
                ],
            }
        ],
    };

    private static ProjectServerResourceDto Resource(ProjectServerResourceKind kind, string name, string scope,
        ProjectServerResourceOutcome outcome = ProjectServerResourceOutcome.Found, string? reason = null) =>
        new(kind, name, scope, "prod", outcome, reason);

    private static ProjectServerResourceTaskDto Task(int id, ProjectServerTaskPhase phase, ProjectServerTaskState state,
        params ProjectServerResourceDto[] resources) => new()
        {
            TaskId = id,
            ServerId = 3,
            ServerName = "vps-prod",
            Phase = phase,
            State = state,
            Resources = [.. resources]
        };

    private static ProjectServerResourceTaskDto DiscoveryWith(params ProjectServerResourceDto[] resources) =>
        Task(41, ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded, resources);

    private static readonly ProjectServerResourceDto Container =
        Resource(ProjectServerResourceKind.Container, "portfolio-prod-front-1", "portfolio-prod");
    private static readonly ProjectServerResourceDto Volume =
        Resource(ProjectServerResourceKind.Volume, "portfolio-prod_db", "portfolio-prod");
    private static readonly ProjectServerResourceDto VirtualHost =
        Resource(ProjectServerResourceKind.VirtualHost, "app.portfolio.example.conf", "app.portfolio.example");

    private IRenderedComponent<ProjectDeleteDialog> RenderDialog() =>
        Render<ProjectDeleteDialog>(parameters => parameters.Add(dialog => dialog.ProjectId, 7));

    private static bool DeleteEnabled(IRenderedComponent<ProjectDeleteDialog> cut) =>
        !cut.Find("#project-delete-submit").HasAttribute("disabled");

    private bool Requested(string method, string url) =>
        _handler.RequestsSnapshot().Any(request => request.Method == method && request.Url.Contains(url, StringComparison.Ordinal));

    [Fact]
    public void Lists_WhatTheAgentFound_PerServerAndEnvironment()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container, Volume, VirtualHost) });

        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.Contains("portfolio-prod_db", cut.Find(".project-deletion-server").TextContent));
        var server = cut.Find(".project-deletion-server").TextContent;
        Assert.Contains("vps-prod", server);
        Assert.Contains("prod", server);
        Assert.Contains("portfolio-prod-front-1", server);
        Assert.Contains("app.portfolio.example.conf", server);
        Assert.Contains("Enum_ProjectServerResourceKind_Container", server);
        Assert.Contains("Enum_ProjectServerResourceKind_Volume", server);
        Assert.Contains("Enum_ProjectServerResourceKind_VirtualHost", server);
        Assert.True(Requested("POST", Discovery));
    }

    /// <summary>The rows of the two tables that end the deletion: what was deleted, then what was not.</summary>
    private static (IReadOnlyList<ProjectDeletionReportRow> Deleted, IReadOnlyList<ProjectDeletionReportRow> NotDeleted) Report(
        IRenderedComponent<ProjectDeleteDialog> cut)
    {
        var grids = cut.FindComponents<OmniDataGrid<ProjectDeletionReportRow>>();
        Assert.Equal(2, grids.Count);
        return ([.. grids[0].Instance.Items!], [.. grids[1].Instance.Items!]);
    }

    private void WaitForReport(IRenderedComponent<ProjectDeleteDialog> cut) =>
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<OmniDataGrid<ProjectDeletionReportRow>>().Count), TimeSpan.FromSeconds(5));

    [Fact]
    public void TheImages_AndTheBlueGreenUpstreamFile_HaveTheirOwnSections_WithTheirRemovalResults()
    {
        const string UpstreamPath = "/etc/apache2/sites-available/000-portfolio-upstream.conf";
        var image = Resource(ProjectServerResourceKind.Image, "portfolio-prod-back-blue:latest", "portfolio-prod");
        var upstream = Resource(ProjectServerResourceKind.UpstreamConfiguration, UpstreamPath, UpstreamPath);
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container, image, upstream) });
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Failed,
                Container with { Outcome = ProjectServerResourceOutcome.Removed },
                image with { Outcome = ProjectServerResourceOutcome.Removed },
                upstream with { Outcome = ProjectServerResourceOutcome.Failed, Reason = "the enabled site other.conf still uses its Define PORTFOLIO_FRONT_PORT" })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.Contains(UpstreamPath, cut.Find(".project-deletion-server").TextContent));
        var found = cut.Find(".project-deletion-server").TextContent;
        Assert.Contains("Enum_ProjectServerResourceKind_Image", found);
        Assert.Contains("portfolio-prod-back-blue:latest", found);
        Assert.Contains("Enum_ProjectServerResourceKind_UpstreamConfiguration", found);

        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        var (deleted, notDeleted) = Report(cut);
        Assert.Contains(new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_Image portfolio-prod-back-blue:latest", "vps-prod", "prod"), deleted);
        Assert.Contains(new ProjectDeletionReportRow($"Enum_ProjectServerResourceKind_UpstreamConfiguration {UpstreamPath}", "vps-prod",
            "the enabled site other.conf still uses its Define PORTFOLIO_FRONT_PORT"), notDeleted);
    }

    [Fact]
    public void ListedVolumes_KeepDeleteDisabled_UntilTheirDataLossIsAcknowledged()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container, Volume) });
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find("#project-delete-volumes-ack"));

        cut.Find("#project-delete-confirm").Input("Portfolio");
        Assert.False(DeleteEnabled(cut));

        cut.Find("#project-delete-volumes-ack").Change(true);
        Assert.True(DeleteEnabled(cut));
    }

    [Fact]
    public void WithoutVolumes_NoAcknowledgementIsAsked()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(VirtualHost) });
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.Contains("app.portfolio.example.conf", cut.Markup));

        cut.Find("#project-delete-confirm").Input("Portfolio");

        Assert.Empty(cut.FindAll("#project-delete-volumes-ack"));
        Assert.True(DeleteEnabled(cut));
    }

    [Fact]
    public void Delete_RemovesOnTheServersFirst_ThenDeletesTheProjectWithTheProvingTasks()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container, Volume) });
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Succeeded,
                Container with { Outcome = ProjectServerResourceOutcome.Removed },
                Volume with { Outcome = ProjectServerResourceOutcome.Removed })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find("#project-delete-volumes-ack"));
        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-volumes-ack").Change(true);

        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        var requests = _handler.RequestsSnapshot();
        var cleanup = requests.FindIndex(request => request.Method == "POST" && request.Url.Contains(Cleanup, StringComparison.Ordinal));
        var delete = requests.FindIndex(request => request.Method == "DELETE");
        Assert.True(cleanup >= 0 && delete > cleanup, "The servers are cleaned before the project is deleted.");
        Assert.Contains("serverTaskIds=41", requests[delete].Url, StringComparison.Ordinal);
        Assert.Contains("serverTaskIds=42", requests[delete].Url, StringComparison.Ordinal);
        Assert.DoesNotContain("force", requests[delete].Url, StringComparison.Ordinal);
        Assert.Contains(_handler.RequestDetails, request => request.Url.Contains(Cleanup, StringComparison.Ordinal)
            && request.Body is not null && request.Body.Contains("\"acknowledgeVolumeDataLoss\":true", StringComparison.Ordinal));
        var (deleted, notDeleted) = Report(cut);
        Assert.Contains(new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_Volume portfolio-prod_db", "vps-prod", "prod"), deleted);
        Assert.Empty(notDeleted);
    }

    [Fact]
    public void R51_APartialFailure_NeverStopsTheDeletion_TheProjectWaitsForItsCleanup_AndTheFailureIsListedWithItsReason()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container, Volume) });
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Failed,
                Container with { Outcome = ProjectServerResourceOutcome.Removed },
                Volume with { Outcome = ProjectServerResourceOutcome.Failed, Reason = "volume is in use" })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find("#project-delete-volumes-ack"));
        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-volumes-ack").Change(true);

        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        Assert.Single(_handler.RequestsSnapshot(), request => request.Method == "DELETE");
        Assert.Contains("ProjectDeletionPendingIntro", cut.Find(".project-deletion-outcome").TextContent);
        var (deleted, notDeleted) = Report(cut);
        Assert.Contains(new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_Container portfolio-prod-front-1", "vps-prod", "prod"), deleted);
        Assert.Contains(new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_Volume portfolio-prod_db", "vps-prod", "volume is in use"), notDeleted);
        Assert.Contains(new ProjectDeletionReportRow("ProjectDeletionAetheusData", "ProjectDeletionPlaceAetheus", "ProjectDeletionAetheusDataKept"), notDeleted);
        Assert.Empty(cut.FindAll("#project-delete-force-ack"));
    }

    [Fact]
    public void R51_ARefusedCleanup_StillEndsTheDeletion_AndSaysWhatWasFoundIsLeft()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container) });
        // A run of the project was still in progress: the backend refuses the cleanup (409).
        _handler.SetResponse(HttpMethod.Post, Cleanup, HttpStatusCode.Conflict);
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.Contains("portfolio-prod-front-1", cut.Find(".project-deletion-server").TextContent));
        cut.Find("#project-delete-confirm").Input("Portfolio");

        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        var (_, notDeleted) = Report(cut);
        Assert.Contains(new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_Container portfolio-prod-front-1", "vps-prod", "ProjectDeletionServerRequestFailed"), notDeleted);
        Assert.Contains("serverTaskIds=41", _handler.RequestsSnapshot().Single(request => request.Method == "DELETE").Url, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanupThatCannotBeReached_IsTreatedAsARefusal()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview());
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container) });
        _handler.SetAsyncJsonResponse<List<ProjectServerResourceTaskDto>>(HttpMethod.Post, Cleanup,
            _ => System.Threading.Tasks.Task.FromException<List<ProjectServerResourceTaskDto>>(new HttpRequestException("Test: server unreachable")));
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.Contains("portfolio-prod-front-1", cut.Find(".project-deletion-server").TextContent));
        cut.Find("#project-delete-confirm").Input("Portfolio");

        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        Assert.Contains(Report(cut).NotDeleted, row => row.Detail == "ProjectDeletionServerRequestFailed");
    }

    [Fact]
    public void R51_AServerWhoseAgentCannotRemoveVirtualHosts_IsSaid_AndWhatItKeepsIsListedAfterTheDeletion()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview() with
        {
            Servers = [Preview().Servers[0] with { MissingCapabilities = ["apache.manage"] }],
        });
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith(Container, VirtualHost) });
        _handler.SetJsonResponse(HttpMethod.Post, Cleanup, new List<ProjectServerResourceTaskDto>
        {
            Task(42, ProjectServerTaskPhase.Removal, ProjectServerTaskState.Succeeded, Container with { Outcome = ProjectServerResourceOutcome.Removed })
        });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.Contains("portfolio-prod-front-1", cut.Find(".project-deletion-server").TextContent));
        cut.Find("#project-delete-confirm").Input("Portfolio");

        Assert.Contains("ProjectDeletionServerMissingCapabilities", cut.Find(".project-deletion-server").TextContent);
        Assert.True(DeleteEnabled(cut));
        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        Assert.Contains(new ProjectDeletionReportRow("Enum_ProjectServerResourceKind_VirtualHost app.portfolio.example.conf", "vps-prod",
            "ProjectDeletionServerMissingCapabilities"), Report(cut).NotDeleted);
    }

    [Theory]
    [InlineData(ProjectDeletionServerState.Offline, "ProjectDeletionServerOffline")]
    [InlineData(ProjectDeletionServerState.NoWriteAccess, "ProjectDeletionServerNoWriteAccess")]
    public void R51_AServerThatCannotBeAsked_IsSaid_NeverAsked_AndTheDeletionGoesOn_LeavingTheProjectWaiting(
        ProjectDeletionServerState state, string reason)
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview(state));
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.Accepted);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => Assert.Contains(reason, cut.Find(".project-deletion-server").TextContent));

        cut.Find("#project-delete-confirm").Input("Portfolio");

        Assert.False(Requested("POST", Discovery));
        Assert.True(DeleteEnabled(cut));
        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        Assert.Contains(new ProjectDeletionReportRow("ProjectDeletionServerEverything", "vps-prod", reason), Report(cut).NotDeleted);
        Assert.False(Requested("POST", Cleanup));
    }

    [Fact]
    public void R49_RefusedLibraryValues_AreSaidInOneSentence_AndListedWithTheirReasonAtTheEnd()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview() with
        {
            RefusedValues = [new ProjectDeletionRefusedValueDto("prod", "COMPOSE_PROJECT", "aetheus-prod", ProjectDeletionRefusal.ReservedForControlPlane)],
        });
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith() });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();

        cut.WaitForAssertion(() => Assert.Equal("ProjectDeletionRefusedSentence", cut.Find(".project-deletion-refused").TextContent.Trim()));
        Assert.DoesNotContain("ProjectDeletionRefusedTitle", cut.Markup);
        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        Assert.Contains(new ProjectDeletionReportRow("COMPOSE_PROJECT = aetheus-prod", "prod", "Enum_ProjectDeletionRefusal_ReservedForControlPlane"), Report(cut).NotDeleted);
    }

    [Fact]
    public void R49_AnEnvironmentWithoutServer_IsNotMentioned()
    {
        _handler.SetJsonResponse("api/projects/7/deletion-preview", Preview() with
        {
            RefusedValues = [new ProjectDeletionRefusedValueDto("qa", "COMPOSE_PROJECT", "portfolio-qa", ProjectDeletionRefusal.NoServer)],
        });
        _handler.SetJsonResponse(HttpMethod.Post, Discovery, new List<ProjectServerResourceTaskDto> { DiscoveryWith() });
        _handler.SetResponse(HttpMethod.Delete, "api/projects/7", HttpStatusCode.NoContent);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find("#project-delete-confirm"));

        Assert.Empty(cut.FindAll(".project-deletion-refused"));
        Assert.DoesNotContain("portfolio-qa", cut.Markup);
        cut.Find("#project-delete-confirm").Input("Portfolio");
        cut.Find("#project-delete-submit").Click();

        WaitForReport(cut);
        Assert.DoesNotContain(Report(cut).NotDeleted, row => row.Item.Contains("portfolio-qa", StringComparison.Ordinal));
    }
}
