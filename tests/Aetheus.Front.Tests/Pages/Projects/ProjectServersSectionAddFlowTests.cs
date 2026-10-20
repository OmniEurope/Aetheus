// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// The "Add" flow of a project's servers section: the agent server list is read once and handed to the
/// dialog, a dismissed dialog sends nothing, and a confirmed one creates the server (the agent server id
/// only for an agent server), reloads the list and the project, and confirms.
/// </summary>
public sealed class ProjectServersSectionAddFlowTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ProjectServersSectionAddFlowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/projects/1/servers", new List<ProjectServerDto>
        {
            new() { Id = 1, DisplayName = "Web Server", Host = "10.0.0.1", Port = 80, Type = ProjectServerType.ExternalHost }
        });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 10, Name = "agent-1", Hostname = "10.0.0.10" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects/1", new ProjectDetailDto { Id = 1, Name = "aetheus" });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/releases", Array.Empty<ReleaseDto>());
    }

    private IRenderedComponent<ProjectServersSection> RenderSection()
    {
        var cut = Render<ProjectServersSection>(parameters => parameters.Add(section => section.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Web Server", StringComparison.Ordinal));
        return cut;
    }

    private static Task ClickAddAsync(IRenderedComponent<ProjectServersSection> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Add").ClickAsync(new());

    private List<string?> CreateBodies() =>
        [.. _handler.RequestDetails
            .Where(request => request.Method == "POST" && request.Url.Contains("api/projects/1/servers", StringComparison.Ordinal))
            .Select(request => request.Body)];

    [Fact]
    public async Task DismissedDialog_HandsTheAgentServersToTheDialog_AndCreatesNothing()
    {
        var cut = RenderSection();
        _dialog.OpenResult = null;

        await ClickAddAsync(cut);

        Assert.Equal(typeof(ProjectServerDialog), _dialog.LastComponent);
        Assert.Equal("AddProjectServer", _dialog.LastTitle);
        var servers = Assert.IsType<List<ServerDto>>(Assert.Contains("AgentServers", _dialog.LastParameters!), exactMatch: false);
        Assert.Equal("agent-1", Assert.Single(servers).Name);
        Assert.Empty(CreateBodies());
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public async Task AgentServer_IsCreatedWithItsServerId_ThenTheListAndProjectReload()
    {
        var cut = RenderSection();
        _handler.SetJsonResponse(HttpMethod.Post, "api/projects/1/servers",
            new ProjectServerDto { Id = 5, DisplayName = "agent-1", Type = ProjectServerType.AgentServer });
        _dialog.OpenResult = new ProjectServerDialogResult
        {
            Type = ProjectServerType.AgentServer,
            ServerId = 10,
            DisplayName = "agent-1",
            Host = "10.0.0.10",
            Port = 22,
            Notes = "primary"
        };
        var listReadsBefore = ListReads();

        await ClickAddAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "Created"));
        var body = Sent();
        Assert.Equal((ProjectServerType.AgentServer, 10, "agent-1", 22, "primary"),
            (body.Type, body.ServerId, body.DisplayName, body.Port, body.Notes));
        Assert.True(ListReads() > listReadsBefore);
        Assert.Contains(_handler.Requests, request => request.Method == "GET" && request.Url.EndsWith("api/projects/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExternalHost_IsCreatedWithoutAServerId_EvenWhenTheDialogCarriesOne()
    {
        var cut = RenderSection();
        _handler.SetJsonResponse(HttpMethod.Post, "api/projects/1/servers",
            new ProjectServerDto { Id = 6, DisplayName = "cdn", Type = ProjectServerType.ExternalHost });
        _dialog.OpenResult = new ProjectServerDialogResult
        {
            Type = ProjectServerType.ExternalHost,
            ServerId = 10,
            DisplayName = "cdn",
            Host = "cdn.example.org"
        };

        await ClickAddAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "Created"));
        var body = Sent();
        Assert.Null(body.ServerId);
        Assert.Equal("cdn.example.org", body.Host);
    }

    [Fact]
    public async Task ACreateTheBackendAnswersWithNothing_ReloadsNothingAndConfirmsNothing()
    {
        var cut = RenderSection();
        _handler.SetRawResponse(HttpMethod.Post, "api/projects/1/servers", "null", "application/json");
        _dialog.OpenResult = new ProjectServerDialogResult { Type = ProjectServerType.ExternalHost, DisplayName = "x", Host = "h" };
        var listReadsBefore = ListReads();

        await ClickAddAsync(cut);

        cut.WaitForAssertion(() => Assert.Single(CreateBodies()));
        Assert.Equal(listReadsBefore, ListReads());
        Assert.Empty(Services.Toasts());
    }

    private CreateProjectServerRequest Sent() =>
        JsonSerializer.Deserialize<CreateProjectServerRequest>(Assert.Single(CreateBodies())!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private int ListReads() =>
        _handler.Requests.Count(request => request.Method == "GET" && request.Url.Contains("api/projects/1/servers", StringComparison.Ordinal));
}
