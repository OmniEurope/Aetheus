// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ObservabilityPackageSignatureBehaviorTests
{
    private static readonly string ExpectedFingerprint = new('A', 64);

    public static TheoryData<string, bool> VerificationScenarios => new()
    {
        { "accepted-self-signed-author", true },
        { "wrong-fingerprint", false },
        { "unexpected-diagnostic", false },
        { "repository-signature", false },
        { "different-trust-error", false }
    };

    [Theory]
    [MemberData(nameof(VerificationScenarios))]
    public async Task PromotionHarness_AcceptsOnlyPinnedSelfSignedAuthorDiagnostics(
        string scenario,
        bool shouldSucceed)
    {
        await using var fixture = await SignatureFixture.CreateAsync();
        var result = await fixture.RunPromotionAsync(scenario);

        Assert.True(
            shouldSucceed == (result.ExitCode == 0),
            $"ExitCode={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        if (shouldSucceed)
        {
            Assert.Equal(
                2,
                result.StandardOutput.Split(
                    "Package signature matches the pinned internal self-signed author certificate.",
                    StringSplitOptions.None).Length - 1);
            Assert.Equal(6, File.ReadAllLines(fixture.PublishInvocations).Length);
        }
        else
        {
            Assert.False(File.Exists(fixture.PublishInvocations));
        }
    }

    [Theory]
    [MemberData(nameof(VerificationScenarios))]
    public async Task PublicationHarness_AcceptsOnlyPinnedSelfSignedAuthorDiagnostics(
        string scenario,
        bool shouldSucceed)
    {
        await using var fixture = await SignatureFixture.CreateAsync();
        var result = await fixture.RunPublicationAsync(scenario);

        Assert.True(
            shouldSucceed == (result.ExitCode == 0),
            $"ExitCode={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal(
            shouldSucceed,
            result.StandardOutput.Contains(
                "Package signature matches the pinned internal self-signed author certificate.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromotionHarness_PublishesEachPackageUnderItsOwnAttestedVersion()
    {
        await using var fixture = await SignatureFixture.CreateAsync();
        var result = await fixture.RunPromotionAsync("accepted-self-signed-author");

        Assert.True(result.ExitCode == 0, $"stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal(
            [
                "true:telemetry:1.2.0",
                "true:web-analytics-dotnet:1.1.0",
                "true:web-analytics-browser:0.1.0",
                "false:telemetry:1.2.0",
                "false:web-analytics-dotnet:1.1.0",
                "false:web-analytics-browser:0.1.0"
            ],
            await File.ReadAllLinesAsync(fixture.PublishInvocations, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PromotionHarness_RefusesALegacySetVersionThatACandidateDoesNotCarry()
    {
        await using var fixture = await SignatureFixture.CreateAsync();
        var result = await fixture.RunPromotionAsync("accepted-self-signed-author", legacySetVersion: "1.2.0");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("The web-analytics-dotnet candidate is 1.1.0", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Package signature matches", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.PublishInvocations));
    }

    [Fact]
    public async Task PromotionHarness_StillPublishesALegacySetWhoseCandidatesAllCarryIt()
    {
        await using var fixture = await SignatureFixture.CreateAsync(uniformVersion: "1.2.3");
        var result = await fixture.RunPromotionAsync("accepted-self-signed-author", legacySetVersion: "1.2.3");

        Assert.True(result.ExitCode == 0, $"stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.All(
            await File.ReadAllLinesAsync(fixture.PublishInvocations, TestContext.Current.CancellationToken),
            line => Assert.EndsWith(":1.2.3", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromotionHarness_RefusesACandidateWithoutAnAttestedVersion_BeforeSigning()
    {
        await using var fixture = await SignatureFixture.CreateAsync();
        await fixture.WriteProvenanceAsync("web-analytics-browser", version: null);
        var result = await fixture.RunPromotionAsync("accepted-self-signed-author");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(
            "The web-analytics-browser candidate does not attest one valid packageVersion.",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Package signature matches", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.PublishInvocations));
    }

    [Fact]
    public async Task PublicationHarness_RefusesAVersionTheCandidateDoesNotAttest()
    {
        await using var fixture = await SignatureFixture.CreateAsync();
        var result = await fixture.RunPublicationAsync("accepted-self-signed-author", packageVersion: "1.1.0");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(
            "PACKAGE_VERSION 1.1.0 is not the version the telemetry candidate attests.",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;

    private sealed class SignatureFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _fakeBin;
        private readonly string _registryPackage;
        private readonly string _shellWrapper;

        private SignatureFixture(string root, string fakeBin, string registryPackage, string shellWrapper)
        {
            _root = root;
            _fakeBin = fakeBin;
            _registryPackage = registryPackage;
            _shellWrapper = shellWrapper;
            PublishInvocations = Path.Combine(root, "publish-invocations");
        }

        public string PublishInvocations { get; }

        public string TelemetryVersion { get; private init; } = "";

        // The three packages carry distinct versions, as their own csproj/package.json declare them.
        public static async Task<SignatureFixture> CreateAsync(string? uniformVersion = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"aetheus-signature-{Guid.NewGuid():N}");
            var fakeBin = Path.Combine(root, "fake-bin");
            Directory.CreateDirectory(fakeBin);
            var telemetryVersion = uniformVersion ?? "1.2.0";
            var webAnalyticsVersion = uniformVersion ?? "1.1.0";
            var browserVersion = uniformVersion ?? "0.1.0";

            await CreateCandidateAsync(
                root, "telemetry", $"Aetheus.Telemetry.{telemetryVersion}.nupkg", telemetryVersion);
            await CreateCandidateAsync(
                root,
                "web-analytics-dotnet",
                $"Aetheus.WebAnalytics.{webAnalyticsVersion}.nupkg",
                webAnalyticsVersion);
            await CreateCandidateAsync(
                root,
                "web-analytics-browser",
                $"aetheus-web-analytics-{browserVersion}.tgz",
                browserVersion);

            var registryPackage = Path.Combine(root, "registry.nupkg");
            File.Copy(
                Path.Combine(root, ".package-candidate", "telemetry", $"Aetheus.Telemetry.{telemetryVersion}.nupkg"),
                registryPackage);

            await WriteExecutableAsync(
                Path.Combine(fakeBin, "openssl"),
                """
                #!/bin/sh
                if [ "$1" = pkcs12 ]; then
                  OUTPUT=""
                  while [ "$#" -gt 0 ]; do
                    if [ "$1" = -out ]; then shift; OUTPUT="$1"; fi
                    shift
                  done
                  : > "$OUTPUT"
                  exit 0
                fi
                if [ "$1" = x509 ]; then
                  printf 'sha256 Fingerprint=%s\n' "$FAKE_OPENSSL_FINGERPRINT"
                  exit 0
                fi
                exit 91
                """);
            await WriteExecutableAsync(Path.Combine(fakeBin, "node"), "#!/bin/sh\nexit 0\n");
            await WriteExecutableAsync(
                Path.Combine(fakeBin, "dotnet"),
                """
                #!/bin/sh
                if [ "$1" = nuget ] && [ "$2" = sign ]; then exit 0; fi
                if [ "$1" = nuget ] && [ "$2" = push ]; then exit 92; fi
                if [ "$1" = restore ]; then exit 0; fi
                if [ "$1" != nuget ] || [ "$2" != verify ]; then exit 93; fi

                FINGERPRINT="$EXPECTED_TEST_FINGERPRINT"
                SIGNATURE_TYPE=Author
                TRUST_ERROR='UntrustedRoot: self-signed certificate'
                EXTRA_CODE=""
                case "$FAKE_VERIFY_SCENARIO" in
                  accepted-self-signed-author) ;;
                  wrong-fingerprint) FINGERPRINT="BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB" ;;
                  unexpected-diagnostic) EXTRA_CODE='error: NU3000: unexpected signature diagnostic' ;;
                  repository-signature) SIGNATURE_TYPE=Repository ;;
                  different-trust-error) TRUST_ERROR='UntrustedRoot: certificate chain is incomplete' ;;
                  *) exit 94 ;;
                esac
                printf '%s\n' \
                  'warn : NU3042: The X.509 root certificate is not present in the trusted bundle.' \
                  "warn : Fingerprint (SHA-256): $FINGERPRINT" \
                  "Signature type: $SIGNATURE_TYPE" \
                  "warn : NU3018: $TRUST_ERROR"
                if [ -n "$EXTRA_CODE" ]; then printf '%s\n' "$EXTRA_CODE"; fi
                printf '%s\n' "error: NU3018: The author primary signature's signing certificate is not trusted by the trust provider."
                exit 1
                """);
            await WriteExecutableAsync(
                Path.Combine(fakeBin, "curl"),
                """
                #!/bin/sh
                DESTINATION=""
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output ]; then shift; DESTINATION="$1"; fi
                  shift
                done
                cp "$FAKE_REGISTRY_PACKAGE" "$DESTINATION"
                printf 200
                """);
            await WriteExecutableAsync(
                Path.Combine(fakeBin, "publish"),
                """
                #!/bin/sh
                printf '%s:%s:%s\n' "${AETHEUS_PUBLISH_PREFLIGHT_ONLY:-false}" "$1" "${PACKAGE_VERSION:-}" \
                  >> "$FAKE_PUBLISH_INVOCATIONS"
                """);

            var shellWrapper = Path.Combine(fakeBin, "run-script");
            await WriteExecutableAsync(
                shellWrapper,
                """
                #!/bin/sh
                PATH="$FAKE_BIN:$PATH"
                export PATH
                exec /bin/sh "$TARGET_SCRIPT" "$@"
                """);

            return new SignatureFixture(root, fakeBin, registryPackage, shellWrapper)
            {
                TelemetryVersion = telemetryVersion
            };
        }

        public Task WriteProvenanceAsync(string kind, string? version) =>
            WriteProvenanceAsync(Path.Combine(_root, ".package-candidate", kind), kind, version);

        public Task<ProcessResult> RunPromotionAsync(string scenario, string? legacySetVersion = null)
        {
            var startInfo = CreateStartInfo(Path.Combine(
                FindRepoRoot(), "deploy", "scripts", "promote-observability-packages.sh"));
            ConfigureCommonEnvironment(startInfo, scenario);
            if (legacySetVersion is not null)
                startInfo.Environment["PACKAGE_VERSION"] = legacySetVersion;
            startInfo.Environment["AETHEUS_WORKING_DIR"] = ToShellPath(_root);
            startInfo.Environment["AETHEUS_NUGET_SIGNING_PFX_BASE64"] = "cGZ4";
            startInfo.Environment["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] = "fixture-password";
            startInfo.Environment["AETHEUS_TRUSTED_PUBLISH_SCRIPT"] = ToShellPath(
                Path.Combine(_fakeBin, "publish"));
            startInfo.Environment["FAKE_PUBLISH_INVOCATIONS"] = ToShellPath(PublishInvocations);
            return RunAsync(startInfo);
        }

        public Task<ProcessResult> RunPublicationAsync(string scenario, string? packageVersion = null)
        {
            var startInfo = CreateStartInfo(Path.Combine(
                FindRepoRoot(), "deploy", "scripts", "publish-observability-package.sh"));
            startInfo.ArgumentList.Add("telemetry");
            ConfigureCommonEnvironment(startInfo, scenario);
            startInfo.Environment["PACKAGE_VERSION"] = packageVersion ?? TelemetryVersion;
            startInfo.Environment["WORKSPACE"] = ToShellPath(_root);
            startInfo.Environment["AETHEUS_PACKAGE_BASE_URL"] = "https://packages.invalid";
            startInfo.Environment["AETHEUS_PACKAGE_TOKEN"] = "aeth_pat_signature+/=";
            startInfo.Environment["FAKE_REGISTRY_PACKAGE"] = ToShellPath(_registryPackage);
            startInfo.Environment["CURL"] = ToShellPath(Path.Combine(_fakeBin, "curl"));
            return RunAsync(startInfo);
        }

        private void ConfigureCommonEnvironment(ProcessStartInfo startInfo, string scenario)
        {
            startInfo.Environment.Remove("PACKAGE_VERSION");
            startInfo.Environment["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = ExpectedFingerprint;
            startInfo.Environment["EXPECTED_TEST_FINGERPRINT"] = ExpectedFingerprint;
            startInfo.Environment["FAKE_VERIFY_SCENARIO"] = scenario;
            startInfo.Environment["FAKE_OPENSSL_FINGERPRINT"] = string.Join(
                ':',
                Enumerable.Repeat("AA", 32));
            startInfo.Environment["DOTNET"] = ToShellPath(Path.Combine(_fakeBin, "dotnet"));
        }

        private ProcessStartInfo CreateStartInfo(string script)
        {
            var shell = OperatingSystem.IsWindows()
                ? @"C:\Program Files\Git\bin\bash.exe"
                : "/bin/sh";
            var startInfo = new ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(ToShellPath(_shellWrapper));
            startInfo.Environment["FAKE_BIN"] = ToShellPath(_fakeBin);
            startInfo.Environment["TARGET_SCRIPT"] = ToShellPath(script);
            return startInfo;
        }

        private static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo)
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Could not start publication harness.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }

        private static async Task CreateCandidateAsync(string root, string kind, string packageName, string version)
        {
            var output = Path.Combine(root, ".package-candidate", kind);
            Directory.CreateDirectory(output);
            var package = Path.Combine(output, packageName);
            var bytes = "signed package fixture"u8.ToArray();
            await File.WriteAllBytesAsync(package, bytes, TestContext.Current.CancellationToken);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await File.WriteAllTextAsync(
                Path.Combine(output, "SHA256SUMS"),
                $"{digest}  {packageName}\n",
                TestContext.Current.CancellationToken);
            await WriteProvenanceAsync(output, kind, version);
        }

        // Same layout as the child pipeline's JSON.stringify(provenance, null, 2); null attests no version.
        private static Task WriteProvenanceAsync(string output, string kind, string? version) =>
            File.WriteAllTextAsync(
                Path.Combine(output, "provenance.json"),
                version is null
                    ? "{}\n"
                    : $"{{\n  \"schema\": 1,\n  \"packageKind\": \"{kind}\",\n  \"packageVersion\": \"{version}\",\n  \"artifacts\": []\n}}\n",
                TestContext.Current.CancellationToken);

        private static async Task WriteExecutableAsync(string path, string contents)
        {
            await File.WriteAllTextAsync(path, contents, TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static string ToShellPath(string path)
        {
            if (!OperatingSystem.IsWindows()) return path;
            return "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
