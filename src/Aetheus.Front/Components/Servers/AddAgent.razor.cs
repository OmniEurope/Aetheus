// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

public partial class AddAgent : IAsyncDisposable
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ILogger<AddAgent> Logger { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    private WizardDialog _wizard = default!;
    private List<WizardStep> _steps = [];
    private int _currentStep;

    private string _platform = "linux";
    private int _tokenExpirationHours = 24;
    private RegistrationTokenDto? _generatedToken;
    private bool _tokenGenerating;

    // Real-time Verify-step detection (SignalR + polling + registration-token fast-path + countdown)
    // lives in a collaborator so this page stays within the 600-line budget. See AgentDetectionMonitor.
    private AgentWizard.AgentDetectionMonitor? _detection;

    // Module selection - lifted from WizardInstallInstructions so the
    // capabilities step and the install step share the same state.
    // Defaults are intentionally all-on: the wizard pre-provisions a full-capability
    // managed server in one copy-paste (the common onboarding case). This is a UI
    // convenience only - the installer itself stays secure-by-default (a bare install
    // with no emitted flags grants zero privilege). Operators untick to harden.
    // See docs/contracts/deployment.md "Add-Agent wizard defaults" (owner decision 2026-06-21).
    private bool _includePipelineRunner = true;
    private bool _includeServerManagement = true;
    // Deployment agent (cross-agent deploy target) is opt-in OFF by default: most onboarded hosts are
    // CI/CD runners / managed servers, not deploy targets. Operators tick it for a deployment host.
    private bool _includeDeploymentAgent;
    // R3-57: patch-manage is ticked by default like the other capabilities (the operator unticks it).
    private bool _includePatchManagement = true;
    private bool _includeFirewallManagement;
    private bool _includeDocker = true;
    private bool _devModeInsecureTls;
    private bool _devModeAutoApplied;

    // Local dev reaches the backend over plain HTTP on :5300 (the HTTPS :5301 endpoint isn't used
    // by the tunnelled agent). Production overrides this via Aetheus:PublicApiBaseUrl, surfaced
    // through GetAgentServerUrlAsync.
    private const string DefaultApiBaseUrl = LocalDevelopmentEndpoints.ApiHttpBaseUrl;

    private string _serverBaseUrl = "";
    private string SiteVersion => (Config["App:Version"] ?? "dev").Trim();
    private string ServerBaseUrl => string.IsNullOrEmpty(_serverBaseUrl)
        ? (Config["ApiBaseUrl"] ?? DefaultApiBaseUrl).TrimEnd('/')
        : _serverBaseUrl;

    internal List<string> GetAllCommands()
    {
        if (_platform == "linux")
        {
            // F-002: the TLS-bypass fragments are emitted ONLY in dev mode (self-signed cert), matching
            // WizardInstallInstructions - outside dev mode the commands keep full TLS validation.
            var insecure = _devModeInsecureTls;
            return
            [
                $"cd /tmp && curl -fsSL {(insecure ? "-k " : string.Empty)}-o agent.tar.gz {ServerBaseUrl}/downloads/aetheus-agent-linux-x64.tar.gz",
                "mkdir -p /tmp/aetheus-agent-install && tar xzf /tmp/agent.tar.gz -C /tmp/aetheus-agent-install",
                $"cd /tmp/aetheus-agent-install && sudo sh install-agent-linux.sh -y --server-url {ServerBaseUrl}{(insecure ? " --allow-insecure-certs" : string.Empty)}{LinuxModuleFlags()}"
            ];
        }
        return
        [
            $"Invoke-WebRequest -Uri {ServerBaseUrl}/downloads/aetheus-agent-win-x64.zip -OutFile $env:TEMP\\aetheus-agent-win.zip",
            "Expand-Archive -Path $env:TEMP\\aetheus-agent-win.zip -DestinationPath $env:TEMP\\AetheusAgentInstall -Force",
            $"powershell -ExecutionPolicy Bypass -File $env:TEMP\\AetheusAgentInstall\\install-agent-windows.ps1 -ServerUrl '{ServerBaseUrl}'{AgentWizard.AgentInstallFlags.BuildWindows(_includePipelineRunner)}"
        ];
    }

    // Shared with WizardInstallInstructions via AgentInstallFlags so the "copy all" summary matches the
    // command shown in the install step (S-TECH-WZFL).
    private string LinuxModuleFlags() => AgentWizard.AgentInstallFlags.BuildLinux(
        _includePipelineRunner, _includeServerManagement, _includeDeploymentAgent,
        _includePatchManagement, _includeFirewallManagement, _includeDocker);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _serverBaseUrl = (await Api.Security.GetAgentServerUrlAsync()
                ?? (Config["ApiBaseUrl"] ?? DefaultApiBaseUrl)).TrimEnd('/');
        }
        catch (HttpRequestException)
        {
            _serverBaseUrl = (Config["ApiBaseUrl"] ?? DefaultApiBaseUrl).TrimEnd('/');
        }
        _detection = new AgentWizard.AgentDetectionMonitor(Api, HubFactory, Logger,
            () => InvokeAsync(() => { RefreshSteps(); StateHasChanged(); }));
        _steps = BuildSteps();
        Breadcrumb.Set(
            new BreadcrumbItem(L["Servers"], "/servers"),
            new BreadcrumbItem(L["AddAgent"]));
    }

    private List<WizardStep> BuildSteps() =>
    [
        new WizardStep { Title = L["WizardStepPlatform"], Content = BuildPlatformStep() },
        new WizardStep { Title = L["WizardStepCapabilities"], Content = BuildCapabilitiesStep() },
        new WizardStep { Title = L["WizardStepInstall"], Content = BuildInstallStep() },
        new WizardStep { Title = L["WizardStepVerify"], Content = BuildVerifyStep() },
    ];

    private void RefreshSteps()
    {
        _steps = BuildSteps();
        StateHasChanged();
    }

    private bool CanAdvance() => _currentStep switch
    {
        0 => !string.IsNullOrEmpty(_platform),
        _ => true,
    };

    // Block leaving the capabilities step (index 1) when the agent would have no actionable role:
    // neither the pipeline-runner module (CI/CD) nor server-management. The step shows an inline
    // warning; this dialog explains why Next is refused. Returns true to allow advancing.
    private async Task<bool> ValidateBeforeNextAsync(int currentStep)
    {
        if (currentStep == 1 && !_includePipelineRunner && !_includeServerManagement && !_includeDeploymentAgent && !_includePatchManagement && !_includeFirewallManagement)
        {
            await Dialog.Alert(L["WizardNoModuleError"], L["WizardNoModuleErrorTitle"], new OmniAlertOptions { OkButtonText = L["OK"] });
            return false;
        }
        return true;
    }

    // Docker container isolation is a pipeline feature - it requires the pipeline-runner module, so
    // force it off whenever the runner is unselected. Independent of server-management, which manages
    // the Docker service on its own (the operator may want server admin without container isolation).
    private void NormalizeDocker()
    {
        if (!_includePipelineRunner)
            _includeDocker = false;
    }

    private async Task OnStepChanged(int step)
    {
        var previousStep = _currentStep;
        _currentStep = step;

        // Step 1 = capabilities: auto-detect dev-mode TLS on first visit.
        if (step == 1 && !_devModeAutoApplied && IsLocalhostUrl(ServerBaseUrl))
        {
            _devModeInsecureTls = true;
            _devModeAutoApplied = true;
        }

        // Step 2 = install: generate token on first visit.
        if (step == 2 && _generatedToken is null && !_tokenGenerating)
            await GenerateTokenAsync();

        // Step 3 = verify: start/stop the real-time detection monitor.
        if (previousStep == 3 && step != 3 && _detection is not null)
            await _detection.StopAsync();
        if (step == 3 && _detection is { AgentFound: false })
            await _detection.StartAsync(_generatedToken);

        RefreshSteps();
    }

    /// <summary>The page that sent the user here to add a server (the pipeline setup wizard's
    /// summary, with its choices). Done, Cancel and Back return to it when it is local.</summary>
    [SupplyParameterFromQuery(Name = LocalReturnUrl.Parameter)] public string? ReturnUrl { get; set; }

    private string ExitHref => LocalReturnUrl.Validated(ReturnUrl) ?? "/servers";

    private void GoBack() => Navigation.NavigateTo(ExitHref);

    private void OnFinish() => Navigation.NavigateTo(ExitHref);

    private static bool IsLocalhostUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    // --- Capabilities step ---
    private RenderFragment BuildCapabilitiesStep() => builder =>
    {
        builder.OpenComponent<AgentWizard.WizardCapabilitiesStep>(0);
        builder.AddAttribute(1, "Platform", _platform);
        builder.AddAttribute(2, "IncludePipelineRunner", _includePipelineRunner);
        builder.AddAttribute(3, "IncludePipelineRunnerChanged", EventCallback.Factory.Create<bool>(this, v => { _includePipelineRunner = v; NormalizeDocker(); RefreshSteps(); }));
        builder.AddAttribute(4, "IncludeServerManagement", _includeServerManagement);
        builder.AddAttribute(5, "IncludeServerManagementChanged", EventCallback.Factory.Create<bool>(this, v => { _includeServerManagement = v; RefreshSteps(); }));
        builder.AddAttribute(6, "IncludeDeploymentAgent", _includeDeploymentAgent);
        builder.AddAttribute(7, "IncludeDeploymentAgentChanged", EventCallback.Factory.Create<bool>(this, v => { _includeDeploymentAgent = v; RefreshSteps(); }));
        builder.AddAttribute(8, "IncludeDocker", _includeDocker);
        builder.AddAttribute(9, "IncludeDockerChanged", EventCallback.Factory.Create<bool>(this, v => { _includeDocker = v; RefreshSteps(); }));
        builder.AddAttribute(10, "DevModeInsecureTls", _devModeInsecureTls);
        builder.AddAttribute(11, "DevModeInsecureTlsChanged", EventCallback.Factory.Create<bool>(this, v => { _devModeInsecureTls = v; RefreshSteps(); }));
        builder.AddAttribute(12, "IncludePatchManagement", _includePatchManagement);
        builder.AddAttribute(13, "IncludePatchManagementChanged", EventCallback.Factory.Create<bool>(this, v => { _includePatchManagement = v; RefreshSteps(); }));
        builder.AddAttribute(14, "IncludeFirewallManagement", _includeFirewallManagement);
        builder.AddAttribute(15, "IncludeFirewallManagementChanged", EventCallback.Factory.Create<bool>(this, v => { _includeFirewallManagement = v; RefreshSteps(); }));
        builder.CloseComponent();
    };

    // --- Platform step ---
    private RenderFragment BuildPlatformStep() => builder =>
    {
        builder.OpenComponent<OmniStack>(0);
        builder.AddAttribute(1, nameof(OmniStack.Gap), OmniSpacing.Medium);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(inner =>
        {
            inner.OpenElement(0, "p");
            inner.AddContent(1, L["WizardPlatformDescription"]);
            inner.CloseElement();

            // One choice among two OE selectable cards, in a named radio group; R-3: the two are tiles of
            // one OmniTileGrid (same width, same height).
            inner.OpenComponent<OmniTileGrid>(2);
            inner.AddAttribute(3, nameof(OmniTileGrid.Columns), 2);
            inner.AddAttribute(4, "role", "radiogroup");
            inner.AddAttribute(5, "aria-label", L["Platform"].Value);
            inner.AddAttribute(6, "ChildContent", (RenderFragment)(options =>
            {
                BuildPlatformOption(options, "linux", L["Linux"]);
                BuildPlatformOption(options, "windows", L["Windows"]);
            }));
            inner.CloseComponent();
        }));
        builder.CloseComponent();
    };

    private void BuildPlatformOption(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder, string value, string label)
    {
        var isSelected = _platform == value;
        var seq = value == "linux" ? 10 : 20;

        async Task SelectAsync()
        {
            _platform = value;
            RefreshSteps();
            await _wizard.NextStep();
        }

        // OE's OmniSelectableCard. Picking a card, even the chosen one again, moves on to the next
        // step; the choice shows by the accent frame and tint alone, no check mark.
        builder.OpenComponent<OmniSelectableCard>(seq);
        builder.AddComponentParameter(seq + 1, nameof(OmniSelectableCard.Title), label);
        builder.AddComponentParameter(seq + 2, nameof(OmniSelectableCard.Value), isSelected);
        builder.AddComponentParameter(seq + 3, nameof(OmniSelectableCard.ValueChanged), EventCallback.Factory.Create<bool>(this, _ => SelectAsync()));
        // Brand SVG icon (Tux silhouette for Linux, 4-pane logo for Windows), decorative.
        builder.AddComponentParameter(seq + 4, nameof(OmniSelectableCard.Icon), (RenderFragment)(icon =>
            icon.AddMarkupContent(0, value == "linux" ? LinuxBrandSvg : WindowsBrandSvg)));
        builder.CloseComponent();
    }

    // Simplified Tux silhouette (public-domain) - keeps the file dependency-free.
    private const string LinuxBrandSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" width=\"32\" height=\"32\" aria-hidden=\"true\" focusable=\"false\">"
        + "<path fill=\"currentColor\" d=\"M12 2c-2.4 0-3.8 1.9-3.8 4.6 0 1.4.4 2.6.4 3.5 0 1-.7 1.6-1.6 2.6-1.4 1.4-2.5 2.7-2.5 4.4 0 1.7 1 2.4 1 3.4 0 .8-.5 1-.5 1.4 0 .8 1.5 1.1 3.5 1.1.9 0 1.6-.2 1.9-.7.3-.4.6-.6 1.6-.6s1.3.2 1.6.6c.3.5 1 .7 1.9.7 2 0 3.5-.3 3.5-1.1 0-.4-.5-.6-.5-1.4 0-1 1-1.7 1-3.4 0-1.7-1.1-3-2.5-4.4-1-1-1.6-1.6-1.6-2.6 0-.9.4-2.1.4-3.5C15.8 3.9 14.4 2 12 2Zm-1.6 3.4c.5 0 .9.4.9.9s-.4.9-.9.9-.9-.4-.9-.9.4-.9.9-.9Zm3.2 0c.5 0 .9.4.9.9s-.4.9-.9.9-.9-.4-.9-.9.4-.9.9-.9ZM12 8c.7 0 1.4.4 1.4 1 0 .4-.7.7-1.4.7s-1.4-.3-1.4-.7c0-.6.7-1 1.4-1Z\"/>"
        + "</svg>";

    // Windows 4-pane brand glyph.
    private const string WindowsBrandSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" width=\"32\" height=\"32\" aria-hidden=\"true\" focusable=\"false\">"
        + "<path fill=\"currentColor\" d=\"M3 5.5 11 4.4v7.1H3V5.5Zm0 7.5h8v7.1L3 19V13Zm9-8.7L22 3v8.5H12V4.3Zm0 8.7h10v8.5l-10-1.3V13Z\"/>"
        + "</svg>";

    // --- Token step ---
    private static void BuildTokenComponent(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder, int seq, string token)
    {
        builder.OpenComponent<AgentWizard.WizardTokenBlock>(seq);
        builder.AddAttribute(seq + 1, "Token", token);
        builder.CloseComponent();
    }

    private async Task GenerateTokenAsync()
    {
        _tokenGenerating = true;
        RefreshSteps();

        _generatedToken = await Api.Auth.CreateRegistrationTokenAsync(_tokenExpirationHours);
        _tokenGenerating = false;

        if (_generatedToken is null)
            Toast.Error("Error", "WizardTokenFailed");
        else
            Toast.Success("Generated", "WizardTokenGenerated");

        RefreshSteps();
    }

    // --- Install step ---
    private RenderFragment BuildInstallStep() => builder =>
    {
        builder.OpenComponent<AgentWizard.WizardInstallStep>(0);
        builder.AddAttribute(1, "Platform", _platform);
        builder.AddAttribute(2, "ServerBaseUrl", ServerBaseUrl);
        builder.AddAttribute(3, "SiteVersion", SiteVersion);
        builder.AddAttribute(4, "InstallerToken", _generatedToken?.Token);
        builder.AddAttribute(5, "IncludePipelineRunner", _includePipelineRunner);
        builder.AddAttribute(6, "IncludeServerManagement", _includeServerManagement);
        builder.AddAttribute(7, "IncludeDeploymentAgent", _includeDeploymentAgent);
        builder.AddAttribute(8, "IncludeDocker", _includeDocker);
        builder.AddAttribute(9, "DevModeInsecureTls", _devModeInsecureTls);
        builder.AddAttribute(10, "IncludePatchManagement", _includePatchManagement);
        builder.AddAttribute(11, "IncludeFirewallManagement", _includeFirewallManagement);
        builder.CloseComponent();
    };

    // --- Verify step (delegates to AgentDetectionMonitor) ---
    private RenderFragment BuildVerifyStep() => builder =>
    {
        builder.OpenComponent<AgentWizard.WizardVerifyStep>(0);
        builder.AddAttribute(1, "AgentFound", _detection?.AgentFound ?? false);
        builder.AddAttribute(2, "Listening", _detection?.Listening ?? false);
        builder.AddAttribute(3, "AgentName", _detection?.AgentName);
        builder.AddAttribute(4, "HubError", _detection?.HubError);
        builder.AddAttribute(5, "OnStart", EventCallback.Factory.Create(this, StartDetectionAsync));
        builder.AddAttribute(6, "OnCancel", EventCallback.Factory.Create(this, CancelDetectionAsync));
        builder.AddAttribute(7, "ElapsedSeconds", _detection?.ElapsedSeconds ?? 0);
        builder.AddAttribute(8, "MaxSeconds", AgentWizard.AgentDetectionMonitor.MaxSeconds);
        builder.AddAttribute(9, "TimedOut", _detection?.TimedOut ?? false);
        builder.AddAttribute(10, "DetectedServerId", _detection?.DetectedServerId);
        builder.CloseComponent();
    };

    private Task StartDetectionAsync() => _detection?.StartAsync(_generatedToken) ?? Task.CompletedTask;

    private Task CancelDetectionAsync() => _detection?.CancelAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_detection is not null)
            await _detection.DisposeAsync();
    }
}
