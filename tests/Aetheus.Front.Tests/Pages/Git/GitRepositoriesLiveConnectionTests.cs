// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Git;

/// <summary>The repositories page degrades to a static list when its live connection fails, and a
/// connection still starting when the user leaves stops with the page instead of failing on the next
/// one (seen in the browser as the application error card on /logs, after /git-repositories).</summary>
public sealed class GitRepositoriesLiveConnectionTests : BunitContext
{
    private readonly HubScript _script = new();

    public GitRepositoriesLiveConnectionTests()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "MyProject", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
        handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
        Services.AddSingleton<HubConnectionFactory>(provider => new ControlledHubFactory(
            provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<AuthStateProvider>(), _script));
    }

    [Fact]
    public async Task ATransportFailureWhileOnThePage_LeavesTheListUsable()
    {
        _script.FailImmediately = true;

        var cut = RenderInsideAnErrorBoundary();

        await _script.WhenAsync(() => _script.StartAttempts > 0, "a start attempt");
        await cut.WhenRenderedAsync(() => Assert.Contains("SelectProject", cut.Markup));
        Assert.Empty(cut.FindAll(".boundary-error"));
    }

    [Fact]
    public async Task LeavingThePageWhileItsConnectionStarts_StopsTheConnectionWithoutAnError()
    {
        var cut = RenderInsideAnErrorBoundary();
        await _script.WhenAsync(() => _script.StartAttempts > 0, "a start attempt");
        var page = cut.FindComponent<GitRepositories>().Instance;

        var dispose = cut.InvokeAsync(async () => await page.DisposeAsync());
        await HangGuard.AwaitAsync(dispose, "the page disposing while its connection starts");

        await dispose;
        await _script.WhenAsync(() => _script.CancelledStarts > 0, "the start cancelled");
        Assert.Empty(cut.FindAll(".boundary-error"));
    }

    private IRenderedComponent<ErrorBoundary> RenderInsideAnErrorBoundary() =>
        Render<ErrorBoundary>(parameters => parameters
            .Add(boundary => boundary.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<GitRepositories>(0);
                builder.CloseComponent();
            }))
            .Add(boundary => boundary.ErrorContent, (RenderFragment<Exception>)(exception => builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "boundary-error");
                builder.AddContent(2, exception.ToString());
                builder.CloseElement();
            })));

    /// <summary>What the live connection's negotiation does, and what it saw.</summary>
    private sealed class HubScript
    {
        private int _startAttempts;
        private int _cancelledStarts;
        private readonly StateSignal _changed = new();

        public Task WhenAsync(Func<bool> condition, string what) => _changed.WhenAsync(condition, what);

        public bool FailImmediately { get; set; }
        public int StartAttempts => Volatile.Read(ref _startAttempts);
        public int CancelledStarts => Volatile.Read(ref _cancelledStarts);

        /// <summary>Fails the way the browser reported it: every transport aborted, surfaced by SignalR
        /// as an AggregateException, either at once or only once the start is cancelled.</summary>
        public async Task<HttpResponseMessage> NegotiateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _startAttempts);
            _changed.Pulse();
            if (!FailImmediately)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelledStarts);
                    _changed.Pulse();
                }
            }
            throw new AggregateException(
                "Unable to connect to the server with any of the available transports.",
                new OperationCanceledException("The operation was cancelled."));
        }
    }

    private sealed class ControlledHubFactory(IConfiguration config, AuthStateProvider auth, HubScript script)
        : HubConnectionFactory(config, auth, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthDelegatingHandler>.Instance)
    {
        public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null) =>
            new HubConnectionBuilder()
                .WithUrl($"http://127.0.0.1:1/hubs/{hubPath}", options =>
                    options.HttpMessageHandlerFactory = _ => new NegotiationHandler(script))
                .Build();

        private sealed class NegotiationHandler(HubScript script) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                script.NegotiateAsync(ct);
        }
    }
}
