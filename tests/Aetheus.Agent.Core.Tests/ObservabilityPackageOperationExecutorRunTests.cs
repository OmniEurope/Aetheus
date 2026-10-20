// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The observability publication run: the pinned signing certificate gates it, the embedded harness
/// is extracted for the run and removed after it, and the harness is started with the trusted environment.
/// The shell itself is injected, so none of this needs <c>sh</c>.</summary>
public sealed class ObservabilityPackageOperationExecutorRunTests
{
    private const string Target = "optional-observability";

    private sealed record HarnessCall(
        string[] Argv, IReadOnlyDictionary<string, string?> Environment, int TimeoutSeconds,
        bool PromotionExisted, bool PublishExisted, long PromotionLength, string HarnessDirectory);

    private static ObservabilityPackageOperationExecutor Build(
        Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>> runner) =>
        new(
            NullLogger<ObservabilityPackageOperationExecutor>.Instance,
            Options.Create(new AetheusAgentOptions { ServerUrl = "https://packages.example/" }))
        {
            RunProcessAsync = runner
        };

    private static Dictionary<string, string> SignedEnvironment()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Aetheus Package Signing Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        const string password = "test-only-password";
        return new Dictionary<string, string>
        {
            ["AETHEUS_WORKING_DIR"] = "/work/candidate",
            ["AETHEUS_NUGET_SIGNING_PFX_BASE64"] = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password)),
            ["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] = password,
            ["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            ["AETHEUS_PACKAGE_TOKEN"] = "token"
        };
    }

    private static Task<ExecutorResult> Execute(
        ObservabilityPackageOperationExecutor executor, Dictionary<string, string> environment,
        OperationKind kind = OperationKind.PipelinePublishObservabilityBundle, string target = Target, int timeout = 600) =>
        executor.ExecuteAsync(kind, target, environment, timeout, new OutputLog().Write, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AValidRun_ExtractsTheEmbeddedHarness_StartsItWithTheTrustedEnvironment_AndRemovesItAfterwards()
    {
        HarnessCall? call = null;
        var executor = Build((startInfo, timeout, _, _) =>
        {
            var promotion = startInfo.ArgumentList[0];
            var publish = startInfo.Environment["AETHEUS_TRUSTED_PUBLISH_SCRIPT"]!;
            call = new HarnessCall(
                [.. startInfo.ArgumentList],
                startInfo.Environment.ToDictionary(e => e.Key, e => e.Value),
                timeout,
                File.Exists(promotion),
                File.Exists(publish),
                new FileInfo(promotion).Length,
                Path.GetDirectoryName(promotion)!);
            return Task.FromResult(new ExecutorResult(5, false));
        });

        var result = await Execute(executor, SignedEnvironment());

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(call);
        Assert.True(call.PromotionExisted);
        Assert.True(call.PublishExisted);
        Assert.True(call.PromotionLength > 0);
        Assert.Equal(600, call.TimeoutSeconds);
        Assert.EndsWith("promote.sh", Assert.Single(call.Argv), StringComparison.Ordinal);
        Assert.Equal("https://packages.example", call.Environment["AETHEUS_PACKAGE_BASE_URL"]);
        Assert.Equal("/work/candidate", call.Environment["AETHEUS_WORKING_DIR"]);
        Assert.False(Directory.Exists(call.HarnessDirectory));
    }

    [Fact]
    public async Task ARunnerThatThrows_StillRemovesTheExtractedHarness()
    {
        string? harnessDirectory = null;
        var executor = Build((startInfo, _, _, _) =>
        {
            harnessDirectory = Path.GetDirectoryName(startInfo.ArgumentList[0]);
            throw new InvalidOperationException("sh is gone");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(executor, SignedEnvironment()));

        Assert.NotNull(harnessDirectory);
        Assert.False(Directory.Exists(harnessDirectory));
    }

    [Fact]
    public async Task ATimedOutHarness_IsReturnedAsIs()
    {
        var executor = Build((_, _, _, _) => Task.FromResult(new ExecutorResult(-1, true)));

        var result = await Execute(executor, SignedEnvironment());

        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.TimedOut);
    }

    public static TheoryData<string> Refusals => new()
    {
        "kind",
        "target",
        "missing-environment",
        "wrong-fingerprint"
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARunThatIsNotTrusted_FailsWithExitCodeOne_WithoutStartingTheHarness(string reason)
    {
        var started = 0;
        var executor = Build((_, _, _, _) =>
        {
            started++;
            return Task.FromResult(new ExecutorResult(0, false));
        });
        var environment = SignedEnvironment();
        var kind = OperationKind.PipelinePublishObservabilityBundle;
        var target = Target;
        switch (reason)
        {
            case "kind": kind = OperationKind.ServiceStart; break;
            case "target": target = "something-else"; break;
            case "missing-environment": environment.Remove("AETHEUS_PACKAGE_TOKEN"); break;
            default: environment["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = new string('0', 64); break;
        }

        var result = await Execute(executor, environment, kind, target);

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(0, started);
    }
}
