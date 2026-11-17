// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of <c>scripts/ensure-dotnet-sdk.sh</c>: resolves the exact SDK <c>global.json</c> pins,
/// so an agent never changes SDK silently when its host is upgraded. The host <c>dotnet</c> is kept
/// when it resolves that exact version from the manifest's directory; otherwise the SDK is installed
/// with the official installer, pinned by commit and SHA-256, into a version-isolated, non-root cache
/// (<c>AETHEUS_DOTNET_ROOT</c>, default <c>$HOME/.aetheus/dotnet</c>). R-30: a repository without a pinned
/// version gets the SDK channel of its <c>TargetFramework</c> (LTS when none is declared) in the same cache.
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

    /// <summary>
    /// Resolves the SDK <paramref name="globalJson"/> pins. R-30: when that manifest is missing, empty or pins no
    /// version and <paramref name="fallbackRoot"/> is given (the directory, relative to the workspace, whose projects
    /// declare the <c>TargetFramework</c>), the SDK channel matching the highest target framework found there is
    /// resolved instead, the LTS channel when none is declared. Without <paramref name="fallbackRoot"/> a missing
    /// manifest stays a failure.
    /// </summary>
    public async Task<ToolchainResolution> ResolveAsync(
        PipelineFunctionContext context, string workingDirectory, string globalJson, CancellationToken cancellationToken,
        string? fallbackRoot = null)
    {
        var manifest = Path.GetFullPath(globalJson, workingDirectory);
        var lines = File.Exists(manifest) && new FileInfo(manifest).Length > 0
            ? await File.ReadAllLinesAsync(manifest, cancellationToken).ConfigureAwait(false)
            : null;
        var version = lines is null ? null : ReadVersion(lines);
        if (version is null && fallbackRoot is not null)
        {
            var root = Path.GetFullPath(fallbackRoot, workingDirectory);
            return await ResolveChannelAsync(context, Directory.Exists(root) ? root : workingDirectory, globalJson, cancellationToken)
                .ConfigureAwait(false);
        }
        if (lines is null)
            return ToolchainResolution.Failed($"Pinned SDK manifest not found: {globalJson}");
        if (version is null || !ExactVersion().IsMatch(version))
            return ToolchainResolution.Failed($"Pinned SDK version is missing or invalid in {globalJson}");
        return await ResolveTargetAsync(context, Target.Exact(version, Path.GetDirectoryName(manifest)!), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>R-30: no pinned SDK; the channel of the repository's <c>TargetFramework</c>, said on the log with why.</summary>
    private async Task<ToolchainResolution> ResolveChannelAsync(
        PipelineFunctionContext context, string root, string globalJson, CancellationToken cancellationToken)
    {
        var found = DotnetTargetFrameworkProbe.Find(root);
        var target = found is null ? Target.LongTermSupport(root) : Target.Channel(found.Channel, root);
        var why = found is null
            ? "no TargetFramework found in the repository, so the LTS channel"
            : $"{found.Moniker} declared in {found.File}";
        await context.Log($"No pinned SDK ({globalJson} is missing or pins no version): .NET SDK channel {target.ChannelName} ({why})")
            .ConfigureAwait(false);
        return await ResolveTargetAsync(context, target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolchainResolution> ResolveTargetAsync(PipelineFunctionContext context, Target target, CancellationToken cancellationToken)
    {
        var system = FunctionShell.FindOnPath(context, "dotnet");
        if (await IsUsableAsync(context, system, target, cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Found(system!);

        var home = FunctionShell.Variable(context, "HOME");
        if (home is null)
            return ToolchainResolution.Failed("HOME is required to install the pinned .NET SDK without root");
        var installRoot = FunctionShell.Variable(context, "AETHEUS_DOTNET_ROOT") ?? Path.Combine(home, ".aetheus", "dotnet");
        var installDirectory = Path.Combine(installRoot, target.DirectoryName);
        var local = Path.Combine(installDirectory, ExecutableName);
        if (await IsUsableAsync(context, local, target, cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Found(local);

        var installError = await InstallAsync(context, target, installDirectory, cancellationToken).ConfigureAwait(false);
        if (installError is not null)
            return ToolchainResolution.Failed(installError);
        return await IsUsableAsync(context, local, target, cancellationToken).ConfigureAwait(false)
            ? ToolchainResolution.Found(local)
            : ToolchainResolution.Failed(target.UnverifiedMessage);
    }

    /// <summary>The first <c>"version": "..."</c> line, as the script's <c>sed ... | head -n 1</c>.</summary>
    internal static string? ReadVersion(IEnumerable<string> lines) =>
        lines.Select(line => VersionLine().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value;

    /// <summary>The script's <c>[ -x "$dotnet" ] &amp;&amp; resolves_exact_sdk "$dotnet"</c>: the
    /// executable reports exactly the pinned version when run from the manifest's directory.</summary>
    private async Task<bool> IsUsableAsync(PipelineFunctionContext context, string? dotnet, Target target, CancellationToken cancellationToken)
    {
        if (dotnet is null || !FunctionShell.IsExecutable(dotnet))
            return false;
        var reported = await FunctionShell
            .CaptureAsync(tools, FunctionShell.StartInfo(context, dotnet, target.WorkingDirectory, "--version"), cancellationToken)
            .ConfigureAwait(false);
        return target.Accepts(reported);
    }

    /// <summary>Runs the pinned official installer. Returns null once it exited cleanly, the reason
    /// otherwise.</summary>
    private async Task<string?> InstallAsync(
        PipelineFunctionContext context, Target target, string installDirectory, CancellationToken cancellationToken)
    {
        var bash = FunctionShell.FindOnPath(context, "bash");
        if (bash is null)
            return "bash is required to run the official .NET SDK installer";

        Directory.CreateDirectory(installDirectory);
        var installerFile = Path.GetTempFileName();
        try
        {
            await context.Log($"Installing {target.Description} in {installDirectory}").ConfigureAwait(false);
            await tools.DownloadAsync(installer.Source, installerFile, cancellationToken).ConfigureAwait(false);
            if (!await FunctionShell.HasSha256Async(installerFile, installer.Sha256, cancellationToken).ConfigureAwait(false))
                return $"The downloaded .NET SDK installer does not match its pinned SHA-256 ({installer.Sha256})";
            var startInfo = FunctionShell.StartInfo(
                context, bash, installDirectory, [installerFile, .. target.InstallerSelector, "--install-dir", installDirectory, "--no-path"]);
            var run = await tools.RunAsync(startInfo, (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
            return run.ExitCode == 0 ? null : $"The official .NET SDK installer failed with exit code {run.ExitCode}";
        }
        finally
        {
            File.Delete(installerFile);
        }
    }

    /// <summary>What is to be provided: the exact SDK <c>global.json</c> pins, or (R-30, no pin) a channel. The
    /// directory name keeps each target in its own cache directory.</summary>
    private sealed record Target(
        string DirectoryName, string Description, string UnverifiedMessage, string[] InstallerSelector,
        string WorkingDirectory, Func<string?, bool> Accepts, string ChannelName)
    {
        public static Target Exact(string version, string manifestDirectory) => new(
            version,
            $"pinned .NET SDK {version}",
            $"Pinned .NET SDK {version} installation could not be verified",
            ["--version", version],
            manifestDirectory,
            reported => string.Equals(reported, version, StringComparison.Ordinal),
            version);

        public static Target Channel(string channel, string root) => new(
            $"channel-{channel}",
            $".NET SDK channel {channel}",
            $".NET SDK channel {channel} installation could not be verified",
            ["--channel", channel],
            root,
            reported => reported?.StartsWith(channel + ".", StringComparison.Ordinal) == true,
            channel);

        public static Target LongTermSupport(string root) => new(
            "channel-LTS",
            ".NET SDK channel LTS",
            ".NET SDK channel LTS installation could not be verified",
            ["--channel", "LTS"],
            root,
            reported => !string.IsNullOrEmpty(reported) && char.IsAsciiDigit(reported[0]),
            "LTS");
    }
}
