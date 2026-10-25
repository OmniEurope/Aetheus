// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// The add-agent wizard driven the way a user drives it: platform cards, the wizard's Previous /
/// Next / Finish / Cancel buttons, and what each step renders. Every outcome is read from the markup,
/// the navigation manager, the toasts and the requests the faked API received, never from a private
/// member of the page (RFL-1).
/// </summary>
public class AddAgentTests : BunitContext
{
    private const string ForwardButton = "button.omni-wizard__next";
    private const string PreviousButton = "button.omni-wizard__previous";
    private const string FinishButton = "button.omni-wizard__finish";
    private const string CancelButton = "button.omni-wizard__cancel";
    private const string SelectedStep = ".omni-steps__item--selected";

    private readonly BunitTestHelper.TestHandler _handler;

    public AddAgentTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/servers/agent-server-url",
            new { Url = "https://localhost:5301" });
        JSInterop.Mode = JSRuntimeMode.Loose;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiBaseUrl"] = "https://localhost:5301"
            })
            .Build();
        Services.AddSingleton<IConfiguration>(config);
    }

    private void IssueTokens(string token = "generated-token-abc", int id = 9) =>
        _handler.SetJsonResponse(HttpMethod.Post, "api/auth/registration-tokens",
            new RegistrationTokenDto { Id = id, Token = token, ExpiresAt = DateTime.UtcNow.AddHours(24) });

    private static Task PressAsync(IRenderedComponent<AddAgent> cut, string selector) =>
        cut.Find(selector).ClickAsync(new MouseEventArgs());

    /// <summary>Presses Next until the wizard shows the given step (0 platform, 1 capabilities,
    /// 2 install, 3 verify).</summary>
    private static async Task GoToStepAsync(IRenderedComponent<AddAgent> cut, int step)
    {
        for (var current = 0; current < step; current++)
            await PressAsync(cut, ForwardButton);
    }

    private static int CurrentStep(IRenderedComponent<AddAgent> cut) =>
        cut.FindAll(".omni-steps__item").ToList().FindIndex(item => item.ClassList.Contains("omni-steps__item--selected"));

    private static void ClickPlatformCard(IRenderedComponent<AddAgent> cut, string label)
    {
        var card = cut.FindComponents<OmniEurope.Blazor.Components.OmniSelectableCard>()
            .Single(component => component.Instance.Title == label);
        cut.InvokeAsync(() => card.Instance.ValueChanged.InvokeAsync(true)).GetAwaiter().GetResult();
    }

    private int TokenRequests() => _handler.Requests.Count(request =>
        request.Method == "POST" && request.Url.EndsWith("api/auth/registration-tokens", StringComparison.Ordinal));

    [Fact]
    public void Renders_WizardDialog()
    {
        var cut = Render<AddAgent>();
        // The wizard opens on the platform-selection step.
        Assert.Contains("omni-selectable-card", cut.Markup);
    }

    [Fact]
    public void Renders_PlatformStep_WithLinuxAndWindows()
    {
        var cut = Render<AddAgent>();
        var markup = cut.Markup;
        // Both platform cards render with their brand SVG icons.
        Assert.Contains("omni-selectable-card", markup);
        Assert.Contains("omni-selectable-card__icon", markup);
        Assert.Contains("<svg", markup);
    }

    [Fact]
    public void Renders_PlatformOptions()
    {
        var cut = Render<AddAgent>();
        var markup = cut.Markup;
        Assert.Contains("omni-selectable-card", markup);
    }

    [Fact]
    public void LinuxPlatform_IsSelectedByDefault()
    {
        var cut = Render<AddAgent>();
        Assert.Contains("omni-selectable-card--selected", cut.Markup);
    }

    [Fact]
    public void Renders_WithConfig()
    {
        var cut = Render<AddAgent>();
        // With the API base URL configured, the wizard still renders its platform step.
        Assert.Contains("omni-selectable-card", cut.Markup);
    }

    // --- Can the user go on ---

    [Fact]
    public void Step0_NextIsEnabledBecausePlatformIsSelected()
    {
        var cut = Render<AddAgent>();
        Assert.False(cut.Find(ForwardButton).HasAttribute("disabled")); // linux is selected by default
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StepsAfterThePlatform_CanAlwaysGoOn_WithOrWithoutAToken(int step)
    {
        // Step 1 has issued no token yet, step 2 has (a token check is not a condition to advance).
        IssueTokens();
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, step);

        Assert.Equal(step, CurrentStep(cut));
        Assert.False(cut.Find(ForwardButton).HasAttribute("disabled"));
    }

    [Fact]
    public async Task VerifyStep_CanFinish()
    {
        IssueTokens();
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 3);

        Assert.Equal(3, CurrentStep(cut));
        Assert.False(cut.Find(FinishButton).HasAttribute("disabled"));
    }

    // --- Steps ---

    [Fact]
    public async Task Wizard_HasFourSteps_AndKeepsThemAfterTheUserActs()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        Assert.Equal(4, cut.FindAll(".omni-steps__item").Count);
        Assert.Contains("WizardStepPlatform", cut.Find(SelectedStep).TextContent);

        ClickPlatformCard(cut, "Windows");
        await GoToStepAsync(cut, 1);

        Assert.Equal(4, cut.FindAll(".omni-steps__item").Count);
    }

    // --- Platform selection ---

    [Fact]
    public void PlatformSelection_DefaultIsLinux()
    {
        var cut = Render<AddAgent>();
        var selected = cut.FindComponents<OmniEurope.Blazor.Components.OmniSelectableCard>()
            .Where(card => card.Instance.Value).ToList();
        Assert.Equal("Linux", Assert.Single(selected).Instance.Title);
        Assert.Contains(cut.Instance.GetAllCommands(), command => command.Contains("install-agent-linux.sh"));
    }

    [Fact]
    public void SwitchToWindows_UpdatesPlatform()
    {
        // FRONTTEST-002: the choice is made the way a user makes it, by clicking the Windows card,
        // and its effect is read from what the wizard produces, not from the field the test wrote.
        var cut = Render<AddAgent>();

        ClickPlatformCard(cut, "Windows");

        var commands = cut.Instance.GetAllCommands();
        Assert.Contains(commands, command => command.Contains("install-agent-windows.ps1"));
        Assert.DoesNotContain(commands, command => command.Contains("install-agent-linux.sh"));
    }

    [Fact]
    public void ChoosingAPlatform_MovesOnToTheCapabilitiesStep()
    {
        var cut = Render<AddAgent>();

        ClickPlatformCard(cut, "Windows");

        Assert.Equal(1, CurrentStep(cut));
    }

    // --- Token generation ---

    [Fact]
    public async Task GenerateTokenAsync_SetsTokenOnSuccess()
    {
        IssueTokens("generated-token-abc");
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 2);

        Assert.Equal(1, TokenRequests());
        Assert.NotEmpty(cut.FindAll(".wizard-token-code"));
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Success && toast.Detail == "WizardTokenGenerated");
    }

    [Fact]
    public async Task GenerateTokenAsync_SetsGeneratingFlagDuringExecution()
    {
        // FRONTTEST-002: the answer is held back. While the request is pending the wizard must not ask
        // for a second token (the generating flag), whatever the user does; once it is answered the
        // token is shown and still only one request was made.
        var answer = new TaskCompletionSource<RegistrationTokenDto>();
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/auth/registration-tokens", _ => answer.Task);
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 1);

        var generation = PressAsync(cut, ForwardButton);
        Assert.Equal(1, TokenRequests());
        Assert.Empty(cut.FindAll(".wizard-token-code"));

        await PressAsync(cut, PreviousButton);
        await PressAsync(cut, ForwardButton).WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, Xunit.TestContext.Current.CancellationToken); // a second request would wait for the held answer and time out
        Assert.Equal(1, TokenRequests());

        answer.SetResult(new RegistrationTokenDto { Token = "test-token", ExpiresAt = DateTime.UtcNow.AddHours(24) });
        await generation;
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".wizard-token-code")));
        Assert.Equal(1, TokenRequests());
    }

    [Fact]
    public async Task GenerateTokenAsync_AsksForTheDefaultExpirationOfTwentyFourHours()
    {
        IssueTokens();
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 2);

        var post = Assert.Single(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Url.EndsWith("api/auth/registration-tokens", StringComparison.Ordinal));
        Assert.Contains("24", post.Body);
    }

    // --- Install commands ---

    [Fact]
    public void GetAllCommands_Linux_ReturnsExpectedCount()
    {
        var cut = Render<AddAgent>();
        ClickPlatformCard(cut, "Linux");
        var result = cut.Instance.GetAllCommands();
        Assert.Equal(3, result.Count); // download + extract + install
        Assert.Contains("curl", result[0]);
        Assert.Contains("tar", result[1]);
        Assert.Contains("install-agent-linux.sh", result[2]);
    }

    [Fact]
    public void GetAllCommands_Windows_ReturnsExpectedCount()
    {
        var cut = Render<AddAgent>();
        ClickPlatformCard(cut, "Windows");
        var result = cut.Instance.GetAllCommands();
        Assert.Equal(3, result.Count); // download + extract to temp + install script
        Assert.Contains("Invoke-WebRequest", result[0]);
        Assert.Contains("Expand-Archive", result[1]);
        Assert.Contains("install-agent-windows.ps1", result[2]);
    }

    // --- Server base URL ---

    [Fact]
    public void ServerBaseUrl_UsesTheUrlTheServerAdvertises()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/agent-server-url", new { Url = "https://deploy.example.test/" });
        var cut = Render<AddAgent>();

        Assert.All(cut.Instance.GetAllCommands(), command => Assert.DoesNotContain("localhost", command));
        Assert.Contains("https://deploy.example.test/downloads/aetheus-agent-linux-x64.tar.gz", cut.Instance.GetAllCommands()[0]);
    }

    [Fact]
    public void ServerBaseUrl_UsesConfig()
    {
        // The server advertises nothing: the configured ApiBaseUrl is the fallback.
        _handler.SetResponse(HttpMethod.Get, "api/servers/agent-server-url", System.Net.HttpStatusCode.NotFound);
        var cut = Render<AddAgent>();

        Assert.Contains("https://localhost:5301/downloads/aetheus-agent-linux-x64.tar.gz", cut.Instance.GetAllCommands()[0]);
        Assert.Contains("--server-url https://localhost:5301", cut.Instance.GetAllCommands()[2]);
    }

    // --- Navigation ---

    [Fact]
    public async Task GoBack_NavigatesToServers()
    {
        var cut = Render<AddAgent>();
        await PressAsync(cut, CancelButton);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/servers", nav.Uri);
    }

    [Fact]
    public async Task OnFinish_NavigatesToServers()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 3);
        await PressAsync(cut, FinishButton);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/servers", nav.Uri);
    }

    /// <summary>Opened from the pipeline setup wizard, Done and Cancel return to its summary; an
    /// address that is not a local path is ignored.</summary>
    [Theory]
    [InlineData("OnFinish")]
    [InlineData("GoBack")]
    public async Task LeavingTheWizard_ReturnsToTheLocalReturnAddress(string exit)
    {
        IssueTokens();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("http://localhost/servers/add-agent?returnUrl=" + Uri.EscapeDataString("/pipelines/setup?projectId=7&step=summary"));
        var cut = Render<AddAgent>();

        await LeaveAsync(cut, exit);

        Assert.EndsWith("/pipelines/setup?projectId=7&step=summary", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingTheWizard_IgnoresAReturnAddressOutsideTheApplication()
    {
        IssueTokens();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("http://localhost/servers/add-agent?returnUrl=" + Uri.EscapeDataString("https://evil.example/x"));
        var cut = Render<AddAgent>();

        await LeaveAsync(cut, "OnFinish");

        Assert.Equal("http://localhost/servers", nav.Uri);
    }

    /// <summary>"OnFinish" is Done on the last step, "GoBack" is Cancel.</summary>
    private static async Task LeaveAsync(IRenderedComponent<AddAgent> cut, string exit)
    {
        if (exit == "OnFinish")
        {
            await GoToStepAsync(cut, 3);
            await PressAsync(cut, FinishButton);
        }
        else
        {
            await PressAsync(cut, CancelButton);
        }
    }

    // --- Moving between steps ---

    [Fact]
    public async Task OnStepChanged_UpdatesCurrentStep()
    {
        IssueTokens("step-change-token");
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 2);

        Assert.Equal(2, CurrentStep(cut));
        Assert.Contains("WizardStepInstall", cut.Find(SelectedStep).TextContent);
    }

    [Fact]
    public async Task OnStepChanged_ToVerifyStep_StartsSignalR()
    {
        // FRONTTEST-003: no blanket catch. The test hub refuses the connection: entering the verify
        // step must have started the monitor (the search panel is shown, listening), reported the
        // hub's failure, and read the wizard's own token (id 9) once.
        IssueTokens("wizard-token", 9);
        _handler.SetJsonResponse("api/auth/registration-tokens/9", new RegistrationTokenDto { Id = 9, IsUsed = false });
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 3);

        Assert.NotEmpty(cut.FindAll(".wizard-polling"));
        Assert.Contains("WizardHubError", cut.Markup);
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("api/auth/registration-tokens/9", StringComparison.Ordinal));
        await cut.Instance.DisposeAsync();
    }

    // --- DisposeAsync ---

    [Fact]
    public async Task DisposeAsync_WithNoHub_Succeeds()
    {
        var cut = Render<AddAgent>();
        await cut.Instance.DisposeAsync();
    }

    // --- Rendering with different platforms ---

    [Fact]
    public async Task Render_WithWindowsSelected_ContainsWindowsContent()
    {
        // FRONTTEST-002: the install step a Windows choice produces shows the Windows commands.
        IssueTokens();
        var cut = Render<AddAgent>();
        ClickPlatformCard(cut, "Windows");
        await GoToStepAsync(cut, 1);

        Assert.Equal(2, CurrentStep(cut));
        Assert.Contains("install-agent-windows.ps1", cut.Markup);
        Assert.DoesNotContain("install-agent-linux.sh", cut.Markup);
    }

    // --- Token masking in summary ---

    [Fact]
    public async Task TokenMasking_LongToken_ShowsPartialMask()
    {
        // FRONTTEST-002: the install step shows the token block with its first five characters only;
        // the full token appears nowhere in the markup.
        const string token = "abcdefghijklmnopqrstuvwxyz0123456789";
        IssueTokens(token);
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 2);

        var tokenBlock = cut.Find(".wizard-token-code");
        Assert.Contains("abcde", tokenBlock.TextContent);
        Assert.DoesNotContain("abcdef", tokenBlock.TextContent);
        Assert.DoesNotContain(token, cut.Markup);
    }

    // --- Step contents ---

    [Fact]
    public void PlatformStep_RendersCards()
    {
        var cut = Render<AddAgent>();
        Assert.Contains("omni-selectable-card", cut.Markup);
        Assert.Contains("omni-selectable-card__icon", cut.Markup);
        Assert.Contains("<svg", cut.Markup);
        Assert.Contains("omni-selectable-card--selected", cut.Markup);
    }

    [Fact]
    public async Task InstallStep_Linux_RendersThreeCodeBlocks()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 2);
        Assert.Contains("wizard-code-block", cut.Markup);
        Assert.Contains("curl", cut.Markup);
        Assert.Contains("tar", cut.Markup);
        Assert.Contains("install-agent-linux.sh", cut.Markup);
        Assert.Contains("download", cut.Markup); // download button
    }

    [Fact]
    public async Task InstallStep_Windows_RendersTwoCodeBlocks()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        ClickPlatformCard(cut, "Windows");
        await GoToStepAsync(cut, 1);
        Assert.Contains("wizard-code-block", cut.Markup);
        Assert.Contains("Invoke-WebRequest", cut.Markup);
        Assert.Contains("install-agent-windows.ps1", cut.Markup);
    }

    [Fact]
    public async Task VerifyStep_NotListening_RendersCheckButton()
    {
        // Entering the verify step starts the search; stopping it returns to the idle state.
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 3);

        await cut.Find(".wizard-polling ~ div button").ClickAsync(new MouseEventArgs());

        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Refresh);
        Assert.Empty(cut.FindAll(".wizard-polling"));
        Assert.Contains("wizard-verify-list", cut.Markup);
    }

    [Fact]
    public async Task VerifyStep_Listening_RendersPolling()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 3);
        Assert.Contains("wizard-polling", cut.Markup);
    }

    [Fact]
    public async Task VerifyStep_AgentFound_RendersSuccessAlert()
    {
        // The wizard's own token comes back used by server 5: the verify step shows the success
        // alert with a link to that server and no longer searches.
        IssueTokens("wizard-token", 9);
        _handler.SetJsonResponse("api/auth/registration-tokens/9",
            new RegistrationTokenDto { Id = 9, IsUsed = true, UsedByServerId = 5 });
        _handler.SetJsonResponse("api/servers/5", new ServerDetailDto { Id = 5, Name = "my-agent" });
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 3);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("svg.omni-icon.wizard-verify-success-icon")));
        Assert.DoesNotContain("check_circle", cut.Markup);
        Assert.Equal("/servers/5/overview", cut.Find(".omni-alert a").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".wizard-polling"));
    }

    [Fact]
    public async Task InstallStep_Linux_RendersDownloadButton()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 2);
        Assert.Contains("download", cut.Markup);
    }

    [Fact]
    public void PlatformStep_LinuxSelected_ShowsTheFrameWithoutACheckMark()
    {
        var cut = Render<AddAgent>();
        // The chosen card is its accent frame; no check mark is added beside the label.
        var selected = cut.Find(".omni-selectable-card.omni-selectable-card--selected");
        Assert.Equal("true", selected.GetAttribute("aria-checked"));
        Assert.Equal(2, cut.FindAll(".omni-selectable-card").Count);
        Assert.All(cut.FindAll(".omni-selectable-card"), card =>
        {
            Assert.Empty(PageAddedElements(card));
            Assert.Empty(card.QuerySelectorAll(".omni-selectable-card__box"));
        });
    }

    // What the page itself put inside an OE selectable card: any element that is not one of the card's own
    // parts (omni-selectable-card__*) and does not sit in its icon slot. Read on the classes, not on OE's
    // nesting, so a new wrapper inside the card (OE 1.9.1 groups box, icon and title in a head) changes nothing.
    private static List<AngleSharp.Dom.IElement> PageAddedElements(AngleSharp.Dom.IElement card) =>
        [.. card.QuerySelectorAll("*").Where(element =>
            element.Closest(".omni-selectable-card__icon") is null
            && !element.ClassList.Any(name => name.StartsWith("omni-selectable-card__", StringComparison.Ordinal)))];

    [Fact]
    public async Task CapabilitiesStep_SelectedCards_CarryOnlyOesDrawnBox()
    {
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 1);
        Assert.NotEmpty(cut.FindAll(".omni-selectable-card.omni-selectable-card--selected"));
        // A check box card draws OE's own box (R2-4); the page adds no check mark of its own beside it.
        Assert.All(cut.FindAll(".omni-selectable-card"), card =>
        {
            Assert.Empty(PageAddedElements(card));
            Assert.Single(card.QuerySelectorAll(".omni-selectable-card__box"));
        });
    }

    [Fact]
    public async Task InstallStep_RendersTheCommandsInOmniCodeBlocks()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 2);
        // OE's readable code surface, not the local dark blocks.
        Assert.NotEmpty(cut.FindAll(".omni-code-block.wizard-code-block"));
        Assert.Empty(cut.FindAll(".wizard-pre"));
        Assert.Empty(cut.FindAll(".wizard-code-header"));
    }

    [Fact]
    public async Task VerifyStep_Listening_PutsStopOutsideThePanel()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 3);
        // The panel holds only the search state; stopping it is a button below.
        var panel = cut.Find(".wizard-polling");
        Assert.Empty(panel.QuerySelectorAll("button"));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("WizardCancelCheck"));
    }

    [Fact]
    public async Task GenerateTokenAsync_NullResponse_DoesNotSetToken()
    {
        // FRONTTEST-003: no catch. A refused creation answers null; the wizard keeps no token, says so,
        // and never claims success.
        _handler.SetResponse(HttpMethod.Post, "api/auth/registration-tokens", System.Net.HttpStatusCode.NotFound);
        var cut = Render<AddAgent>();

        await GoToStepAsync(cut, 2);

        Assert.Equal(1, TokenRequests());
        Assert.Empty(cut.FindAll(".wizard-token-code"));
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger && toast.Detail == "WizardTokenFailed");
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Success);
    }

    // --- Verify step listening state ---

    [Fact]
    public async Task VerifyStep_InitialState_NotListeningNotFound()
    {
        IssueTokens();
        var cut = Render<AddAgent>();
        await GoToStepAsync(cut, 2);

        // Before the verify step nothing searches: no panel and no read of the token's status.
        Assert.Empty(cut.FindAll(".wizard-polling"));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("api/auth/registration-tokens/", StringComparison.Ordinal));
    }

    // --- Breadcrumb set on init ---

    [Fact]
    public void OnInitialized_SetsBreadcrumb()
    {
        var cut = Render<AddAgent>();
        // OnInitializedAsync sets the Servers > AddAgent breadcrumb trail.
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();
        Assert.Equal(2, breadcrumb.Items.Count);
        Assert.Contains(breadcrumb.Items, i => i.Text == "AddAgent");
    }
}
