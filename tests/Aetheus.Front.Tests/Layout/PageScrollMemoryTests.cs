// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Layout;

/// <summary> Every navigation is reported to the shell's scroll helper, before it (the page left
/// keeps its position) and after it (a return gets it back), in that order. </summary>
public sealed class PageScrollMemoryTests : BunitContext
{
    [Fact]
    public async Task ANavigation_IsReportedBeforeAndAfter()
    {
        var js = new RecordingJsRuntime();
        var nav = Services.GetRequiredService<NavigationManager>();
        using var memory = new PageScrollMemory(nav, js);

        nav.NavigateTo("/pipelines/5");
        await js.CallsAsync(2);
        nav.NavigateTo("/pipelines/runs/77");
        await js.CallsAsync(4);

        Assert.Equal(
            ["Aetheus.pageScroll.leaving", "Aetheus.pageScroll.arrived", "Aetheus.pageScroll.leaving", "Aetheus.pageScroll.arrived"],
            js.Calls);
    }

    [Fact]
    public void Disposed_ItReportsNothingMore()
    {
        var js = new RecordingJsRuntime();
        var nav = Services.GetRequiredService<NavigationManager>();
        new PageScrollMemory(nav, js).Dispose();

        nav.NavigateTo("/servers/9");

        // Without a location-changing handler left, the navigation completes within NavigateTo: once it
        // is done, nothing was reported, and nothing is left that could report later.
        Assert.EndsWith("/servers/9", nav.Uri, StringComparison.Ordinal);
        Assert.Empty(js.Calls);
    }

    /// <summary>Keeps the order of the calls, which bUnit's invocation record groups by identifier.</summary>
    private sealed class RecordingJsRuntime : IJSRuntime
    {
        private readonly StateSignal _called = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        public Task CallsAsync(int count) => _called.WhenAsync(() => Calls.Count >= count, $"{count} scroll call(s)");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Enqueue(identifier);
            _called.Pulse();
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
