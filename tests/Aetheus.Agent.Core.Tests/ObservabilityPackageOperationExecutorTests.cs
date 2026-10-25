// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ObservabilityPackageOperationExecutorTests
{
    [Fact]
    public void BuildStartInfo_KeepsSecretsOutOfArgv_AndUsesAgentHarness()
    {
        const string token = "super-secret-token";
        const string password = "super-secret-password";
        var environment = new Dictionary<string, string>
        {
            ["AETHEUS_WORKING_DIR"] = "/work/candidate",
            ["PACKAGE_VERSION"] = "1.2.3",
            ["AETHEUS_NUGET_SIGNING_PFX_BASE64"] = "cGZ4",
            ["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] = password,
            ["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = new('A', 64),
            ["AETHEUS_PACKAGE_BASE_URL"] = "https://attacker.example",
            ["AETHEUS_PACKAGE_TOKEN"] = token,
            ["DOTNET"] = "/tmp/hostile-dotnet",
            ["UNRELATED_SECRET"] = "must-not-cross"
        };

        var startInfo = ObservabilityPackageOperationExecutor.BuildStartInfo(
            "/agent-owned/promote.sh",
            "/agent-owned/publish.sh",
            "https://packages.example",
            environment);

        Assert.Equal("sh", startInfo.FileName);
        Assert.Equal(["/agent-owned/promote.sh"], startInfo.ArgumentList);
        Assert.DoesNotContain(startInfo.ArgumentList, argument =>
            argument.Contains(token, StringComparison.Ordinal)
            || argument.Contains(password, StringComparison.Ordinal));
        Assert.Equal("/agent-owned/publish.sh", startInfo.Environment["AETHEUS_TRUSTED_PUBLISH_SCRIPT"]);
        Assert.Equal("https://packages.example", startInfo.Environment["AETHEUS_PACKAGE_BASE_URL"]);
        Assert.False(startInfo.Environment.ContainsKey("DOTNET"));
        Assert.False(startInfo.Environment.ContainsKey("UNRELATED_SECRET"));
        // A legacy set-wide version is still forwarded, so the harness can hold every candidate to it.
        Assert.Equal("1.2.3", startInfo.Environment["PACKAGE_VERSION"]);
    }

    private static Dictionary<string, string> PerPackageEnvironment() => new()
    {
        ["AETHEUS_WORKING_DIR"] = "/work/candidate",
        ["AETHEUS_NUGET_SIGNING_PFX_BASE64"] = "cGZ4",
        ["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] = "password",
        ["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = new('A', 64),
        ["AETHEUS_PACKAGE_TOKEN"] = "token"
    };

    [Fact]
    public void HasRequiredEnvironment_DoesNotRequireASetWideVersion()
    {
        var environment = PerPackageEnvironment();

        Assert.True(ObservabilityPackageOperationExecutor.HasRequiredEnvironment(environment));
        environment.Remove("AETHEUS_PACKAGE_TOKEN");
        Assert.False(ObservabilityPackageOperationExecutor.HasRequiredEnvironment(environment));
    }

    [Fact]
    public void BuildStartInfo_WithoutASetWideVersion_PassesNoneToTheHarness()
    {
        var startInfo = ObservabilityPackageOperationExecutor.BuildStartInfo(
            "/agent-owned/promote.sh",
            "/agent-owned/publish.sh",
            "https://packages.example",
            PerPackageEnvironment());

        Assert.False(startInfo.Environment.ContainsKey("PACKAGE_VERSION"));
        Assert.Equal("/work/candidate", startInfo.Environment["AETHEUS_WORKING_DIR"]);
    }

    [Fact]
    public void SigningCertificateMatchesExpectedFingerprint_RequiresExactPinnedPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Aetheus Package Signing Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        const string password = "test-only-password";
        var environment = new Dictionary<string, string>
        {
            ["AETHEUS_NUGET_SIGNING_PFX_BASE64"] = Convert.ToBase64String(
                certificate.Export(X509ContentType.Pfx, password)),
            ["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] = password,
            ["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] =
                certificate.GetCertHashString(HashAlgorithmName.SHA256)
        };

        Assert.True(ObservabilityPackageOperationExecutor
            .SigningCertificateMatchesExpectedFingerprint(environment));

        environment["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = new('0', 64);
        Assert.False(ObservabilityPackageOperationExecutor
            .SigningCertificateMatchesExpectedFingerprint(environment));
    }

    [Fact]
    public async Task TrustedPublishHarness_RetryAcceptsIdenticalBytesWithoutSecondWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "aetheus-publish-test-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, ".package-candidate", "telemetry");
        var fakeBin = Path.Combine(root, "fake-bin");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(fakeBin);
        try
        {
            const string version = "1.2.3";
            var package = Path.Combine(output, $"Aetheus.Telemetry.{version}.nupkg");
            await File.WriteAllTextAsync(package, "immutable-package-bytes", TestContext.Current.CancellationToken);
            var digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(
                package, TestContext.Current.CancellationToken)));
            await File.WriteAllTextAsync(
                Path.Combine(output, "SHA256SUMS"),
                $"{digest}  Aetheus.Telemetry.{version}.nupkg\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(output, "provenance.json"),
                $"{{\n  \"packageKind\": \"telemetry\",\n  \"packageVersion\": \"{version}\"\n}}\n",
                TestContext.Current.CancellationToken);

            var fakeCurl = Path.Combine(fakeBin, "curl");
            await File.WriteAllTextAsync(fakeCurl, """
                #!/bin/sh
                destination=""
                form=""
                method="GET"
                while [ "$#" -gt 0 ]; do
                  case "$1" in
                    --output) destination="$2"; shift 2 ;;
                    --form) form="$2"; shift 2 ;;
                    --request) method="$2"; shift 2 ;;
                    --config|--write-out) shift 2 ;;
                    --silent|--show-error|--location|--fail) shift ;;
                    *) shift ;;
                  esac
                done
                if [ "$method" = PUT ]; then
                  package="${form#package=@}"
                  package="${package%%;*}"
                  cp "$package" "$FAKE_REGISTRY"
                  printf 'write\n' >> "$FAKE_WRITE_COUNT"
                elif [ -f "$FAKE_REGISTRY" ]; then
                  cp "$FAKE_REGISTRY" "$destination"
                  printf 200
                else
                  printf 404
                fi
                """.ReplaceLineEndings("\n"), TestContext.Current.CancellationToken);
            var fakeDotnet = Path.Combine(fakeBin, "dotnet");
            await File.WriteAllTextAsync(fakeDotnet, """
                #!/bin/sh
                if [ "$1" = nuget ] && [ "$2" = push ]; then
                  cp "$3" "$FAKE_REGISTRY"
                  printf 'write\n' >> "$FAKE_WRITE_COUNT"
                fi
                exit 0
                """.ReplaceLineEndings("\n"), TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                var executableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(fakeCurl, executableMode);
                File.SetUnixFileMode(fakeDotnet, executableMode);
            }

            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Aetheus.slnx")))
                repository = repository.Parent;
            Assert.NotNull(repository);
            var script = Path.Combine(
                repository.FullName, "src", "Aetheus.Agent.Core", "Harnesses", "publish-observability-package.sh");
            var registry = Path.Combine(root, "registry.nupkg");
            var writes = Path.Combine(root, "writes");
            await RunHarnessAsync(script, root, fakeBin, registry, writes, version);
            await RunHarnessAsync(script, root, fakeBin, registry, writes, version);

            Assert.Equal(["write"], await File.ReadAllLinesAsync(writes, TestContext.Current.CancellationToken));
            Assert.Equal(
                await File.ReadAllBytesAsync(package, TestContext.Current.CancellationToken),
                await File.ReadAllBytesAsync(registry, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunHarnessAsync(
        string script,
        string root,
        string fakeBin,
        string registry,
        string writes,
        string version)
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
        startInfo.ArgumentList.Add(script.Replace('\\', '/'));
        startInfo.ArgumentList.Add("telemetry");
        startInfo.Environment["WORKSPACE"] = root.Replace('\\', '/');
        startInfo.Environment["PACKAGE_VERSION"] = version;
        startInfo.Environment["AETHEUS_PACKAGE_BASE_URL"] = "https://packages.example";
        startInfo.Environment["AETHEUS_PACKAGE_TOKEN"] = "aeth_pat_test+/=";
        startInfo.Environment["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = new('A', 64);
        startInfo.Environment["DOTNET"] = Path.Combine(fakeBin, "dotnet").Replace('\\', '/');
        startInfo.Environment["CURL"] = Path.Combine(fakeBin, "curl").Replace('\\', '/');
        startInfo.Environment["FAKE_REGISTRY"] = registry.Replace('\\', '/');
        startInfo.Environment["FAKE_WRITE_COUNT"] = writes.Replace('\\', '/');
        var pathSeparator = OperatingSystem.IsWindows() ? ":" : Path.PathSeparator.ToString();
        var shellFakeBin = OperatingSystem.IsWindows()
            ? "/" + char.ToLowerInvariant(fakeBin[0]) + fakeBin[2..].Replace('\\', '/')
            : fakeBin;
        startInfo.Environment["PATH"] = shellFakeBin + pathSeparator
            + startInfo.Environment["PATH"];
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0,
            $"Harness failed ({process.ExitCode}). stdout={await output} stderr={await error}");
    }
}
