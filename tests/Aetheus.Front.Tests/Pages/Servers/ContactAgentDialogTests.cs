// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ContactAgentDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ContactAgentDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_WithParameters()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "TestServer"));

        // On init the dialog probes the agent for the given ServerId via the contact endpoint.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers/1/contact"));
    }

    /// <summary>Recette R-116: while the attempts run, OE's busy veil lies over their lines; no loader is added.</summary>
    [Fact]
    public void WhileTheAttemptsRun_TheirLinesAreVeiled_AndNoLoaderIsAdded()
    {
        var contact = new TaskCompletionSource<ContactAgentResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/servers/1/contact", _ => contact.Task);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/servers/1/contact", _ => contact.Task);

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "TestServer"));

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=contact-agent-running]").GetAttribute("aria-busy")));
        Assert.Contains("omni-busy", cut.Find("[data-testid=contact-agent-running]").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));

        contact.SetResult(new ContactAgentResultDto { Reachable = true });
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=contact-agent-running]")));
    }

    [Fact]
    public void FormatSeconds_UnderMinute()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "Srv"));

        var method = typeof(ContactAgentDialog).GetMethod("FormatSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(cut.Instance, [30.0])!;

        Assert.Contains("ContactAgentSecondsAgo", result);
    }

    [Fact]
    public void FormatSeconds_Minutes()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "Srv"));

        var method = typeof(ContactAgentDialog).GetMethod("FormatSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(cut.Instance, [120.0])!;

        Assert.Contains("ContactAgentMinutesAgo", result);
    }

    [Fact]
    public void FormatSeconds_Hours()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "Srv"));

        var method = typeof(ContactAgentDialog).GetMethod("FormatSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(cut.Instance, [7200.0])!;

        Assert.Contains("ContactAgentHoursAgo", result);
    }

    [Fact]
    public void ResolveError_Null_ReturnsRequestFailed()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "Srv"));

        var method = typeof(ContactAgentDialog).GetMethod("ResolveError", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(cut.Instance, [null])!;

        Assert.Equal("ContactAgentRequestFailed", result);
    }

    [Fact]
    public void ResolveError_NoErrorString_ReturnsUnreachable()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "Srv"));

        var method = typeof(ContactAgentDialog).GetMethod("ResolveError", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(cut.Instance, [new ContactAgentResultDto { Reachable = false }])!;

        Assert.Equal("ContactAgentUnreachable", result);
    }

    [Fact]
    public void ResolveError_WithErrorString_ReturnsError()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "Srv"));

        var method = typeof(ContactAgentDialog).GetMethod("ResolveError", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(cut.Instance, [new ContactAgentResultDto { Reachable = false, Error = "Connection refused" }])!;

        Assert.Equal("Connection refused", result);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        _handler.SetJsonResponse("api/servers/1/contact", new ContactAgentResultDto { Reachable = true });

        var cut = Render<ContactAgentDialog>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.ServerName, "Srv"));

        await ((IAsyncDisposable)cut.Instance).DisposeAsync();
        // Disposal is idempotent: a second DisposeAsync (cts already cancelled, hub torn down)
        // must not throw.
        var ex = await Record.ExceptionAsync(async () => await ((IAsyncDisposable)cut.Instance).DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public void MaxAttempts_IsTwo()
    {
        // With Type=exec on the VPS the heartbeat is steady (every 30 s) so
        // attempt 1 succeeds ~always; 2 is the right margin for a rare flap.
        // Locking it down here so it doesn't silently creep back to 3.
        var field = typeof(ContactAgentDialog).GetField("MaxAttempts", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(2, (int)field.GetValue(null)!);
    }

    // The Success-path auto-close timer was removed (user feedback: prefer an explicit Close
    // button) - see ContactAgentDialog.razor / handoff 2026-05-26.
}
