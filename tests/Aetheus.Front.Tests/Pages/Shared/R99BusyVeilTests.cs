// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Environments;
using Aetheus.Front.Components.Git;
using Aetheus.Front.Components.PackageFeeds;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// R-99: while a deletion, or an action started from a confirmation, runs, OE's veil lies over the zone it works
/// on and the user waits for the end. The page body takes it through <see cref="BusyVeil"/> (a page-level delete, a
/// grid row delete); a dialog that runs the action itself veils its own content. Each test holds the real HTTP
/// request open (a delayed handler response) and reads the markup meanwhile.
/// </summary>
public sealed class R99BusyVeilTests : BunitContext
{
    private const string BodyWrapper = "[data-testid='app-body-wrapper']";

    private readonly BunitTestHelper.TestHandler _handler;

    public R99BusyVeilTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    /// <summary>A server response that stays open until the test releases it (see <see cref="Settle"/>).</summary>
    private static TaskCompletionSource<object> Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Ends a test whatever its outcome: the held response is released and the click awaited, so a failing assertion never leaves a request open.</summary>
    private static async Task Settle(Task click, TaskCompletionSource<object> release)
    {
        release.TrySetResult(new object());
        await click;
    }

    private static Task ClickAsync(AngleSharp.Dom.IElement element) => element.ClickAsync(new MouseEventArgs());

    private static void AssertVeiled(IRenderedComponent<MainLayout> layout, bool expected)
    {
        var wrapper = layout.Find(BodyWrapper);
        Assert.Equal(expected, wrapper.ClassList.Contains("omni-busy"));
        Assert.Equal(expected ? "true" : "false", wrapper.GetAttribute("aria-busy"));
    }

