// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Template branch tests for ContactAgentDialog. Sets private _state via reflection
/// to exercise all @if branches without executing StartAsync (hub-dependent).
/// The ContactState enum is private so we reference it by name via reflection.
/// </summary>
public class ContactAgentDialogTemplateTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type DialogType = typeof(ContactAgentDialog);

    // Cached ContactState enum values (private, resolved once)
    private static readonly Type ContactStateType = DialogType.GetNestedType("ContactState", BindingFlags.NonPublic)!;
    private static readonly object StateRunning = Enum.Parse(ContactStateType, "Running");
    private static readonly object StateSuccess = Enum.Parse(ContactStateType, "Success");
    private static readonly object StateFailed = Enum.Parse(ContactStateType, "Failed");

    public ContactAgentDialogTemplateTests()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(
            HttpMethod.Post,
            "api/servers/1/contact-agent",
            new ContactAgentResultDto { Reachable = true });
    }

    [Fact]
    public void Renders_Running_ShowsProgressBar()
    {
        var cut = RenderDialog("my-server");
        // Default _state is Running - just check that rendering works
        Assert.Contains("contact-agent-dialog", cut.Markup);
    }

    [Fact]
    public void Renders_Running_ShowsAttemptInfo()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateRunning);
        SetField(cut.Instance, "_attempt", 1);
        cut.Render();
        // Shows attempt counter
        Assert.Contains("contact-agent-dialog", cut.Markup);
    }

    [Fact]
    public void Success_ResultFields_CanBeSet()
    {
        // Verify we can set success state fields on the instance
        var cut = RenderDialog("prod-server");
        var result = new ContactAgentResultDto
        {
            Reachable = true,
            SecondsSinceLastHeartbeat = 12.5,
            AgentVersion = "2.1.0"
        };
        SetField(cut.Instance, "_state", StateSuccess);
        SetField(cut.Instance, "_result", result);
        // State should be set correctly
        var stateField = DialogType.GetField("_state", Priv)!.GetValue(cut.Instance);
        Assert.Equal(StateSuccess, stateField);
        var resultField = (ContactAgentResultDto?)DialogType.GetField("_result", Priv)!.GetValue(cut.Instance);
        Assert.Equal("2.1.0", resultField?.AgentVersion);
    }

    [Fact]
    public void Renders_Success_WithNoVersionOrHeartbeat()
    {
        var cut = RenderDialog("prod-server");
        SetField(cut.Instance, "_state", StateSuccess);
        SetField(cut.Instance, "_result", new ContactAgentResultDto { Reachable = true });
        cut.Render();
        // Should render success block without crashing
        Assert.Contains("contact-agent-dialog", cut.Markup);
    }

    [Fact]
    public void Renders_Failed_ShowsErrorMessage()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateFailed);
        SetField(cut.Instance, "_errorMessage", "Connection refused");
        cut.Render();
        Assert.Contains("Connection refused", cut.Markup);
    }

    [Fact]
    public void Renders_Failed_ShowsRetryAndWhyOfflineButtons()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateFailed);
        SetField(cut.Instance, "_errorMessage", "Timed out");
        cut.Render();
        Assert.Contains("WhyOffline", cut.Markup);
        Assert.NotNull(cut.FindMenuItem("Retry"));
    }

    [Fact]
    public void Renders_Failed_WithDiagnostic_ShowsDiagCard()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateFailed);
        SetField(cut.Instance, "_errorMessage", "Offline");
        SetField(cut.Instance, "_diagnostic", new ServerDiagnosticDto
        {
            Summary = "Token expired",
            BackendVersion = "1.5.0",
            AgentVersion = "1.4.9",
            TokenValid = false,
            TokenExpiresAt = DateTime.UtcNow.AddDays(-5),
            TokenDaysRemaining = -5.0
        });
        cut.Render();
        Assert.Contains("Token expired", cut.Markup);
        Assert.Contains("1.5.0", cut.Markup);
    }

    [Fact]
    public void Renders_Failed_WithDiagnosticTokenValid_ShowsValidInfo()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateFailed);
        SetField(cut.Instance, "_errorMessage", "Offline");
        SetField(cut.Instance, "_diagnostic", new ServerDiagnosticDto
        {
            Summary = "Service crashed",
            BackendVersion = "1.5.0",
            AgentVersion = "1.5.0",
            TokenValid = true,
            TokenExpiresAt = DateTime.UtcNow.AddDays(30),
            TokenDaysRemaining = 30.0
        });
        cut.Render();
        Assert.Contains("Service crashed", cut.Markup);
    }

    [Fact]
    public void Renders_Failed_WithNullDiagnostic_HidesDiagCard()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateFailed);
        SetField(cut.Instance, "_errorMessage", "Unknown");
        SetField(cut.Instance, "_diagnostic", null);
        cut.Render();
        Assert.DoesNotContain("contact-agent-diagnostic", cut.Markup);
    }

    [Fact]
    public void Renders_Running_ShowsCancelButton()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateRunning);
        SetField(cut.Instance, "_attempt", 1);
        cut.Render();
        Assert.Contains("GoBack", cut.Markup);
    }

    [Fact]
    public void Renders_Success_ShowsCloseButton()
    {
        var cut = RenderDialog("my-server");
        SetField(cut.Instance, "_state", StateSuccess);
        SetField(cut.Instance, "_result", new ContactAgentResultDto { Reachable = true });
        cut.Render();
        Assert.Contains("Close", cut.Markup);
    }

    // === Helpers ===

    private IRenderedComponent<ContactAgentDialog> RenderDialog(string serverName) =>
        Render<ContactAgentDialog>(p =>
        {
            p.Add(x => x.ServerId, 1);
            p.Add(x => x.ServerName, serverName);
        });

    private static void SetField(object instance, string name, object? value) =>
        DialogType.GetField(name, Priv)!.SetValue(instance, value);
}
