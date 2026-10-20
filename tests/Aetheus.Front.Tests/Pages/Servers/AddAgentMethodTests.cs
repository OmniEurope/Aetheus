// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class AddAgentMethodTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AddAgentMethodTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/servers/agent-server-url",
            new { Url = "https://localhost:5301" });
    }

    [Fact]
    public void GetAllCommands_Linux_Returns3Commands()
    {
        _handler.SetJsonResponse("api/settings/agent-server-url", "");
        var cut = Render<AddAgent>();
        var commands = cut.Instance.GetAllCommands();
        Assert.Equal(3, commands.Count);
        Assert.Contains(commands, c => c.Contains("curl"));
        Assert.Contains(commands, c => c.Contains("tar"));
    }

    [Fact]
    public void GetAllCommands_Linux_OmitsTlsBypass_WhenSecure()
    {
        // F-002: default (no dev insecure mode) must keep full TLS validation - no -k, no --allow-insecure-certs.
        _handler.SetJsonResponse("api/settings/agent-server-url", "");
        var cut = Render<AddAgent>();
        var commands = cut.Instance.GetAllCommands();
        Assert.DoesNotContain(commands, c => c.Contains("-k "));
        Assert.DoesNotContain(commands, c => c.Contains("--allow-insecure-certs"));
    }

    [Fact]
    public void GetAllCommands_Linux_EmitsTlsBypass_WhenDevInsecure()
    {
        // F-002: dev insecure mode aligns with the wizard - -k on the download and --allow-insecure-certs on install.
        _handler.SetJsonResponse("api/settings/agent-server-url", "");
        var cut = Render<AddAgent>();
        typeof(AddAgent).GetField("_devModeInsecureTls", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, true);
        var commands = cut.Instance.GetAllCommands();
        Assert.Contains(commands, c => c.Contains("curl") && c.Contains("-k "));
        Assert.Contains(commands, c => c.Contains("--allow-insecure-certs"));
    }

    [Fact]
    public void GetAllCommands_Windows_Returns3Commands()
    {
        _handler.SetJsonResponse("api/settings/agent-server-url", "");
        var cut = Render<AddAgent>();
        typeof(AddAgent).GetField("_platform", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "windows");
        var commands = cut.Instance.GetAllCommands();
        Assert.Equal(3, commands.Count);
        Assert.Contains(commands, c => c.Contains("Invoke-WebRequest"));
        Assert.Contains(commands, c => c.Contains("install-agent-windows.ps1"));
    }

    [Fact]
    public void CanAdvance_Step0_RequiresPlatform()
    {
        _handler.SetJsonResponse("api/settings/agent-server-url", "");
        var cut = Render<AddAgent>();
        var m = typeof(AddAgent).GetMethod("CanAdvance", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Step 0: platform is set by default → true
        Assert.True((bool)m.Invoke(cut.Instance, null)!);
    }
}