    /// <summary>The page under test is hosted by the real layout, as the router does, so the veil is read where the user sees it.</summary>
    private IRenderedComponent<MainLayout> RenderInLayout<TPage>(params (string Name, object? Value)[] parameters)
        where TPage : IComponent =>
        Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(builder =>
        {
            builder.OpenComponent<TPage>(0);
            foreach (var (name, value) in parameters)
                builder.AddComponentParameter(1, name, value);
            builder.CloseComponent();
        })));

    // --- the shared mechanism -----------------------------------------------------------------------

    [Fact]
    public async Task BusyVeil_IsOnWhileTheActionRuns_ThenOff_AndTellsTheLayoutBeforeTheActionStarts()
    {
        var veil = new BusyVeil();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyWhenTold = new List<bool>();
        var busyAtStart = false;
        veil.Changed += () => busyWhenTold.Add(veil.IsBusy);

        var run = veil.RunAsync(() =>
        {
            busyAtStart = veil.IsBusy;
            return release.Task;
        });

        Assert.True(busyAtStart);
        Assert.True(veil.IsBusy);
        Assert.Equal([true], busyWhenTold);

        release.SetResult();
        await run;

        Assert.False(veil.IsBusy);
        Assert.Equal([true, false], busyWhenTold);
    }

    [Fact]
    public async Task BusyVeil_IsLiftedWhenTheActionThrows_AndKeptUntilTheLastOfSeveralActionsEnds()
    {
        var veil = new BusyVeil();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstRun = veil.RunAsync(() => first.Task);
        var secondRun = veil.RunAsync(() => second.Task);
        first.SetResult();
        await firstRun;
        Assert.True(veil.IsBusy);
        second.SetException(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondRun);
        Assert.False(veil.IsBusy);
    }

    [Fact]
    public async Task BusyVeil_IsLifted_WhenTheListenerThrowsAtTheStart_AndTheActionDoesNotRun()
    {
        var veil = new BusyVeil();
        var calls = 0;
        veil.Changed += () =>
        {
            if (calls++ == 0) throw new InvalidOperationException("listener failed");
        };
        var ran = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => veil.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }));

        Assert.False(ran);
        Assert.False(veil.IsBusy);
    }

    [Fact]
    public async Task Layout_VeilsThePageBody_WhileAnActionRuns()
    {
        var layout = Render<MainLayout>();
        var veil = Services.GetRequiredService<BusyVeil>();
        AssertVeiled(layout, expected: false);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = layout.InvokeAsync(() => veil.RunAsync(() => release.Task));

        layout.WaitForAssertion(() => AssertVeiled(layout, expected: true));
        release.SetResult();
        await run;
        layout.WaitForAssertion(() => AssertVeiled(layout, expected: false));
    }

    // --- a page-level delete -----------------------------------------------------------------------

    [Fact]
    public async Task PageLevelDelete_VeilsThePageBody_WhileTheDeleteRequestIsHeld_ThenNavigatesToTheList()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>());
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
        _handler.SetJsonResponse("api/environments/1", new EnvironmentDto
        {
            Id = 1,
            Name = "Prod",
            Type = EnvironmentType.Production,
            Servers = []
        });
        var release = Hold();
        _handler.SetAsyncJsonResponse(HttpMethod.Delete, "api/environments/1", _ => release.Task);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var dialog = Services.GetRequiredService<OmniDialogService>();

        var layout = RenderInLayout<EnvironmentEdit>((nameof(EnvironmentEdit.Id), 1));
        layout.WaitForAssertion(() => Assert.Contains("Prod", layout.Markup, StringComparison.Ordinal));
        AssertVeiled(layout, expected: false);

        var delete = layout.FindAll("button").First(button => button.TextContent.Trim() == "Delete");
        var task = ClickAsync(delete);
        try
        {
            await layout.InvokeAsync(() => dialog.Close(true));

            layout.WaitForAssertion(() => AssertVeiled(layout, expected: true));
            Assert.Contains("Prod", layout.Markup, StringComparison.Ordinal);
            Assert.False(nav.Uri.EndsWith("/environments", StringComparison.Ordinal));

            release.SetResult(new object());
            await task;
            Assert.EndsWith("/environments", nav.Uri);
            layout.WaitForAssertion(() => AssertVeiled(layout, expected: false));
        }
        finally
        {
            await Settle(task, release);
        }
    }

    [Fact]
    public async Task PageLevelDelete_LiftsTheVeil_WhenTheServerRefuses()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>());
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
        _handler.SetJsonResponse("api/environments/1", new EnvironmentDto
        {
            Id = 1,
            Name = "Prod",
            Type = EnvironmentType.Production,
            Servers = []
        });
        _handler.SetResponse(HttpMethod.Delete, "api/environments/1", System.Net.HttpStatusCode.InternalServerError);
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var layout = RenderInLayout<EnvironmentEdit>((nameof(EnvironmentEdit.Id), 1));
        layout.WaitForAssertion(() => Assert.Contains("Prod", layout.Markup, StringComparison.Ordinal));

        var task = ClickAsync(layout.FindAll("button").First(button => button.TextContent.Trim() == "Delete"));
        await layout.InvokeAsync(() => dialog.Close(true));
        await task;

        layout.WaitForAssertion(() => AssertVeiled(layout, expected: false));
        Assert.Contains("Prod", layout.Markup, StringComparison.Ordinal);
    }

    // --- a grid row delete -------------------------------------------------------------------------

    [Fact]
    public async Task GridRowDelete_VeilsThePageBody_WhileTheDeleteRequestIsHeld_ThenReloadsTheRows()
    {
        var rules = new List<AlertRuleDto>
        {
            new() { Id = 1, Name = "High CPU", Metric = MetricType.Cpu, Operator = ComparisonOperator.GreaterThan, Threshold = 90, SustainedSeconds = 60, IsEnabled = true, CreatedAt = DateTime.UtcNow }
        };
        _handler.SetJsonResponse("api/alerts", rules);
        var release = Hold();
        _handler.SetAsyncJsonResponse(HttpMethod.Delete, "api/alerts/1", _ => release.Task);
        var dialog = Services.GetRequiredService<OmniDialogService>();

        var layout = RenderInLayout<Alerts>();
        layout.WaitForAssertion(() => Assert.Contains("High CPU", layout.Markup, StringComparison.Ordinal));
        AssertVeiled(layout, expected: false);

        var delete = layout.FindAll("button").First(button => button.GetAttribute("title") == "Delete");
        var task = ClickAsync(delete);
        try
        {
            await layout.InvokeAsync(() => dialog.Close(true));

            layout.WaitForAssertion(() => AssertVeiled(layout, expected: true));
            Assert.Contains("High CPU", layout.Markup, StringComparison.Ordinal);
            Assert.Contains(_handler.Requests, request => request.Method == "DELETE" && request.Url.Contains("api/alerts/1", StringComparison.Ordinal));

            _handler.SetJsonResponse("api/alerts", new List<AlertRuleDto>());
            release.SetResult(new object());
            await task;
            layout.WaitForAssertion(() => AssertVeiled(layout, expected: false));
            Assert.DoesNotContain("High CPU", layout.Markup, StringComparison.Ordinal);
        }
        finally
        {
            await Settle(task, release);
        }
    }

    // --- a dialog that runs the deletion itself ----------------------------------------------------

    [Fact]
    public async Task DialogDelete_VeilsTheDialogContent_WhileTheDeleteRequestIsHeld_ThenLiftsIt()
    {
        var package = new PackageEntryDto { Id = 7, PackageFeedId = 3, Name = "Serilog" };
        _handler.SetJsonResponse("api/package-feeds/3", new PackageFeedDetailDto
        {
            Id = 3,
            Name = "corp-nuget",
            Packages = [package]
        });
        var release = Hold();
        _handler.SetAsyncJsonResponse(HttpMethod.Delete, "api/package-feeds/3/packages/7", _ => release.Task);
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var cut = Render<PackageFeedPackagesDialog>(p => p.Add(x => x.FeedId, 3));
        cut.WaitForAssertion(() => Assert.Contains("Serilog", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("false", cut.Find("[data-testid='package-feed-packages-zone']").GetAttribute("aria-busy"));

        var delete = cut.FindAll("button").First(button => button.GetAttribute("title") == "Delete");
        var task = ClickAsync(delete);
        try
        {
            await cut.InvokeAsync(() => dialog.Close(true));

            cut.WaitForAssertion(() =>
            {
                var zone = cut.Find("[data-testid='package-feed-packages-zone']");
                Assert.Contains("omni-busy", zone.ClassName, StringComparison.Ordinal);
                Assert.Equal("true", zone.GetAttribute("aria-busy"));
            });
            Assert.Contains("Serilog", cut.Markup, StringComparison.Ordinal);

            release.SetResult(new object());
            await task;
            cut.WaitForAssertion(() =>
            {
                var zone = cut.Find("[data-testid='package-feed-packages-zone']");
                Assert.DoesNotContain("omni-busy", zone.ClassName, StringComparison.Ordinal);
                Assert.Equal("false", zone.GetAttribute("aria-busy"));
            });
            Assert.DoesNotContain("Serilog", cut.Markup, StringComparison.Ordinal);
        }
        finally
        {
            await Settle(task, release);
        }
    }

    [Fact]
    public async Task ProjectDeleteDialog_VeilsItsContent_WhileTheDeleteRequestIsHeld_ThenLiftsIt()
    {
        _handler.SetJsonResponse("api/projects/9/deletion-preview", new ProjectDeletionPreviewDto
        {
            ProjectId = 9,
            ProjectName = "Alpha",
            ActiveRunCount = 0,
            Items = []
        });
        var release = Hold();
        _handler.SetAsyncJsonResponse(HttpMethod.Delete, "api/projects/9", _ => release.Task);
        var cut = Render<ProjectDeleteDialog>(p => p.Add(x => x.ProjectId, 9));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#project-delete-confirm")));
        Assert.Equal("false", cut.Find("[data-testid='project-delete-zone']").GetAttribute("aria-busy"));
        cut.Find("#project-delete-confirm").Input("Alpha");

        var task = ClickAsync(cut.Find("#project-delete-submit"));
        try
        {
            cut.WaitForAssertion(() =>
            {
                var zone = cut.Find("[data-testid='project-delete-zone']");
                Assert.Contains("omni-busy", zone.ClassName, StringComparison.Ordinal);
                Assert.Equal("true", zone.GetAttribute("aria-busy"));
            });
            Assert.Contains("Alpha", cut.Markup, StringComparison.Ordinal);

            release.SetResult(new object());
            await task;
            cut.WaitForAssertion(() =>
            {
                var zone = cut.Find("[data-testid='project-delete-zone']");
                Assert.DoesNotContain("omni-busy", zone.ClassName, StringComparison.Ordinal);
                Assert.Equal("false", zone.GetAttribute("aria-busy"));
            });
        }
        finally
        {
            await Settle(task, release);
        }
    }
}
