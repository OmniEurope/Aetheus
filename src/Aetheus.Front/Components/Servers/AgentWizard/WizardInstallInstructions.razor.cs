// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.AgentWizard;

public partial class WizardInstallInstructions : AgentInstallOptionsComponentBase
{
    [Parameter] public string? Token { get; set; }

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private string[] _linuxStep1 = [];
    private string[] _linuxStep2 = [];
    private string[] _linuxStep3 = [];
    private string _linuxCombined = string.Empty;
    private string _linuxCombinedDev = string.Empty;

    private string[] _winStep1 = [];
    private string[] _winStep2 = [];
    private string[] _winStep3 = [];
    private string _winCombined = string.Empty;

    protected override void OnParametersSet() => BuildCommands();

    private void BuildCommands()
    {
        // TLS-bypass fragments are emitted ONLY in dev mode (self-signed certificate);
        // otherwise the generated commands keep full TLS validation.
        var insecure = DevModeInsecureTls;

        _linuxStep1 =
        [
            $"cd /tmp && curl -fsSL {(insecure ? "-k " : string.Empty)}-o agent.tar.gz {ServerBaseUrl}/downloads/aetheus-agent-linux-x64.tar.gz"
        ];
        _linuxStep2 =
        [
            "mkdir -p /tmp/aetheus-agent-install && tar xzf /tmp/agent.tar.gz -C /tmp/aetheus-agent-install"
        ];
        _linuxStep3 =
        [
            $"cd /tmp/aetheus-agent-install && sudo sh install-agent-linux.sh -y --server-url {ServerBaseUrl}{(insecure ? " --allow-insecure-certs" : string.Empty)}{BuildLinuxModuleFlags()}"
        ];
        _linuxCombined = string.Join(" && ", _linuxStep1.Concat(_linuxStep2).Concat(_linuxStep3));

        // Dev-only convenience: a copy of the combined one-liner whose host is rewritten to
        // host.docker.internal so it runs verbatim from inside the local VPS-sim container
        // (where 'localhost' is the container itself, not the host running the backend). Shown
        // only when dev mode is on AND the server URL is a localhost loopback.
        var devBaseUrl = RewriteHostForVpsSim(ServerBaseUrl);
        _linuxCombinedDev =
            DevModeInsecureTls
            && !string.IsNullOrEmpty(ServerBaseUrl)
            && !string.Equals(devBaseUrl, ServerBaseUrl, StringComparison.Ordinal)
                ? _linuxCombined.Replace(ServerBaseUrl, devBaseUrl, StringComparison.Ordinal)
                : string.Empty;

        // Download & extract to TEMP (no admin needed), then run install script (self-elevates).
        // In dev mode only, an SSL bypass is added before download so self-signed dev certs work.
        var winArchive = $"aetheus-agent-win-x64-v{SiteVersion}.zip";
        var winExtractDir = "$env:TEMP\\AetheusAgentInstall";
        var winStep1 = new List<string>
        {
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12"
        };
        if (insecure)
        {
            winStep1.Add("try { Add-Type -TypeDefinition 'using System.Net; public static class TlsFix { public static void Run() { ServicePointManager.ServerCertificateValidationCallback = delegate { return true; }; } }'; [TlsFix]::Run() } catch {}");
        }
        winStep1.Add($"Invoke-WebRequest -Uri '{ServerBaseUrl}/downloads/aetheus-agent-win-x64.zip' -OutFile \"$env:TEMP\\{winArchive}\" -UseBasicParsing");
        _winStep1 = [.. winStep1];
        _winStep2 =
        [
            $"Expand-Archive -Path \"$env:TEMP\\{winArchive}\" -DestinationPath '{winExtractDir}' -Force"
        ];
        _winStep3 =
        [
            $"powershell -ExecutionPolicy Bypass -File '{winExtractDir}\\install-agent-windows.ps1' -ServerUrl '{ServerBaseUrl}'{(insecure ? " -AllowInsecureCerts" : string.Empty)}{BuildWindowsModuleFlags()}"
        ];
        _winCombined = string.Join("; ", _winStep1.Concat(_winStep2).Concat(_winStep3));
    }

    // Rewrite a loopback host to the Docker-Desktop alias the VPS-sim container can reach.
    private static string RewriteHostForVpsSim(string url)
        => url.Replace("localhost", "host.docker.internal", StringComparison.OrdinalIgnoreCase)
              .Replace("127.0.0.1", "host.docker.internal", StringComparison.Ordinal);

    // Shared with AddAgent's "copy all commands" summary via AgentInstallFlags (S-TECH-WZFL).
    private string BuildLinuxModuleFlags() => AgentInstallFlags.BuildLinux(
        IncludePipelineRunner, IncludeServerManagement, IncludeDeploymentAgent,
        IncludePatchManagement, IncludeFirewallManagement, IncludeDocker, IncludeHostReboot);

    private string BuildWindowsModuleFlags() => AgentInstallFlags.BuildWindows(IncludePipelineRunner);
}
