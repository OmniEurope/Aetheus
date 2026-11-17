// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>F-FRONT-025: the text a site's configuration read produces reaches the editor that asked for it, and only
/// that one. The answer arrives over SignalR and is read from the task's log, so it can land after the reader closed the
/// editor, after the read was refused, or after another site's editor opened; it must then fill nothing, because Save
/// writes whatever the editor holds under the site the editor was opened for.</summary>
public sealed class ServerApacheSectionStaleConfigTests : BunitContext
{
    private const int TaskId = 81001;
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly HeldDialogService _dialog;

    public ServerApacheSectionStaleConfigTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddScoped<OmniDialogService>(services => new HeldDialogService(
            services.GetRequiredService<AppDialogs>(), services.GetRequiredService<OmniOverlayService>()));
        _dialog = (HeldDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    /// <summary>A dialog that stays open until the test closes it, as the real one does while the reader edits.</summary>
    private sealed class HeldDialogService(AppDialogs dialogs, OmniOverlayService overlay) : OmniDialogService(dialogs, overlay)
    {
        private TaskCompletionSource<object?> _open = new();
        public List<ApacheConfigEditorModel> Models { get; } = [];

        public override Task<object?> OpenAsync<T>(string title, Dictionary<string, object?>? parameters = null, OmniDialogOptions? options = null)
        {
            Models.Add((ApacheConfigEditorModel)parameters!["Model"]!);
            _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _open.Task;
        }

        public override void Close(object? result = null) => _open.TrySetResult(result);
    }

    private static ApacheVirtualHostDto Site(string name) => new()
    {
        ServerName = name + ".example",
        Port = 80,
        IsEnabled = true,
        DocumentRoot = "/var/www/" + name,
        ConfigFile = $"/etc/apache2/sites-available/{name}.conf"
    };

    private IRenderedComponent<ServerApacheSection> RenderSection()
    {
        _handler.SetResponse(HttpMethod.Get, "api/servers/30/apache/vhosts/a.conf/config", HttpStatusCode.Accepted);
        _handler.SetResponse(HttpMethod.Get, "api/servers/30/apache/vhosts/b.conf/config", HttpStatusCode.Accepted);
        var cut = Render<ServerApacheSection>(parameters => parameters
            .Add(section => section.Server, new ServerDetailDto
            {
                Id = 30,
                Name = "apache-srv",
                Hostname = "10.0.0.3",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
                Apache = new ApacheDataDto { Modules = [], VirtualHosts = [Site("a"), Site("b")] }
            })
            .Add(section => section.ServerId, 30));
        // The 30 s answer timeout must not overtake a held answer when the whole suite loads the runner.
        cut.Instance.ConfigReadTimeout = Timeout.InfiniteTimeSpan;
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button[title='EditConfig']").Count));
        return cut;
    }

    /// <summary>Opens the editor of the n-th listed site (0 is a.conf, 1 is b.conf) and returns its model; the editor
    /// stays open, waiting for the agent's answer.</summary>
    private async Task<ApacheConfigEditorModel> OpenEditorAsync(IRenderedComponent<ServerApacheSection> cut, int site)
    {
        var opened = _dialog.Models.Count;
        _ = cut.FindAll("button[title='EditConfig']")[site].ClickAsync(new());
        await cut.WaitForStateAsync(() => _dialog.Models.Count > opened);
        return _dialog.Models[^1];
    }

    private static TaskCompletedNotification ReadOf(string site) => new()
    {
        TaskId = TaskId,
        ServerId = 30,
        TaskName = $"Apache config - {site}",
        Status = TaskExecutionStatus.Success
    };

    /// <summary>The task's log is read behind a gate, so the answer can be released after the reader moved on.</summary>
    private TaskCompletionSource<List<TaskLogDto>> HoldTheLog()
    {
        var gate = new TaskCompletionSource<List<TaskLogDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, $"api/logs/task/{TaskId}", _ => gate.Task);
        return gate;
    }

    private static List<TaskLogDto> Log(string text) => [new() { Id = 1, TaskId = TaskId, Message = text }];

    [Fact]
    public async Task ASlowReadOfSiteA_AnsweringAfterTheEditorOfSiteBOpened_FillsNothingInB()
    {
        var cut = RenderSection();
        var editorA = await OpenEditorAsync(cut, 0);
        var gate = HoldTheLog();
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(ReadOf("a.conf")));
        await _handler.WhenGetAsync($"api/logs/task/{TaskId}");

        await cut.InvokeAsync(() => _dialog.Close(null));
        var editorB = await OpenEditorAsync(cut, 1);
        gate.SetResult(Log("ServerName a.example"));
        await Task.Delay(200, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("b.conf", editorB.SiteName);
        Assert.Equal(string.Empty, editorB.Content);
        Assert.True(editorB.Loading);
        Assert.Equal(string.Empty, editorA.Content);
    }

    [Fact]
    public async Task AReadOfSiteA_ArrivingWhileTheEditorOfSiteBWaits_IsNotShownInB()
    {
        var cut = RenderSection();
        var editorB = await OpenEditorAsync(cut, 1);
        _handler.SetJsonResponse(HttpMethod.Get, $"api/logs/task/{TaskId}", Log("ServerName a.example"));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(ReadOf("a.conf")));
        await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, editorB.Content);
        Assert.True(editorB.Loading);
    }

    [Fact]
    public async Task ASlowRead_AnsweringAfterTheEditorWasClosed_FillsNothing()
    {
        var cut = RenderSection();
        var editor = await OpenEditorAsync(cut, 0);
        var gate = HoldTheLog();
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(ReadOf("a.conf")));
        await _handler.WhenGetAsync($"api/logs/task/{TaskId}");

        await cut.InvokeAsync(() => _dialog.Close(null));
        gate.SetResult(Log("ServerName a.example"));
        await Task.Delay(200, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, editor.Content);
        Assert.True(editor.Loading);
    }

    [Fact]
    public async Task AnAnswerForAnEditorThatStoppedWaiting_FillsNothing()
    {
        var cut = RenderSection();
        _handler.SetResponse(HttpMethod.Get, "api/servers/30/apache/vhosts/a.conf/config", HttpStatusCode.Conflict);
        var editor = await OpenEditorAsync(cut, 0);
        cut.WaitForAssertion(() => Assert.False(editor.Loading));
        _handler.SetJsonResponse(HttpMethod.Get, $"api/logs/task/{TaskId}", Log("ServerName a.example"));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(ReadOf("a.conf")));
        await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, editor.Content);
    }

    [Fact]
    public async Task TheReadOfTheEditorsOwnSite_StillFillsIt()
    {
        var cut = RenderSection();
        var editor = await OpenEditorAsync(cut, 0);
        var gate = HoldTheLog();
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(ReadOf("a.conf")));
        await _handler.WhenGetAsync($"api/logs/task/{TaskId}");

        // The answer fills the shared model and raises OnChanged without re-rendering the section, so a render-driven
        // wait would check only once and miss an answer that lands after it on a loaded runner: wait for the event.
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.OnChanged += () => answered.TrySetResult();
        gate.SetResult(Log("ServerName a.example"));
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("ServerName a.example", editor.Content);
        Assert.False(editor.Loading);
    }
}
