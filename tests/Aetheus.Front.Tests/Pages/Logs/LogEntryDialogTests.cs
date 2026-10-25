// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Logs;

public class LogEntryDialogTests : BunitContext
{
    public LogEntryDialogTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void Renders_MessageAndException()
    {
        var cut = Render<LogEntryDialog>(p => p
            .Add(c => c.Message, "Something went boom")
            .Add(c => c.Exception, "System.NullReferenceException: ref"));

        Assert.Contains("Something went boom", cut.Markup);
        Assert.Contains("NullReferenceException", cut.Markup);
    }

    [Fact]
    public void Renders_MessageOnly_WithoutExceptionSection()
    {
        var cut = Render<LogEntryDialog>(p => p.Add(c => c.Message, "plain message"));

        Assert.Contains("plain message", cut.Markup);
        Assert.DoesNotContain("ExceptionDetails", cut.Markup);
    }

    /// <summary>R-41: a message carrying terminal colours shows them drawn, never the raw "[32m" codes, while
    /// the copy keeps the line as stored.</summary>
    [Fact]
    public void R41_AnAnsiColouredMessage_IsDrawn_WithoutItsRawCodes()
    {
        const string message = "\u001b[90m3:27PM\u001b[0m \u001b[32mINF\u001b[0m no leaks found";
        var cut = Render<LogEntryDialog>(p => p.Add(c => c.Message, message));

        Assert.Contains("INF", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("no leaks found", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[32m", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", cut.Markup, StringComparison.Ordinal);

        cut.FindAll(".dialog-footer button")[0].Click();
        Assert.Equal(message, JSInterop.Invocations["navigator.clipboard.writeText"].Single().Arguments[0]);
    }

    [Fact]
    public void Copy_WithException_WritesMessageAndExceptionToClipboard()
    {
        var cut = Render<LogEntryDialog>(p => p
            .Add(c => c.Message, "boom")
            .Add(c => c.Exception, "stack-trace-here"));

        // First button of the action row is "Copy" (each code block above has its own copy button).
        cut.FindAll(".dialog-footer button")[0].Click();

        // The clipboard JS interop was invoked with message + exception joined.
        JSInterop.VerifyInvoke("navigator.clipboard.writeText");
        var invocation = JSInterop.Invocations["navigator.clipboard.writeText"].Single();
        Assert.Equal("boom\n\nstack-trace-here", invocation.Arguments[0]);
    }

    [Fact]
    public void Copy_MessageOnly_WritesMessageToClipboard()
    {
        var cut = Render<LogEntryDialog>(p => p.Add(c => c.Message, "just the message"));

        cut.FindAll(".dialog-footer button")[0].Click();

        JSInterop.VerifyInvoke("navigator.clipboard.writeText");
        var invocation = JSInterop.Invocations["navigator.clipboard.writeText"].Single();
        Assert.Equal("just the message", invocation.Arguments[0]);
    }

    /// <summary>R-39: Copy stays the main action; Copy as Markdown is the other entry of its split button,
    /// so the footer keeps one main action (one main action per zone, the rest in a menu).</summary>
    private static string CopyAsMarkdown(IRenderedComponent<LogEntryDialog> cut, BunitJSInterop js)
    {
        var copy = cut.Find(".dialog-footer .log-entry-copy");
        Assert.Contains("omni-split-button--primary", copy.ClassList);
        cut.Find(".dialog-footer .log-entry-copy .omni-split-button__toggle").Click();
        var item = cut.WaitForElement(".log-entry-copy-markdown");
        Assert.Contains("CopyAsMarkdown", item.TextContent, StringComparison.Ordinal);
        item.Click();
        cut.WaitForAssertion(() => Assert.Single(js.Invocations["navigator.clipboard.writeText"]));
        return ((string)js.Invocations["navigator.clipboard.writeText"].Single().Arguments[0]!).Replace("\r\n", "\n");
    }

    [Fact]
    public void R39_CopyAsMarkdown_WritesMessageAndExceptionToClipboard_AsTwoFencedBlocks()
    {
        var cut = Render<LogEntryDialog>(p => p
            .Add(c => c.Message, "boom")
            .Add(c => c.Exception, "System.InvalidOperationException: stack-trace-here"));

        var markdown = CopyAsMarkdown(cut, JSInterop);

        Assert.Contains("```\nboom\n```", markdown, StringComparison.Ordinal);
        Assert.Contains("### ExceptionDetails\n\n```\nSystem.InvalidOperationException: stack-trace-here\n```", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void R39_CopyAsMarkdown_MessageOnly_HasNoExceptionSection()
    {
        var cut = Render<LogEntryDialog>(p => p.Add(c => c.Message, "just the message"));

        var markdown = CopyAsMarkdown(cut, JSInterop);

        Assert.Contains("just the message", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("ExceptionDetails", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_InvokesDialogClose()
    {
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));

        var cut = Render<LogEntryDialog>(p => p.Add(c => c.Message, "boom"));
        var spy = (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

        Assert.False(spy.Closed);
        // The action row is the Copy split button (main part, then its menu chevron), then Close, the last one.
        cut.FindAll(".dialog-footer button")[^1].Click();

        Assert.True(spy.Closed);
    }
}
