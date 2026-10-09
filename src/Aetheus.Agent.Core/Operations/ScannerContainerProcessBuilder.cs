// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Builds the hardened Docker invocation for manifest-backed scanners and materializes trusted
/// scanner harnesses from the agent assembly, outside the untrusted source workspace.
/// </summary>
internal static class ScannerContainerProcessBuilder
{
    internal const int PidsLimit = 1024;
    private const string ZapActiveHarnessResource =
        "Aetheus.Agent.Core.AnalysisHarnesses.zap-active-automation.sh";
    private const string ZapActiveHarnessContainerPath =
        "/aetheus/harness/zap-active-automation.sh";

    internal static void AddCurrentUser(ICollection<string> arguments)
    {
        arguments.Add("--user");
        arguments.Add($"{AgentUserIdentity.Uid}:{AgentUserIdentity.Gid}");
    }

    internal static ProcessStartInfo Build(
        ScannerManifestEntry scanner,
        string sourceDirectory,
        string outputDirectory,
        string? cacheDirectory,
        string? trustedHarnessPath,
        IReadOnlyDictionary<string, string> envVars,
        RestrictedScannerEgress? egress,
        bool skipDatabaseUpdate = false)
    {
        if (string.IsNullOrWhiteSpace(scanner.Image) || !scanner.Image.Contains("@sha256:", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(scanner.EntryPoint))
            throw new IOException("Container scanner manifest entry is incomplete.");
        var containerOutputDirectory = string.IsNullOrWhiteSpace(scanner.ContainerOutputDirectory)
            ? "/out"
            : scanner.ContainerOutputDirectory;
        var psi = CreateProcess("docker");
        var args = psi.ArgumentList;
        AddRuntimeOptions(args, scanner, egress);
        AddEnvironment(args, scanner);
        if (!OperatingSystem.IsWindows())
            AddCurrentUser(args);
        AddMounts(args, sourceDirectory, outputDirectory, containerOutputDirectory, trustedHarnessPath, cacheDirectory);
        var resolvedArguments = ResolveScannerArguments(scanner, envVars, containerOutputDirectory);
        ZapEgressConfigurator.Configure(scanner, resolvedArguments, egress);
        // Right after the subcommand (`fs`, `image`): a Trivy flag, decided by TrivyDatabaseFreshness.
        if (skipDatabaseUpdate && TrivyDatabaseFreshness.Applies(scanner) && resolvedArguments.Count > 0)
            resolvedArguments.Insert(1, TrivyDatabaseFreshness.SkipUpdateArgument);
        if (JscpdRepositoryConfiguration.Applies(scanner))
            resolvedArguments.AddRange(JscpdRepositoryConfiguration.AdditionalArguments(sourceDirectory));
        AddEntrypoint(args, scanner);
        foreach (var argument in resolvedArguments) args.Add(argument);
        return psi;
    }

    private static void AddRuntimeOptions(
        ICollection<string> arguments,
        ScannerManifestEntry scanner,
        RestrictedScannerEgress? egress)
    {
        AddOption(arguments, "run", "--rm");
        AddOption(arguments, "--name", $"aetheus-scan-{Guid.NewGuid():N}");
        AddOption(arguments, "--cap-drop", "ALL");
        AddOption(arguments, "--security-opt", "no-new-privileges");
        AddOption(arguments, "--pids-limit", PidsLimit.ToString());
        AddOption(arguments, "--memory", scanner.Memory);
        AddOption(arguments, "--cpus", scanner.Cpus);
        arguments.Add("--read-only");
        AddOption(arguments, "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size={scanner.MaxTemporaryBytes ?? 268_435_456},mode=1777");
        if (!string.IsNullOrWhiteSpace(scanner.TemporaryHomeDirectory))
            AddTemporaryHomeOptions(arguments, scanner);
        var workingDirectory = scanner.ContainerWorkingDirectory ?? scanner.TemporaryHomeDirectory;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            AddOption(arguments, "--workdir", workingDirectory);
        AddOption(arguments, "--network", egress?.NetworkName ?? "none");
        if (egress is not null)
            AddRestrictedEgressEnvironment(arguments, egress.ProxyUrl);
    }

    private static void AddTemporaryHomeOptions(ICollection<string> arguments, ScannerManifestEntry scanner)
    {
        AddOption(arguments, "--tmpfs",
            $"{scanner.TemporaryHomeDirectory}:rw,exec,nosuid,nodev,size={scanner.MaxTemporaryHomeBytes},mode=1777");
        AddOption(arguments, "--env", $"HOME={scanner.HomeDirectory ?? scanner.TemporaryHomeDirectory}");
    }

    private static void AddEnvironment(ICollection<string> arguments, ScannerManifestEntry scanner)
    {
        foreach (var variable in (scanner.Environment ?? []).OrderBy(item => item.Key, StringComparer.Ordinal))
            AddOption(arguments, "--env", $"{variable.Key}={variable.Value}");
    }

    private static void AddMounts(
        ICollection<string> arguments,
        string sourceDirectory,
        string outputDirectory,
        string containerOutputDirectory,
        string? trustedHarnessPath,
        string? cacheDirectory)
    {
        AddOption(arguments, "--mount", $"type=bind,src={sourceDirectory},dst=/src,readonly");
        AddOption(arguments, "--mount", $"type=bind,src={outputDirectory},dst={containerOutputDirectory}");
        if (trustedHarnessPath is not null)
            AddOption(arguments, "--mount",
                $"type=bind,src={trustedHarnessPath},dst={ZapActiveHarnessContainerPath},readonly");
        if (cacheDirectory is not null)
            AddOption(arguments, "--mount", $"type=bind,src={cacheDirectory},dst=/cache");
    }

    private static List<string> ResolveScannerArguments(
        ScannerManifestEntry scanner,
        IReadOnlyDictionary<string, string> envVars,
        string containerOutputDirectory)
    {
        var resolvedArguments = ScannerOperationValidator.ResolveArguments(
            scanner.Arguments,
            envVars,
            "/src",
            containerOutputDirectory.TrimEnd('/') + "/" + scanner.ReportPath,
            "/src/.aetheus/security-rules/opengrep",
            containerOutputDirectory);
        if (string.Equals(scanner.Key, "gitleaks-history", StringComparison.OrdinalIgnoreCase)
            && envVars.TryGetValue("AETHEUS_GITLEAKS_LOG_OPTIONS", out var logOptions)
            && !string.IsNullOrWhiteSpace(logOptions))
        {
            resolvedArguments.Add("--log-opts");
            resolvedArguments.Add(logOptions);
        }
        return resolvedArguments;
    }

    private static void AddEntrypoint(ICollection<string> args, ScannerManifestEntry scanner)
    {
        if (!string.IsNullOrWhiteSpace(scanner.SeedTemporaryHomeFrom))
        {
            args.Add("--entrypoint"); args.Add("/bin/sh");
            args.Add("--"); args.Add(scanner.Image!);
            args.Add("-ceu");
            args.Add("mkdir -p -- \"$2\"; cp -R -- \"$1\"/. \"$2\"/; shift 2; exec \"$@\"");
            args.Add("aetheus-scanner-init");
            args.Add(scanner.SeedTemporaryHomeFrom!);
            args.Add(scanner.SeedTemporaryHomeTarget ?? scanner.TemporaryHomeDirectory!);
            args.Add(scanner.EntryPoint!);
        }
        else
        {
            args.Add("--entrypoint"); args.Add(scanner.EntryPoint!);
            args.Add("--"); args.Add(scanner.Image!);
        }
    }

    private static void AddOption(ICollection<string> arguments, string name, string value)
    {
        arguments.Add(name);
        arguments.Add(value);
    }

    internal static void AddRestrictedEgressEnvironment(ICollection<string> arguments, string proxyUrl)
    {
        foreach (var variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
        {
            arguments.Add("--env");
            arguments.Add($"{variable}={proxyUrl}");
        }

        // ZAP's Python launchers control the in-container daemon through localhost. That control
        // traffic must not enter the audited egress proxy; the QA target remains 127.0.0.1 and is
        // therefore still forced through the allowlisted proxy to reach the host-side ephemeral app.
        arguments.Add("--env");
        arguments.Add("NO_PROXY=localhost");
        arguments.Add("--env");
        arguments.Add("no_proxy=localhost");
    }

    internal static async Task<string?> ExtractEmbeddedHarnessAsync(
        ScannerManifestEntry scanner,
        string scanRoot,
        CancellationToken ct)
    {
        if (!string.Equals(scanner.Key, "zap-active", StringComparison.Ordinal))
            return null;

        var harnessDirectory = Path.Combine(scanRoot, "harness");
        Directory.CreateDirectory(harnessDirectory);
        ScannerFileSecurity.TrySetOwnerOnly(harnessDirectory);
        var harnessPath = Path.Combine(harnessDirectory, "zap-active-automation.sh");
        await using var resource = typeof(ScannerContainerProcessBuilder).Assembly
            .GetManifestResourceStream(ZapActiveHarnessResource)
            ?? throw new IOException("The immutable ZAP active harness is missing from the agent assembly.");
        await using var destination = new FileStream(
            harnessPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await resource.CopyToAsync(destination, ct).ConfigureAwait(false);
        ScannerFileSecurity.TrySetOwnerOnly(harnessPath);
        return harnessPath;
    }

    private static ProcessStartInfo CreateProcess(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
}
