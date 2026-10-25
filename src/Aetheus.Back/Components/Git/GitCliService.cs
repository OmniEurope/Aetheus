// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Git;

public sealed partial class GitCliService(ILogger<GitCliService> logger, GitProcessRunner runner) : IGitCliService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Strips userinfo (user:token@) from any scheme://… URL so a credential-bearing repository URL
    // (or a git stderr line echoing it) never reaches the log file. NEVER log secret values.
    [GeneratedRegex(@"://[^@/\s]+@")]
    private static partial Regex UserInfoRegex();

    private static string StripCredentials(string text) => UserInfoRegex().Replace(text, "://");

    public async Task<GitRemoteBranch> ResolveBranchCommitAsync(string repositoryUrl, string? branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        if (!IsRepositoryUrlAllowed(repositoryUrl))
            throw new BadRequestException("The external repository must use a public HTTPS URL without credentials.");

        var pinnedEndpoint = await ResolveSafeEndpointAsync(repositoryUrl, ct).ConfigureAwait(false);
        if (pinnedEndpoint is null)
            throw new BadRequestException("The external repository host does not resolve to a public address.");

        return await RunLsRemoteAsync(BuildResolveBranchStartInfo(repositoryUrl, branch, pinnedEndpoint.Value), branch, Timeout, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Runs the <c>ls-remote</c> and reads the branch it names, or refuses: no answer within
    /// <paramref name="timeout"/>, a failed run, or no such branch.</summary>
    internal static async Task<GitRemoteBranch> RunLsRemoteAsync(
        ProcessStartInfo startInfo, string? branch, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            var resolved = ParseLsRemote(await outputTask.ConfigureAwait(false), branch);
            if (process.ExitCode == 0 && resolved is not null) return resolved;
        }
        catch (OperationCanceledException)
        {
            SafeKill(process);
            ct.ThrowIfCancellationRequested();
            throw new BadRequestException("The external repository did not respond before the timeout.");
        }

        throw new BadRequestException("The external repository or selected branch could not be reached.");
    }

    /// <summary><c>git ls-remote</c> of one branch, or of the default HEAD when none is named, pinned to
    /// the validated address, never following a redirect and never prompting.</summary>
    internal static ProcessStartInfo BuildResolveBranchStartInfo(
        string repositoryUrl, string? branch, (string Host, int Port, string Address) endpoint)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var defaultHead = string.IsNullOrWhiteSpace(branch);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("protocol.file.allow=never");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"http.curloptResolve={endpoint.Host}:{endpoint.Port}:{endpoint.Address}");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("http.followRedirects=false");
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add("--exit-code");
        psi.ArgumentList.Add(defaultHead ? "--symref" : "--heads");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(repositoryUrl);
        psi.ArgumentList.Add(defaultHead ? "HEAD" : $"refs/heads/{branch}");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        return psi;
    }

    /// <summary>The branch and commit an <c>ls-remote</c> answer names: the default HEAD when no branch
    /// was asked, otherwise the asked branch with its full hash.</summary>
    internal static GitRemoteBranch? ParseLsRemote(string output, string? branch) =>
        string.IsNullOrWhiteSpace(branch)
            ? ParseDefaultHead(output)
            : ParseBranchCommit(output, branch) is { } commit
                ? new GitRemoteBranch(branch, commit)
                : null;

    internal static string? ParseBranchCommit(string output, string branch)
    {
        var expectedRef = $"refs/heads/{branch}";
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[1] == expectedRef
                && parts[0] is { Length: 40 or 64 } && parts[0].All(Uri.IsHexDigit))
                return parts[0];
        }
        return null;
    }

    public async Task<string?> ReadPipelineYamlAsync(string repositoryUrl, string commit, string pipelineName, CancellationToken ct = default)
    {
        if (!IsRepositoryUrlAllowed(repositoryUrl) || !IsFullCommitHash(commit))
            throw new BadRequestException("A public HTTPS repository and a full commit hash are required.");
        var endpoint = await ResolveSafeEndpointAsync(repositoryUrl, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("The external repository host does not resolve to a public address.");
        return await ReadPipelineYamlAtCommitAsync(repositoryUrl, commit, pipelineName, endpoint, ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadPipelineYamlAtCommitAsync(
        string repositoryUrl, string commit, string pipelineName, (string Host, int Port, string Address) endpoint, CancellationToken ct)
    {
        var directory = Directory.CreateTempSubdirectory("aetheus-pipeline-").FullName;
        try
        {
            await FetchCommitAsync(directory, repositoryUrl, commit, endpoint, ct).ConfigureAwait(false);
            return await ReadPipelineYamlFromSnapshotAsync(directory, commit, pipelineName, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteSnapshot(directory);
        }
    }

    private static bool IsFullCommitHash(string commit) => commit.Length is 40 or 64 && commit.All(Uri.IsHexDigit);

    // A bare repository holding only the one commit, fetched through the pinned address.
    private async Task FetchCommitAsync(
        string directory, string repositoryUrl, string commit, (string Host, int Port, string Address) endpoint, CancellationToken ct)
    {
        var init = await runner.RunGitAsync(directory, ["init", "--bare"], ct).ConfigureAwait(false);
        if (init.ExitCode != 0) throw new BadRequestException("Could not prepare the external repository read.");
        var fetch = await runner.RunGitBoundedAsync(directory,
            ["-c", "protocol.file.allow=never", "-c", "http.followRedirects=false",
             "-c", $"http.curloptResolve={endpoint.Host}:{endpoint.Port}:{endpoint.Address}",
             "-c", "credential.helper=", "-c", "credential.interactive=false",
             "fetch", "--depth=1", "--no-tags", "--no-recurse-submodules", "--", repositoryUrl, commit],
            4096, ct, Timeout, ignoreExitCode: true).ConfigureAwait(false);
        if (fetch.ExitCode != 0) throw new BadRequestException("Could not read the selected external repository commit.");
    }

    private static void DeleteSnapshot(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }

    internal async Task<string?> ReadPipelineYamlFromSnapshotAsync(string directory, string commit, string pipelineName, CancellationToken ct)
    {
        var tree = await runner.RunGitBoundedAsync(directory,
            ["ls-tree", "--name-only", "-r", commit, "--", ".pipeline"], 65536, ct).ConfigureAwait(false);
        if (tree.ExitCode != 0 || tree.OutputTruncated)
            throw new BadRequestException("Could not enumerate the pipeline definitions at the selected commit.");
        var preferred = $".pipeline/{PipelineGitPathPolicy.Slugify(pipelineName)}.yaml";
        foreach (var path in tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path == preferred ? 0 : 1))
        {
            var blob = await runner.RunGitBoundedAsync(directory, ["show", $"{commit}:{path}"], 256 * 1024, ct).ConfigureAwait(false);
            if (blob.ExitCode != 0 || blob.OutputTruncated)
                throw new BadRequestException("An external pipeline definition could not be read or exceeds 256 Ko.");
            var definition = YamlParsingHelper.ParseAndValidate(blob.Output, logger);
            var name = string.IsNullOrWhiteSpace(definition?.Name) ? Path.GetFileNameWithoutExtension(path) : definition.Name;
            if (string.Equals(name, pipelineName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, PipelineGitPathPolicy.Slugify(pipelineName), StringComparison.OrdinalIgnoreCase))
                return blob.Output;
        }
        return null;
    }

    internal static GitRemoteBranch? ParseDefaultHead(string output)
    {
        const string prefix = "ref: refs/heads/";
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reference = lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal)
            && line.EndsWith("\tHEAD", StringComparison.Ordinal));
        var commit = lines.FirstOrDefault(line => line.EndsWith("\tHEAD", StringComparison.Ordinal)
            && line is { Length: 45 or 69 });
        if (reference is null || commit is null) return null;
        var branch = reference[prefix.Length..^"\tHEAD".Length];
        var hash = commit[..^"\tHEAD".Length];
        return branch.Length > 0 && hash.All(Uri.IsHexDigit)
            ? new GitRemoteBranch(branch, hash)
            : null;
    }

    public async Task<List<(string BranchName, string Version)>> ListReleaseBranchesAsync(string repositoryUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        // Hardening (High #12): only allow http(s)/ssh schemes. Reject anything that could be
        // interpreted as a git option (`-c …`, `--upload-pack=…`) or as a `file://` / loopback
        // SSRF target.
        if (!IsRepositoryUrlAllowed(repositoryUrl))
        {
            logger.LogWarning("Rejected disallowed repository URL");
            return [];
        }

        var pinnedEndpoint = await ResolveSafeEndpointAsync(repositoryUrl, ct).ConfigureAwait(false);
        if (pinnedEndpoint is null)
        {
            logger.LogWarning("Rejected repository URL whose DNS resolved to a forbidden or unavailable address");
            return [];
        }

        return await RunListReleaseBranchesAsync(
            BuildListReleaseBranchesStartInfo(repositoryUrl, pinnedEndpoint.Value), repositoryUrl, Timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the <c>ls-remote</c> of the release branches and parses its answer, or logs and
    /// returns nothing: no answer within <paramref name="timeout"/> (the process is killed) or a failed run.
    /// A cancellation by the caller propagates.</summary>
    internal async Task<List<(string BranchName, string Version)>> RunListReleaseBranchesAsync(
        ProcessStartInfo psi, string repositoryUrl, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        string output;
        string error;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            error = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SafeKill(process);
            logger.LogWarning("git ls-remote timed out after {Timeout}s for {Url}", timeout.TotalSeconds, StripCredentials(repositoryUrl));
            return [];
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("git ls-remote failed (exit {Code}) for {Url}: {Error}", process.ExitCode, StripCredentials(repositoryUrl), StripCredentials(error));
            return [];
        }

        return ParseOutput(output);
    }

    private static List<(string BranchName, string Version)> ParseOutput(string output)
    {
        var results = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(output))
            return results;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Format: "<hash>\trefs/heads/release/<version>"
            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0) continue;

            var refName = line[(tabIndex + 1)..];
            const string prefix = "refs/heads/";
            if (!refName.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var branchName = refName[prefix.Length..]; // "release/v1.2.0"

            const string releasePrefix = "release/";
            if (!branchName.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var version = branchName[releasePrefix.Length..];
            // Strip leading 'v' or 'V'
            if (version.Length > 0 && (version[0] == 'v' || version[0] == 'V'))
                version = version[1..];

            if (!string.IsNullOrWhiteSpace(version))
                results.Add((branchName, version));
        }

        return results;
    }

    private static void SafeKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
        catch (NotSupportedException) { /* not supported on this platform */ }
    }

    private static bool IsRepositoryUrlAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        // External Git operations are HTTPS-only. SSH cannot enforce the same transport policy and
        // a plain HTTP clone would expose credentials and source in transit.
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "https") return false;

        // Block obvious loopback / link-local / private targets at URL level. The agent that
        // ultimately resolves DNS may have additional protections; this is a first cheap pass.
        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.StartsWith("127.", StringComparison.Ordinal)) return false;
        if (host == "::1") return false;

        // Reject literal private/link-local IPs (RFC-1918, 169.254/16 incl. cloud metadata 169.254.169.254,
        // and IPv6 unique-local fc00::/7 / link-local fe80::/10). Host may be bracketed for IPv6 - Uri.Host
        // already strips the brackets.
        if (IPAddress.TryParse(host, out var ip) && WebhookSsrfGuard.IsForbiddenAddress(ip)) return false;

        return true;
    }

    internal static ProcessStartInfo BuildListReleaseBranchesStartInfo(string repositoryUrl, (string Host, int Port, string Address) endpoint)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // -c protocol.allow=user-configured constrains the allowed transports for this invocation.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("protocol.file.allow=never");
        // Keep TLS/SNI validation against the original host while pinning libcurl to the exact
        // address validated above. DNS rebinding between validation and git's connect is impossible.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"http.curloptResolve={endpoint.Host}:{endpoint.Port}:{endpoint.Address}");
        // A redirect would leave the pinned public address for a target never validated.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("http.followRedirects=false");
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add("--heads");
        // `--` separator so the URL can never be parsed as an option.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(repositoryUrl);
        psi.ArgumentList.Add("refs/heads/release/*");
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        return psi;
    }

    internal static async Task<(string Host, int Port, string Address)?> ResolveSafeEndpointAsync(
        string repositoryUrl,
        CancellationToken ct)
    {
        var uri = new Uri(repositoryUrl, UriKind.Absolute);
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }

        // Reject mixed answers too: allowing one public answer while a private answer is present lets
        // address selection or retries cross the SSRF boundary.
        if (addresses.Length == 0 || addresses.Any(WebhookSsrfGuard.IsForbiddenAddress)) return null;
        var selected = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses[0];
        var address = selected.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{selected}]"
            : selected.ToString();
        return (uri.Host, uri.IsDefaultPort ? 443 : uri.Port, address);
    }

}
