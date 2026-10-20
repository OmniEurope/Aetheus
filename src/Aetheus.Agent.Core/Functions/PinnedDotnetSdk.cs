// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of <c>deploy/scripts/ensure-dotnet-sdk.sh</c>: resolves the exact SDK <c>global.json</c> pins,
/// so an agent never changes SDK silently when its host is upgraded. The host <c>dotnet</c> is kept
/// when it resolves that exact version from the manifest's directory; otherwise the SDK is installed
/// with the official installer, pinned by commit and SHA-256, into a version-isolated, non-root cache
/// (<c>AETHEUS_DOTNET_ROOT</c>, default <c>$HOME/.aetheus/dotnet</c>).
///
/// Differences, deliberate: the installer is downloaded and checked in-process instead of through
/// <c>curl</c> and <c>sha256sum</c> (same URL, same digest, same retry policy), so only <c>bash</c> is
/// required to install; and the installer's output goes to the step log instead of
/// <c>/dev/null</c>, since the resolved path travels as an output rather than on stdout.
/// </summary>
internal sealed partial class PinnedDotnetSdk(IPipelineFunctionTools tools, PinnedDownload installer)
{
    internal const string InstallerUrl =
        "https://raw.githubusercontent.com/dotnet/install-scripts/da3ce11ba63f3dbb0fb835d41bda2665d5c48e84/src/dotnet-install.sh";

    internal const string InstallerSha256 = "082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e";

    /// <summary>The official installer at the commit and digest the script pinned.</summary>
    internal static readonly PinnedDownload OfficialInstaller = new(new Uri(InstallerUrl), InstallerSha256);

    [GeneratedRegex("^\\s*\"version\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex VersionLine();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex ExactVersion();

    private static string ExecutableName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    public async Task<ToolchainResolution> ResolveAsync(
        PipelineFunctionContext context, string workingDirectory, string globalJson, CancellationToken cancellationToken)
    {
        var manifest = Path.GetFullPath(globalJson, workingDirectory);
        if (!File.Exists(manifest) || new FileInfo(manifest).Length == 0)
            return ToolchainResolution.Failed($"Pinned SDK manifest not found: {globalJson}");
        var version = ReadVersion(await File.ReadAllLinesAsync(manifest, cancellationToken).ConfigureAwait(false));
        if (version is null || !ExactVersion().IsMatch(version))
            return ToolchainResolution.Failed($"Pinned SDK version is missing or invalid in {globalJson}");
        return await ResolveVersionAsync(context, new Pin(version, Path.GetDirectoryName(manifest)!), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ToolchainResolution> ResolveVersionAsync(PipelineFunctionContext context, Pin pin, CancellationToken cancellationToken)
    {
        var system = FunctionShell.FindOnPath(context, "dotnet");
        if (await IsUsableAsync(context, system, pin, cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Found(system!);

        var home = FunctionShell.Variable(context, "HOME");
        if (home is null)
            return ToolchainResolution.Failed("HOME is required to install the pinned .NET SDK without root");
        var installRoot = FunctionShell.Variable(context, "AETHEUS_DOTNET_ROOT") ?? Path.Combine(home, ".aetheus", "dotnet");
        var installDirectory = Path.Combine(installRoot, pin.Version);
        var local = Path.Combine(installDirectory, ExecutableName);
        if (await IsUsableAsync(context, local, pin, cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Found(local);

        var installError = await InstallAsync(context, pin.Version, installDirectory, cancellationToken).ConfigureAwait(false);
        if (installError is not null)
            return ToolchainResolution.Failed(installError);
        return await IsUsableAsync(context, local, pin, cancellationToken).ConfigureAwait(false)
            ? ToolchainResolution.Found(local)
            : ToolchainResolution.Failed($"Pinned .NET SDK {pin.Version} installation could not be verified");
    }

    /// <summary>The first <c>"version": "..."</c> line, as the script's <c>sed ... | head -n 1</c>.</summary>
    internal static string? ReadVersion(IEnumerable<string> lines) =>
        lines.Select(line => VersionLine().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value;

    /// <summary>The script's <c>[ -x "$dotnet" ] &amp;&amp; resolves_exact_sdk "$dotnet"</c>: the
    /// executable reports exactly the pinned version when run from the manifest's directory.</summary>
    private async Task<bool> IsUsableAsync(PipelineFunctionContext context, string? dotnet, Pin pin, CancellationToken cancellationToken)
    {
        if (dotnet is null || !FunctionShell.IsExecutable(dotnet))
            return false;
        var reported = await FunctionShell
            .CaptureAsync(tools, FunctionShell.StartInfo(context, dotnet, pin.ManifestDirectory, "--version"), cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(reported, pin.Version, StringComparison.Ordinal);
    }

    /// <summary>Runs the pinned official installer. Returns null once it exited cleanly, the reason
    /// otherwise.</summary>
    private async Task<string?> InstallAsync(
        PipelineFunctionContext context, string version, string installDirectory, CancellationToken cancellationToken)
    {
        var bash = FunctionShell.FindOnPath(context, "bash");
        if (bash is null)
            return "bash is required to run the official .NET SDK installer";

        Directory.CreateDirectory(installDirectory);
        var installerFile = Path.GetTempFileName();
        try
        {
            await context.Log($"Installing pinned .NET SDK {version} in {installDirectory}").ConfigureAwait(false);
            await tools.DownloadAsync(installer.Source, installerFile, cancellationToken).ConfigureAwait(false);
            if (!await FunctionShell.HasSha256Async(installerFile, installer.Sha256, cancellationToken).ConfigureAwait(false))
                return $"The downloaded .NET SDK installer does not match its pinned SHA-256 ({installer.Sha256})";
            var startInfo = FunctionShell.StartInfo(
                context, bash, installDirectory, installerFile, "--version", version, "--install-dir", installDirectory, "--no-path");
            var run = await tools.RunAsync(startInfo, (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
            return run.ExitCode == 0 ? null : $"The official .NET SDK installer failed with exit code {run.ExitCode}";
        }
        finally
        {
            File.Delete(installerFile);
        }
    }

    private sealed record Pin(string Version, string ManifestDirectory);
}
