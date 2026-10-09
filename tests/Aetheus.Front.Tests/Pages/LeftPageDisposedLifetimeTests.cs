// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Recette R2-100: "ObjectDisposedException - CancellationTokenSource_Disposed" reached the error boundary
/// when a page was left during a refresh. PipelinesList and PipelineRunLoadSession were fixed first; these
/// are the four remaining screens, each driven into the moment its disposed source used to be read again.
/// </summary>
public sealed class LeftPageDisposedLifetimeTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private CapturingHubConnectionFactory Hubs => (CapturingHubConnectionFactory)Services.GetRequiredService<HubConnectionFactory>();

    public LeftPageDisposedLifetimeTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<HubConnectionFactory>(sp => new CapturingHubConnectionFactory(sp));
    }

    [Fact]
    public async Task Home_LeftWhileTheDashboardLoads_OpensNoHubThatWouldReadTheDisposedLifetime()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/appmonitoring/summary", new AppMonitoringSummaryDto());
        BunitTestHelper.SetOnboardingWizardResponses(_handler);
        var dashboard = new TaskCompletionSource<DashboardOverviewDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "monitoring/dashboard", _ => dashboard.Task);
        var cut = Render<Home>();

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        var openedBeforeLeaving = Hubs.Created.Count;
        dashboard.SetResult(new DashboardOverviewDto { Servers = [], RecentRuns = [] });
        await WaitUntilAsync(() => _handler.Requests.Any(r => r.Url.Contains("appmonitoring/summary", StringComparison.Ordinal)));
        await Task.Delay(200, Xunit.TestContext.Current.CancellationToken);

        // Before the fix the resumed load opened the three hubs after the page was gone; their first
        // event read the disposed lifetime's Token and threw ObjectDisposedException.
        var openedAfterLeaving = Hubs.Created.Skip(openedBeforeLeaving).ToList();
        foreach (var (_, hub) in openedAfterLeaving.Where(created => created.Path == "servers"))
            await HubHandlerInvoker.InvokeAsync(hub, "ServerOffline", 1);
        Assert.Empty(openedAfterLeaving);
        // A second disposal (the renderer's, after the page's own) cancels nothing twice.
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ProjectQualitySection_ProjectEventAfterTheSectionWasLeft_DoesNotTouchTheDisposedDebounce()
    {
        _handler.SetJsonResponse("api/pipelines/projects/1/quality-trend", new ProjectQualityTrendDto());
        _handler.SetJsonResponse("api/analysis/projects/1/summary", new AnalysisProjectSummaryDto());
        var cut = Render<ProjectQualitySection>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => Hubs.Created.Any(created => created.Path == "entities"), TimeSpan.FromSeconds(3));
        var hub = Hubs.Hub("entities");
        // A first report arms the quiet-period debounce.
        await HubHandlerInvoker.InvokeAsync(hub, "EntityChanged", ResourceType.Project, 1, "Updated");

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());

        // Before the fix this cancelled the debounce source DisposeAsync had disposed: ObjectDisposedException.
        await HubHandlerInvoker.InvokeAsync(hub, "EntityChanged", ResourceType.Project, 1, "Updated");
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task MonacoEditor_ChangeSentWhileTheEditorIsBeingDisposed_DoesNotReadTheDisposedLifetime()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var published = new List<string>();
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, string.Empty)
            .Add(x => x.DebounceDelay, 50)
            .Add(x => x.DebouncedValueChanged, (string value) => published.Add(value)));

        await cut.Instance.DisposeAsync();
        // The JS editor is disposed last: a keystroke still reaches .NET in between. Before the fix it read
        // the disposed lifetime's Token to link the debounce source: ObjectDisposedException.
        await cut.Instance.OnYamlChanged("late");
        await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(published);
    }

    [Fact]
    public async Task PipelineRunLiveConnection_StoppedQueuePoll_DoesNotReadTheDisposedLifetime()
    {
        var logger = new WarningRecorder();
        // Run on the renderer's single-threaded context, as in the browser: the poll resumes only after
        // DisposeAsync has disposed the reload's lifetime, which is when it used to read it again.
        await Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var connection = new PipelineRunLiveConnection(Hubs, logger, 1, () => [],
                () => Task.CompletedTask, () => Task.CompletedTask, callback => callback(), () => true);
            await connection.StartAsync();
            await connection.DisposeAsync();
        });

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task DebouncedAsyncAction_LifetimeTokenReadAfterDisposal_IsCancelledInsteadOfThrowing()
    {
        var action = new DebouncedAsyncAction(() => Task.CompletedTask, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "[Test]");

        await action.DisposeAsync();

        Assert.True(action.LifetimeToken.IsCancellationRequested);
    }

    [Fact]
    public void PipelineRunLoadSession_AfterThePageIsLeft_NoLoadIsCurrentAnyMore()
    {
        var session = new PipelineRunLoadSession();
        var (generation, _) = session.Begin();

        session.Dispose();

        // The run page checks this before opening its live connection: a load resumed after the page was
        // left opened one that nothing disposed, whose queue poll then ran on a disposed page.
        Assert.False(session.IsCurrent(generation));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, Xunit.TestContext.Current.CancellationToken);
        Assert.True(condition(), "The awaited request was never sent.");
    }

    private sealed class WarningRecorder : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                lock (Warnings) Warnings.Add($"{formatter(state, exception)} {exception?.GetType().Name}");
        }
    }
}
