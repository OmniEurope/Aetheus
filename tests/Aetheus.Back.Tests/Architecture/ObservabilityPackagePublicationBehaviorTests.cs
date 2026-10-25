// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Aetheus.Tests.Shared;

namespace Aetheus.Back.Tests.Architecture;

public sealed class ObservabilityPackagePublicationBehaviorTests
{
    private static string Root => FindRepoRoot();

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task SuccessAndFailure_RemoveEveryTemporaryArtifact()
    {
        await using var success = await PublicationHarness.CreateAsync("web-analytics-browser");
        Assert.Equal(0, await success.RunAsync());
        success.AssertTemporaryArtifactsRemoved();

        await using var failure = await PublicationHarness.CreateAsync("telemetry");
        failure.RemoveEnvironmentVariable("AETHEUS_PACKAGE_BASE_URL");
        Assert.Equal(1, await failure.RunAsync());
        failure.AssertTemporaryArtifactsRemoved();
    }

    [PlatformTheory("linux")]
    [InlineData("HUP", 129)]
    [InlineData("INT", 130)]
    [InlineData("TERM", 143)]
    [SupportedOSPlatform("linux")]
    public async Task Signal_CleansEveryTemporaryAndTerminatesWithSignalExitCode(
        string signal,
        int expectedExitCode)
    {
        await using var harness = await PublicationHarness.CreateAsync("web-analytics-browser");
        harness.EnableBlockingCurl();
        using var process = harness.Start();
        await harness.WaitForCurlAsync();

        using var kill = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/kill",
            ArgumentList = { $"-{signal}", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            UseShellExecute = false
        })!;
        await kill.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, kill.ExitCode);

        harness.ReleaseCurl();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedExitCode, process.ExitCode);
        harness.AssertTemporaryArtifactsRemoved();
    }

    // PLAN-002 C8: each package is published under its own version. Before, one set-wide
    // PACKAGE_VERSION reached every package, and a browser candidate attested 0.1.0 went through the
    // preflight under the Telemetry version 1.2.0 (exit 0). It is now refused before any registry call.
    [Theory]
    [InlineData("1.2.0", 2)]
    [InlineData("0.1.0", 0)]
    public async Task Publication_AcceptsOnlyTheVersionTheCandidateAttests(string packageVersion, int expectedExitCode)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aetheus-publish-version-{Guid.NewGuid():N}");
        var output = Path.Combine(root, ".package-candidate", "web-analytics-browser");
        var fakeBin = Path.Combine(root, "bin");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(fakeBin);
        try
        {
            const string packageName = "aetheus-web-analytics-0.1.0.tgz";
            var packageBytes = "per-package version fixture"u8.ToArray();
            await File.WriteAllBytesAsync(
                Path.Combine(output, packageName), packageBytes, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(output, "SHA256SUMS"),
                $"{Convert.ToHexStringLower(SHA256.HashData(packageBytes))}  {packageName}\n",
                TestContext.Current.CancellationToken);
            await WriteProvenanceAsync(output, "web-analytics-browser", "0.1.0");
            var curlCalls = Path.Combine(root, "curl-calls");
            var fakeCurl = Path.Combine(fakeBin, "curl");
            await File.WriteAllTextAsync(
                fakeCurl,
                """
                #!/bin/sh
                DESTINATION=""
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = "--output" ]; then shift; DESTINATION="$1"; fi
                  shift
                done
                printf 'call\n' >> "$FAKE_CURL_CALLS"
                if [ -n "$DESTINATION" ]; then : > "$DESTINATION"; fi
                printf '404'
                """.ReplaceLineEndings("\n"),
                TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    fakeCurl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var startInfo = new ProcessStartInfo(
                OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\bash.exe" : "/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(ToShellPath(
                Path.Combine(Root, "src", "Aetheus.Agent.Core", "Harnesses", "publish-observability-package.sh")));
            startInfo.ArgumentList.Add("web-analytics-browser");
            startInfo.Environment["WORKSPACE"] = ToShellPath(root);
            startInfo.Environment["PACKAGE_VERSION"] = packageVersion;
            startInfo.Environment["AETHEUS_PACKAGE_BASE_URL"] = "https://packages.invalid";
            startInfo.Environment["AETHEUS_PACKAGE_TOKEN"] = "aeth_pat_version+/=";
            startInfo.Environment["AETHEUS_PUBLISH_PREFLIGHT_ONLY"] = "true";
            startInfo.Environment["CURL"] = ToShellPath(fakeCurl);
            startInfo.Environment["FAKE_CURL_CALLS"] = ToShellPath(curlCalls);
            using var process = Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(
                process.ExitCode == expectedExitCode,
                $"ExitCode={process.ExitCode} stdout={await standardOutput} stderr={await standardError}");
            Assert.Equal(expectedExitCode == 0, File.Exists(curlCalls));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Same layout as the child pipeline's JSON.stringify(provenance, null, 2).
    private static Task WriteProvenanceAsync(string output, string kind, string version) =>
        File.WriteAllTextAsync(
            Path.Combine(output, "provenance.json"),
            $"{{\n  \"schema\": 1,\n  \"packageKind\": \"{kind}\",\n  \"packageVersion\": \"{version}\",\n  \"artifacts\": []\n}}\n",
            TestContext.Current.CancellationToken);

    private static string ToShellPath(string path) =>
        OperatingSystem.IsWindows()
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;

    private sealed class PublicationHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _temporaryRoot;
        private readonly string _marker;
        private readonly string _release;
        private readonly ProcessStartInfo _startInfo;

        private PublicationHarness(
            string root,
            string temporaryRoot,
            string marker,
            string release,
            ProcessStartInfo startInfo)
        {
            _root = root;
            _temporaryRoot = temporaryRoot;
            _marker = marker;
            _release = release;
            _startInfo = startInfo;
        }

        [SupportedOSPlatform("linux")]
        public static async Task<PublicationHarness> CreateAsync(string kind)
        {
            var root = Path.Combine(Path.GetTempPath(), $"aetheus-publish-{Guid.NewGuid():N}");
            var temporaryRoot = Path.Combine(root, "temporary");
            var fakeBin = Path.Combine(root, "bin");
            Directory.CreateDirectory(temporaryRoot);
            Directory.CreateDirectory(fakeBin);

            const string version = "1.0.0";
            var output = Path.Combine(root, ".package-candidate", kind);
            Directory.CreateDirectory(output);
            var packageName = kind == "web-analytics-browser"
                ? $"aetheus-web-analytics-{version}.tgz"
                : $"Aetheus.Telemetry.{version}.nupkg";
            var packageBytes = "behavioral publication fixture"u8.ToArray();
            await File.WriteAllBytesAsync(
                Path.Combine(output, packageName),
                packageBytes,
                TestContext.Current.CancellationToken);
            await WriteProvenanceAsync(output, kind, version);
            var digest = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            await File.WriteAllTextAsync(
                Path.Combine(output, "SHA256SUMS"),
                $"{digest}  {packageName}\n",
                TestContext.Current.CancellationToken);

            var fakeMktemp = Path.Combine(fakeBin, "mktemp");
            await File.WriteAllTextAsync(
                fakeMktemp,
                """
                #!/bin/sh
                COUNTER_FILE="$FAKE_MKTEMP_ROOT/counter"
                COUNT=0
                if [ -f "$COUNTER_FILE" ]; then COUNT="$(cat "$COUNTER_FILE")"; fi
                COUNT=$((COUNT + 1))
                printf '%s' "$COUNT" > "$COUNTER_FILE"
                TARGET="$FAKE_MKTEMP_ROOT/item-$COUNT"
                if [ "${1:-}" = "-d" ]; then mkdir -p "$TARGET"; else : > "$TARGET"; fi
                printf '%s\n' "$TARGET"
                """.ReplaceLineEndings("\n"),
                TestContext.Current.CancellationToken);
            var fakeCurl = Path.Combine(fakeBin, "curl");
            await File.WriteAllTextAsync(
                fakeCurl,
                """
                #!/bin/sh
                DESTINATION=""
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = "--output" ]; then shift; DESTINATION="$1"; fi
                  shift
                done
                if [ -n "${FAKE_CURL_MARKER:-}" ]; then
                  : > "$FAKE_CURL_MARKER"
                  while [ ! -f "$FAKE_CURL_RELEASE" ]; do sleep 0.05; done
                fi
                if [ -n "$DESTINATION" ]; then : > "$DESTINATION"; fi
                printf '404'
                """.ReplaceLineEndings("\n"),
                TestContext.Current.CancellationToken);
            File.SetUnixFileMode(
                fakeMktemp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                fakeCurl,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var marker = Path.Combine(root, "curl-started");
            var release = Path.Combine(root, "curl-release");
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--default-signal=HUP");
            startInfo.ArgumentList.Add("--default-signal=INT");
            startInfo.ArgumentList.Add("--default-signal=TERM");
            startInfo.ArgumentList.Add("/bin/sh");
            startInfo.ArgumentList.Add(Path.Combine(Root, "src", "Aetheus.Agent.Core", "Harnesses", "publish-observability-package.sh"));
            startInfo.ArgumentList.Add(kind);
            startInfo.Environment["WORKSPACE"] = root;
            startInfo.Environment["PACKAGE_VERSION"] = version;
            startInfo.Environment["AETHEUS_PACKAGE_BASE_URL"] = "https://packages.invalid";
            startInfo.Environment["AETHEUS_PACKAGE_TOKEN"] = "aeth_pat_behavior+/=";
            startInfo.Environment["AETHEUS_PUBLISH_PREFLIGHT_ONLY"] = "true";
            startInfo.Environment["FAKE_MKTEMP_ROOT"] = temporaryRoot;
            startInfo.Environment["PATH"] = $"{fakeBin}:{Environment.GetEnvironmentVariable("PATH")}";
            startInfo.Environment["FAKE_CURL_RELEASE"] = release;
            return new PublicationHarness(root, temporaryRoot, marker, release, startInfo);
        }

        public void RemoveEnvironmentVariable(string name) => _startInfo.Environment.Remove(name);

        public void EnableBlockingCurl() => _startInfo.Environment["FAKE_CURL_MARKER"] = _marker;

        public void ReleaseCurl() => File.WriteAllText(_release, string.Empty);

        public Process Start() => Process.Start(_startInfo)
                                  ?? throw new InvalidOperationException("Could not start publication script.");

        public async Task<int> RunAsync()
        {
            using var process = Start();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return process.ExitCode;
        }

        public async Task WaitForCurlAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(_marker) && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(_marker), "The fake curl process did not start.");
        }

        public void AssertTemporaryArtifactsRemoved()
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(_temporaryRoot, "item-*"));
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
