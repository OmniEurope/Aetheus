// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// What the OpenGrep SAST scan (<see cref="ScannerOperationExecutor"/>) and the
/// <c>opengrep-rule-tests</c> function share: the in-house rule files embedded in the agent, the
/// binary of <c>scanner-manifest.json</c> downloaded once into the agent's work directory and trusted
/// only at its pinned SHA-256, and the sandbox it runs in (the manifest's runtime image pinned by
/// digest, no network, read-only root, no capability).
/// </summary>
internal static class OpenGrepSandbox
{
    /// <summary>The scanner manifest key of OpenGrep.</summary>
    internal const string ScannerKey = "opengrep";

    private const string RuleResourcePrefix = "Aetheus.Agent.Core.AnalysisRules.opengrep.";

    /// <summary>Every file of <c>.aetheus/security-rules/opengrep</c>, embedded by
    /// <c>Aetheus.Agent.Core.csproj</c> under <see cref="RuleResourcePrefix"/>.</summary>
    internal static readonly IReadOnlyList<string> RuleFiles =
    [
        "aetheus-security.yml",
        "aetheus-apache.yml",
        "aetheus-compose.yml",
        "aetheus-delivery.yml",
        "aetheus-dockerfile.yml",
        "aetheus-javascript.yml",
        "aetheus-pipeline.yml"
    ];

    /// <summary>Writes every embedded rule file into <paramref name="directory"/>, created if needed.
    /// A missing resource is an <see cref="IOException"/>: a scan with part of its rules would report
    /// a clean result for the checks it silently lost.</summary>
    internal static async Task ExtractEmbeddedRulesAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var assembly = typeof(OpenGrepSandbox).Assembly;
        foreach (var file in RuleFiles)
        {
            await using var source = assembly.GetManifestResourceStream(RuleResourcePrefix + file)
                ?? throw new IOException($"Embedded OpenGrep rules resource '{file}' is missing.");
            var path = Path.Combine(directory, file);
            await using var destination = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Where the verified binary of that manifest entry is kept in the agent's work
    /// directory, shared by every scan and rule test of the same version.</summary>
    internal static string BinaryPath(string workDirectory, ScannerManifestEntry scanner) =>
        Path.Combine(workDirectory, "scanners", scanner.Key, scanner.Version, scanner.Key);

    /// <summary>The runtime image, refused unless the manifest pins it by digest.</summary>
    internal static string RequirePinnedRuntime(ScannerManifestEntry scanner)
    {
        if (string.IsNullOrWhiteSpace(scanner.RuntimeImage)
            || !scanner.RuntimeImage.Contains("@sha256:", StringComparison.Ordinal))
            throw new IOException("Binary scanner runtime image is not pinned by digest.");
        return scanner.RuntimeImage;
    }

    /// <summary>
    /// Leaves at <paramref name="binaryPath"/> a file whose bytes match the manifest's SHA-256: kept
    /// when already there, otherwise downloaded by <paramref name="downloadTo"/> into a temporary file
    /// next to it, verified, made owner-executable and moved into place. A mismatch is an
    /// <see cref="IOException"/> and leaves nothing behind.
    /// </summary>
    internal static async Task EnsureBinaryAsync(
        ScannerManifestEntry scanner,
        string binaryPath,
        Func<Uri, string, CancellationToken, Task> downloadTo,
        CancellationToken cancellationToken)
    {
        var downloadUri = ScannerOperationExecutor.RequireVerifiedDownload(scanner);
        var expected = scanner.Sha256LinuxAmd64!;
        if (File.Exists(binaryPath)
            && await ScannerOperationSupport.HasExpectedHashAsync(binaryPath, expected, cancellationToken).ConfigureAwait(false))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
        var tempPath = binaryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await downloadTo(downloadUri, tempPath, cancellationToken).ConfigureAwait(false);
            if (!await ScannerOperationSupport.HasExpectedHashAsync(tempPath, expected, cancellationToken).ConfigureAwait(false))
                throw new IOException("Downloaded scanner binary failed SHA-256 verification.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(tempPath, binaryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// <c>docker run</c> of the verified binary in the manifest's pinned runtime image: no network,
    /// read-only root, every capability dropped, no privilege escalation, bounded processes, memory
    /// and CPU, the agent's own identity (or <paramref name="user"/>, <c>uid:gid</c>), and only the
    /// given bind mounts.
    /// </summary>
    internal static ProcessStartInfo BuildDockerProcess(
        ScannerManifestEntry scanner,
        string binaryPath,
        IEnumerable<SandboxMount> mounts,
        IEnumerable<string> arguments,
        string? user = null)
    {
        var image = RequirePinnedRuntime(scanner);
        var psi = ScannerOperationSupport.BaseProcess("docker");
        var args = psi.ArgumentList;
        args.Add("run"); args.Add("--rm");
        args.Add("--name"); args.Add($"aetheus-scan-{Guid.NewGuid():N}");
        args.Add("--cap-drop"); args.Add("ALL");
        args.Add("--security-opt"); args.Add("no-new-privileges");
        args.Add("--pids-limit"); args.Add(ScannerContainerProcessBuilder.PidsLimit.ToString());
        args.Add("--memory"); args.Add(scanner.Memory);
        args.Add("--cpus"); args.Add(scanner.Cpus);
        args.Add("--read-only");
        args.Add("--network"); args.Add("none");
        args.Add("--tmpfs"); args.Add("/tmp:rw,noexec,nosuid,nodev,size=128m,mode=1777");
        args.Add("--tmpfs"); args.Add("/opengrep-home:rw,exec,nosuid,nodev,size=512m,mode=1777");
        args.Add("--env"); args.Add("HOME=/opengrep-home");
        if (user is null)
            ScannerContainerProcessBuilder.AddCurrentUser(args);
        else
        {
            args.Add("--user"); args.Add(user);
        }
        args.Add("--mount"); args.Add($"type=bind,src={binaryPath},dst=/scanner/opengrep,readonly");
        foreach (var mount in mounts)
        {
            args.Add("--mount");
            args.Add($"type=bind,src={mount.Source},dst={mount.Target}" + (mount.ReadOnly ? ",readonly" : string.Empty));
        }
        args.Add("--entrypoint"); args.Add("/scanner/opengrep");
        args.Add("--"); args.Add(image);
        foreach (var argument in arguments)
            args.Add(argument);
        return psi;
    }
}

/// <summary>One bind mount of the OpenGrep sandbox.</summary>
internal sealed record SandboxMount(string Source, string Target, bool ReadOnly);
