// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// SITE-FUNC-NAVIGATION-001 is instrumented, not fixed: bUnit does not reproduce the framework's
/// InvalidOperation_EnumFailedVersion (its renderer defers re-renders past the provider's enumeration).
/// These tests hold the instrumentation: every URL-driven tab change and every disposal of a tab set is
/// logged with the URL, so the order of events around the next occurrence can be read.
/// </summary>
public sealed class UrlSyncedTabsNavigationTests : BunitContext
{
    private readonly CapturingLogger _logger = new();

    public UrlSyncedTabsNavigationTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<ILogger<UrlSyncedTabs>>(_logger);
    }

    [Fact]
    public async Task BackAndForward_LogEachUrlDrivenTabChange_AndTheDisposal_WithTheUrl()
    {
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<UrlSyncedTabs>(p => p.Add(x => x.Slugs, ["overview", "history"]));

        nav.NavigateTo("http://localhost/pipelines/19?tab=history");
        cut.WaitForAssertion(() => Assert.Contains(_logger.Messages, m => m.Contains("-> 'history' (index 1)", StringComparison.Ordinal)));
        nav.NavigateTo("http://localhost/pipelines/19");
        cut.WaitForAssertion(() => Assert.Contains(_logger.Messages, m =>
            m.Contains("URL http://localhost/pipelines/19:", StringComparison.Ordinal)
            && m.Contains("'history' (index 1) -> '(null)' (index 0)", StringComparison.Ordinal)));

        await DisposeComponentsAsync();

        Assert.Contains(_logger.Messages, m =>
            m.Contains("[overview,history] disposed at URL http://localhost/pipelines/19", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger<UrlSyncedTabs>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
