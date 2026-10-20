// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Executes the installed, assembly-embedded observability promotion harness. Candidate files are
/// read from the checkout, but neither the signing/publishing code nor its path comes from it.
/// Each package is published under the version its own candidate attests (PLAN-002 C8), so no
/// set-wide version is required.
/// </summary>
public sealed class ObservabilityPackageOperationExecutor(
    ILogger<ObservabilityPackageOperationExecutor> logger,
    IOptions<AetheusAgentOptions> options) : EnvironmentOperationExecutor
{
    private const string PromotionResource =
        "Aetheus.Agent.Core.PublicationHarnesses.promote-observability-packages.sh";
    private const string PublishResource =
        "Aetheus.Agent.Core.PublicationHarnesses.publish-observability-package.sh";
    private static readonly string[] RequiredEnvironment =
    [
        "AETHEUS_WORKING_DIR",
        "AETHEUS_NUGET_SIGNING_PFX_BASE64",
        "AETHEUS_NUGET_SIGNING_PFX_PASSWORD",
        "AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT",
        "AETHEUS_PACKAGE_TOKEN"
    ];

    // A run definition from before PLAN-002 C8 carries one PACKAGE_VERSION for the whole set. For one
    // cycle it is still forwarded, and the harness then requires every candidate to carry exactly it.
    private const string LegacySetVersion = "PACKAGE_VERSION";

    // The harness runner, replaceable so the extracted scripts and the start info are asserted without a shell.
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = RunAsync;

    public override bool CanHandle(OperationKind kind) =>
        kind == OperationKind.PipelinePublishObservabilityBundle;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (kind != OperationKind.PipelinePublishObservabilityBundle
            || target != "optional-observability"
            || !HasRequiredEnvironment(envVars)
            || !SigningCertificateMatchesExpectedFingerprint(envVars))
            return new ExecutorResult(1, false);

        var harnessDirectory = Path.Combine(Path.GetTempPath(), "aetheus-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(harnessDirectory);
        try
        {
            var promotionPath = await ExtractResourceAsync(
                PromotionResource, Path.Combine(harnessDirectory, "promote.sh"), cancellationToken).ConfigureAwait(false);
            var publishPath = await ExtractResourceAsync(
                PublishResource, Path.Combine(harnessDirectory, "publish.sh"), cancellationToken).ConfigureAwait(false);
            var startInfo = BuildStartInfo(
                promotionPath,
                publishPath,
                options.Value.ServerUrl.TrimEnd('/'),
                envVars);
            return await RunProcessAsync(startInfo, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(harnessDirectory, recursive: true); }
            catch (Exception exception) { logger.LogWarning(exception, "Failed to remove publication harness directory"); }
        }
    }

    internal static ProcessStartInfo BuildStartInfo(
        string promotionPath,
        string publishPath,
        string trustedPackageBaseUrl,
        IReadOnlyDictionary<string, string> envVars)
    {
        var startInfo = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(promotionPath);
        foreach (var key in RequiredEnvironment)
            if (envVars.TryGetValue(key, out var value))
                startInfo.Environment[key] = value;
        startInfo.Environment.Remove(LegacySetVersion);
        if (envVars.TryGetValue(LegacySetVersion, out var legacySetVersion)
            && !string.IsNullOrWhiteSpace(legacySetVersion))
            startInfo.Environment[LegacySetVersion] = legacySetVersion;
        startInfo.Environment.Remove("DOTNET");
        startInfo.Environment["AETHEUS_PACKAGE_BASE_URL"] = trustedPackageBaseUrl;
        startInfo.Environment["AETHEUS_TRUSTED_PUBLISH_SCRIPT"] = publishPath;
        return startInfo;
    }

    internal static bool HasRequiredEnvironment(IReadOnlyDictionary<string, string> envVars) =>
        RequiredEnvironment.All(
            key => envVars.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));

    internal static bool SigningCertificateMatchesExpectedFingerprint(
        IReadOnlyDictionary<string, string> envVars)
    {
        try
        {
            var expected = Convert.FromHexString(
                envVars["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"]);
            if (expected.Length != 32)
                return false;

            var pfx = Convert.FromBase64String(envVars["AETHEUS_NUGET_SIGNING_PFX_BASE64"]);
            using var certificate = X509CertificateLoader.LoadPkcs12(
                pfx,
                envVars["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"],
                X509KeyStorageFlags.EphemeralKeySet);
            return certificate.HasPrivateKey
                && CryptographicOperations.FixedTimeEquals(
                    expected,
                    certificate.GetCertHash(HashAlgorithmName.SHA256));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static async Task<string> ExtractResourceAsync(
        string resourceName,
        string destination,
        CancellationToken ct)
    {
        await using var resource = typeof(ObservabilityPackageOperationExecutor).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded publication harness '{resourceName}' is unavailable.");
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await resource.CopyToAsync(output, ct).ConfigureAwait(false);
        return destination;
    }

    private static async Task<ExecutorResult> RunAsync(
        ProcessStartInfo startInfo,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 7200)));
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = StreamAsync(process.StandardOutput, TaskLogLevel.Info, onOutput, timeout.Token);
        var stderr = StreamAsync(process.StandardError, TaskLogLevel.Error, onOutput, timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new ExecutorResult(process.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // the process already exited: nothing left to kill
            if (ct.IsCancellationRequested) throw;
            return new ExecutorResult(-1, true);
        }
    }

    private static async Task StreamAsync(
        StreamReader reader,
        TaskLogLevel level,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            await onOutput(line, level).ConfigureAwait(false);
    }
}
