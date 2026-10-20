// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Tests;

public sealed class ScannerManifestTests
{
    [Fact]
    public void DefaultManifest_HasUniqueImmutableScannerDefinitions()
    {
        var manifest = ScannerManifestCatalog.Default;

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Contains(manifest.Scanners, scanner => scanner.Key == "gitleaks");
        Assert.Contains(manifest.Scanners, scanner => scanner.Key == "opengrep");
        Assert.Contains(manifest.Scanners, scanner => scanner.Key == "trivy-dependencies");
        Assert.Contains(manifest.Scanners, scanner => scanner.Key == "syft");
        Assert.Contains(manifest.Scanners, scanner => scanner.Key == "trivy-image-front");
        Assert.Contains(manifest.Scanners, scanner => scanner.Key == "syft-front");
        Assert.Contains(manifest.Scanners, scanner => scanner.Key == "zap-active");
        var syft = Assert.Single(manifest.Scanners, scanner => scanner.Key == "syft");
        Assert.Equal(2_147_483_648, syft.MaxTemporaryBytes);
        Assert.Equal("4g", syft.Memory);
        Assert.All(manifest.Scanners, scanner =>
            Assert.InRange(scanner.MaxTemporaryBytes ?? 268_435_456, 67_108_864, 4_294_967_296));
        Assert.All(manifest.Scanners.Where(scanner => scanner.Key is "zap-api" or "zap-passive"), scanner =>
        {
            Assert.Equal("/zap/wrk", scanner.ContainerOutputDirectory);
            Assert.Equal("/zap/wrk", scanner.ContainerWorkingDirectory);
            Assert.Equal("/home/zap/runtime", scanner.TemporaryHomeDirectory);
            Assert.Equal("/home/zap/.ZAP", scanner.SeedTemporaryHomeFrom);
            Assert.Equal("/home/zap/runtime/.ZAP", scanner.SeedTemporaryHomeTarget);
            Assert.Contains(scanner.Arguments, argument => argument.Contains("-silent", StringComparison.Ordinal));
        });
        var zapApi = Assert.Single(manifest.Scanners, scanner => scanner.Key == "zap-api");
        var zapPassive = Assert.Single(manifest.Scanners, scanner => scanner.Key == "zap-passive");
        var zapActive = Assert.Single(manifest.Scanners, scanner => scanner.Key == "zap-active");
        Assert.Equal("4g", zapApi.Memory);
        Assert.Equal("2.0", zapApi.Cpus);
        Assert.Equal(
            "-Duser.home=/home/zap/runtime -Xmx2048m -XX:ActiveProcessorCount=2",
            zapApi.Environment["_JAVA_OPTIONS"]);
        Assert.Equal("3g", zapActive.Memory);
        Assert.Equal("1.0", zapActive.Cpus);
        Assert.Equal(
            "-Duser.home=/home/zap/runtime -Xmx768m -XX:ActiveProcessorCount=1",
            zapActive.Environment["_JAVA_OPTIONS"]);
        Assert.Equal("/bin/sh", zapActive.EntryPoint);
        Assert.Contains("/aetheus/harness/zap-active-automation.sh", zapActive.Arguments);
        Assert.DoesNotContain(zapActive.Arguments, argument =>
            argument.Contains("/src/deploy/scripts/", StringComparison.Ordinal));
        Assert.Contains("__AETHEUS_PROXY_HOST__", zapActive.Arguments);
        Assert.Contains("__AETHEUS_PROXY_PORT__", zapActive.Arguments);
        Assert.Contains("__AETHEUS_PROXY_USERNAME__", zapActive.Arguments);
        Assert.Contains("__AETHEUS_PROXY_PASSWORD__", zapActive.Arguments);
        Assert.Contains("{report}", zapActive.Arguments);
        Assert.Equal([2], zapActive.FindingExitCodes);
        Assert.Equal("{report}", ReportArgument(zapApi));
        Assert.DoesNotContain("-m", zapApi.Arguments);
        Assert.Equal("3", OptionValue(zapApi, "-T"));
        Assert.Contains("ascan.maxScanDurationInMins=12", OptionValue(zapApi, "-z"), StringComparison.Ordinal);
        Assert.Equal("report.json", ReportArgument(zapPassive));
        Assert.All(manifest.Scanners.Where(scanner => scanner.Execution == "container"), scanner =>
            Assert.Contains("@sha256:", scanner.Image, StringComparison.Ordinal));
        Assert.All(manifest.Scanners.Where(scanner => scanner.Execution == "binary"), scanner =>
        {
            Assert.Equal(64, scanner.Sha256LinuxAmd64?.Length);
            Assert.Contains("@sha256:", scanner.RuntimeImage, StringComparison.Ordinal);
        });
        Assert.All(manifest.Scanners.Where(scanner => scanner.Key is "eslint" or "jscpd"), scanner =>
        {
            Assert.Equal("/src", scanner.ContainerWorkingDirectory);
            Assert.Equal("node", scanner.EntryPoint);
            Assert.DoesNotContain(scanner.Arguments, argument =>
                argument.Equals("exec", StringComparison.Ordinal)
                || argument.Equals("--offline", StringComparison.Ordinal));
        });
        var eslint = Assert.Single(manifest.Scanners, scanner => scanner.Key == "eslint");
        Assert.Equal("/src/node_modules/eslint/bin/eslint.js", eslint.Arguments[0]);
        var jscpd = Assert.Single(manifest.Scanners, scanner => scanner.Key == "jscpd");
        Assert.Equal("/src/node_modules/jscpd/run-jscpd.js", jscpd.Arguments[0]);
        Assert.Equal("/home/aetheus-runtime", jscpd.TemporaryHomeDirectory);
        Assert.Equal("/home/aetheus-runtime", jscpd.HomeDirectory);
        Assert.Equal(134_217_728, jscpd.MaxTemporaryHomeBytes);
        // Jscpd 5 replaces the repository's configured lists with any list given on the command
        // line, so the manifest passes none and the repository's.jscpd.json applies.
        Assert.DoesNotContain(jscpd.Arguments, argument =>
            argument is "--ignore-pattern" or "--ignore" or "-i" or "--config" or "-c");
        var gitleaksHistory = Assert.Single(manifest.Scanners, scanner => scanner.Key == "gitleaks-history");
        Assert.All(manifest.Scanners.Where(scanner => scanner.Key is "gitleaks" or "gitleaks-history"), scanner =>
            Assert.Equal("/src/.gitleaks.toml", OptionValue(scanner, "--config")));
        Assert.DoesNotContain("--log-opts", gitleaksHistory.Arguments);
        Assert.DoesNotContain("{gitLogRange}", gitleaksHistory.Arguments);
        var ruff = Assert.Single(manifest.Scanners, scanner => scanner.Key == "ruff");
        Assert.Contains(".claude", OptionValues(ruff, "--exclude"));
        Assert.Contains("node_modules", OptionValues(ruff, "--exclude"));
        Assert.Contains(".analysis-*", OptionValues(ruff, "--exclude"));
        // The OpenGrep rule corpus is vulnerable and incomplete on purpose (#1094 F821, #1112
        // NoPackage). Ruff resolves a relative pattern against its working directory, which the
        // scanner container does not set, so the path is absolute.
        Assert.Contains("/src/tests/security-rules", OptionValues(ruff, "--exclude"));
        var pmd = Assert.Single(manifest.Scanners, scanner => scanner.Key == "pmd-java");
        Assert.Contains("--exclude=/src/tests/security-rules", pmd.Arguments);
        var openGrep = Assert.Single(manifest.Scanners, scanner => scanner.Key == "opengrep");
        Assert.Contains("--exclude=node_modules", openGrep.Arguments);
        Assert.Contains("--exclude=.claude", openGrep.Arguments);
        Assert.Contains("--exclude=src/Aetheus.Front/wwwroot/lib", openGrep.Arguments);
        Assert.Contains("--exclude=.aetheus/security-rules/tests", openGrep.Arguments);
        Assert.Equal("{source}", openGrep.Arguments[^1]);
        var trivyIac = Assert.Single(manifest.Scanners, scanner => scanner.Key == "trivy-iac");
        var ignoreFileIndex = trivyIac.Arguments.IndexOf("--ignorefile");
        Assert.InRange(ignoreFileIndex, 0, trivyIac.Arguments.Count - 2);
        Assert.Equal("/src/.trivyignore.yaml", trivyIac.Arguments[ignoreFileIndex + 1]);
        Assert.Contains("/src/.claude", trivyIac.Arguments);
        Assert.Contains("/src/**/bin", trivyIac.Arguments);
        Assert.Contains("/src/**/obj", trivyIac.Arguments);
        // The fixtures that prove the security rules are vulnerable on purpose: scanned as source, their
        // test Dockerfile sealed the whole security grade at F (run 2359, three DS-0031 "critical").
        Assert.Contains("/src/tests/security-rules", trivyIac.Arguments);
        Assert.All(manifest.Scanners.Where(scanner => scanner.Key == "trivy-dependencies"
                                                      || scanner.Key.StartsWith("trivy-image", StringComparison.Ordinal)), scanner =>
        {
            var repositories = scanner.Arguments
                .Select((argument, index) => (argument, index))
                .Where(item => item.argument == "--db-repository")
                .Select(item => scanner.Arguments[item.index + 1])
                .ToArray();
            Assert.Equal(
                ["ghcr.io/aquasecurity/trivy-db:2", "mirror.gcr.io/aquasec/trivy-db:2"],
                repositories);
            Assert.Contains("ghcr.io", scanner.AllowedDestinations);
            Assert.Contains("mirror.gcr.io", scanner.AllowedDestinations);
        });
        Assert.Contains("/src/.analysis-image/aetheus-back.tar",
            Assert.Single(manifest.Scanners, scanner => scanner.Key == "trivy-image").Arguments);
        Assert.Contains("/src/.analysis-image/aetheus-front.tar",
            Assert.Single(manifest.Scanners, scanner => scanner.Key == "trivy-image-front").Arguments);
        Assert.Contains(Assert.Single(manifest.Scanners, scanner => scanner.Key == "syft").Arguments,
            argument => argument.Contains("aetheus-back.tar", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(manifest.Scanners, scanner => scanner.Key == "syft-front").Arguments,
            argument => argument.Contains("aetheus-front.tar", StringComparison.Ordinal));

        static string ReportArgument(ScannerManifestEntry scanner)
        {
            var index = scanner.Arguments.IndexOf("-J");
            Assert.InRange(index, 0, scanner.Arguments.Count - 2);
            return scanner.Arguments[index + 1];
        }

        static string OptionValue(ScannerManifestEntry scanner, string option)
        {
            var index = scanner.Arguments.IndexOf(option);
            Assert.InRange(index, 0, scanner.Arguments.Count - 2);
            return scanner.Arguments[index + 1];
        }

        static IReadOnlyList<string> OptionValues(ScannerManifestEntry scanner, string option) =>
            scanner.Arguments
                .Select((argument, index) => (argument, index))
                .Where(item => item.argument == option && item.index + 1 < scanner.Arguments.Count)
                .Select(item => scanner.Arguments[item.index + 1])
                .ToList();
    }

    [Theory]
    [InlineData(67_108_863)]
    [InlineData(4_294_967_297)]
    public void Validate_RejectsTemporaryStorageQuotaOutsideBounds(long quota)
    {
        var manifest = new ScannerManifest
        {
            SchemaVersion = 1,
            Scanners =
            [
                new ScannerManifestEntry
                {
                    Key = "bounded",
                    Version = "1.0.0",
                    Execution = "container",
                    Image = "example.invalid/scanner@sha256:" + new string('a', 64),
                    MaxTemporaryBytes = quota,
                },
            ],
        };

        var error = Assert.Throws<InvalidDataException>(() => ScannerManifestCatalog.Validate(manifest));

        Assert.Contains("temporary storage quota", error.Message, StringComparison.Ordinal);
    }
}
