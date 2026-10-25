// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

public sealed class ScannerContainerProcessBuilderTests
{
    [Fact]
    public void AddRestrictedEgressEnvironment_BypassesOnlyTheScannerControlHost()
    {
        var arguments = new List<string>();

        ScannerContainerProcessBuilder.AddRestrictedEgressEnvironment(arguments, "http://proxy.test:56400");

        Assert.Contains("HTTP_PROXY=http://proxy.test:56400", arguments);
        Assert.Contains("HTTPS_PROXY=http://proxy.test:56400", arguments);
        Assert.Contains("http_proxy=http://proxy.test:56400", arguments);
        Assert.Contains("https_proxy=http://proxy.test:56400", arguments);
        Assert.Contains("NO_PROXY=localhost", arguments);
        Assert.Contains("no_proxy=localhost", arguments);
        Assert.DoesNotContain("NO_PROXY=127.0.0.1", arguments);
        Assert.DoesNotContain("no_proxy=127.0.0.1", arguments);
    }

    [Fact]
    public void Build_GitleaksRangeIsOneValidatedArgumentAfterTheImage()
    {
        var range = $"{new string('a', 40)}..{new string('b', 40)}";
        var scanner = new ScannerManifestEntry
        {
            Key = "gitleaks-history",
            Image = $"example.invalid/gitleaks@sha256:{new string('c', 64)}",
            EntryPoint = "gitleaks",
            Execution = "container",
            Arguments = ["git", "/src"],
            ReportPath = "report.sarif"
        };

        var process = ScannerContainerProcessBuilder.Build(
            scanner,
            Directory.GetCurrentDirectory(),
            Directory.GetCurrentDirectory(),
            null,
            null,
            new Dictionary<string, string> { ["AETHEUS_GITLEAKS_LOG_OPTIONS"] = range },
            null);

        var arguments = process.ArgumentList.ToList();
        var imageIndex = arguments.IndexOf(scanner.Image);
        var optionIndex = arguments.IndexOf("--log-opts");
        Assert.True(imageIndex >= 0);
        Assert.True(optionIndex > imageIndex);
        Assert.Equal(range, arguments[optionIndex + 1]);
        Assert.DoesNotContain("--all", arguments);
    }

    [Fact]
    public void Build_GitleaksFullScanDoesNotAddLogOptions()
    {
        var scanner = new ScannerManifestEntry
        {
            Key = "gitleaks-history",
            Image = $"example.invalid/gitleaks@sha256:{new string('c', 64)}",
            EntryPoint = "gitleaks",
            Execution = "container",
            Arguments = ["git", "/src"],
            ReportPath = "report.sarif"
        };

        var process = ScannerContainerProcessBuilder.Build(
            scanner,
            Directory.GetCurrentDirectory(),
            Directory.GetCurrentDirectory(),
            null,
            null,
            new Dictionary<string, string>(),
            null);

        Assert.DoesNotContain("--log-opts", process.ArgumentList);
    }

    [Fact]
    public void Build_ZapApiAppliesBoundedHeapAndContainerResources()
    {
        var scanner = Assert.Single(
            ScannerManifestCatalog.Default.Scanners,
            entry => entry.Key == "zap-api");

        var process = ScannerContainerProcessBuilder.Build(
            scanner,
            Directory.GetCurrentDirectory(),
            Directory.GetCurrentDirectory(),
            null,
            null,
            new Dictionary<string, string>
            {
                ["AETHEUS_DAST_TARGET_URL"] = "http://127.0.0.1:8080",
                ["AETHEUS_DAST_API_SPECIFICATION_URL"] = "http://127.0.0.1:8080/openapi/v1.json",
                ["AETHEUS_DAST_API_SPECIFICATION_FORMAT"] = "openapi",
            },
            null);

        var arguments = process.ArgumentList.ToList();
        Assert.Equal("4g", OptionValue(arguments, "--memory"));
        Assert.Equal("2.0", OptionValue(arguments, "--cpus"));
        Assert.Contains(
            "_JAVA_OPTIONS=-Duser.home=/home/zap/runtime -Xmx2048m -XX:ActiveProcessorCount=2",
            arguments);

        static string OptionValue(List<string> arguments, string option)
        {
            var index = arguments.IndexOf(option);
            Assert.InRange(index, 0, arguments.Count - 2);
            return arguments[index + 1];
        }
    }

    [Theory]
    [InlineData(".jscpd.json", "{\"ignorePattern\":[\"x\"]}")]
    [InlineData("package.json", "{\"name\":\"app\",\"jscpd\":{\"minTokens\":70}}")]
    public void Build_JscpdWithRepositoryConfiguration_PassesNoIgnoreThatWouldReplaceIt(
        string fileName, string content)
    {
        // Preserve repository jscpd lists by omitting command-line list overrides.
        using var source = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(source.Path, fileName), content);

        var arguments = BuildJscpd(source.Path);

        Assert.DoesNotContain("--ignore", arguments);
        Assert.DoesNotContain("--ignore-pattern", arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"name\":\"app\",\"devDependencies\":{\"jscpd\":\"5.0.12\"}}")]
    [InlineData("not json")]
    public void Build_JscpdWithoutRepositoryConfiguration_IgnoresInstalledPackages(string? packageJson)
    {
        using var source = new TemporaryDirectory();
        if (packageJson is not null)
            File.WriteAllText(Path.Combine(source.Path, "package.json"), packageJson);

        var arguments = BuildJscpd(source.Path);

        var index = arguments.IndexOf("--ignore");
        Assert.InRange(index, arguments.IndexOf("/src/node_modules/jscpd/run-jscpd.js"), arguments.Count - 2);
        Assert.Equal("**/node_modules/**", arguments[index + 1]);
        Assert.DoesNotContain("--ignore-pattern", arguments);
    }

    /// <summary>Recette R3-63, option 3: a repository without <c>.gitleaks.toml</c> is scanned with
    /// Gitleaks' built-in rules (no <c>--config</c>, which Gitleaks refuses for a missing path); a
    /// repository with one keeps it, path included.</summary>
    [Theory]
    [InlineData("gitleaks")]
    [InlineData("gitleaks-history")]
    public void Build_GitleaksReadsTheRepositoryConfigurationOnlyWhenItExists(string key)
    {
        using var withoutFile = new TemporaryDirectory();
        using var withFile = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(withFile.Path, ".gitleaks.toml"), "[extend]\nuseDefault = true\n");

        var defaults = BuildScanner(key, withoutFile.Path);
        var configured = BuildScanner(key, withFile.Path);

        Assert.DoesNotContain("--config", defaults);
        Assert.DoesNotContain("/src/.gitleaks.toml", defaults);
        Assert.Contains("--report-path", defaults);
        var index = configured.IndexOf("--config");
        Assert.True(index >= 0, "The repository's own Gitleaks configuration must be passed.");
        Assert.Equal("/src/.gitleaks.toml", configured[index + 1]);
    }

    /// <summary>ADR-050: a repository without <c>.trivyignore.yaml</c> is scanned by Trivy IaC with no exception
    /// (no <c>--ignorefile</c>, which Trivy refuses for a missing path: "ignore file not found"); a repository
    /// with one keeps it, path included.</summary>
    [Fact]
    public void Build_TrivyIacReadsTheRepositoryIgnoreFileOnlyWhenItExists()
    {
        using var withoutFile = new TemporaryDirectory();
        using var withFile = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(withFile.Path, ".trivyignore.yaml"), "misconfigurations: []\n");

        var defaults = BuildScanner("trivy-iac", withoutFile.Path);
        var configured = BuildScanner("trivy-iac", withFile.Path);

        Assert.DoesNotContain("--ignorefile", defaults);
        Assert.DoesNotContain("/src/.trivyignore.yaml", defaults);
        Assert.Contains("misconfig", defaults);
        var index = configured.IndexOf("--ignorefile");
        Assert.True(index >= 0, "The repository's own Trivy ignore file must be passed.");
        Assert.Equal("/src/.trivyignore.yaml", configured[index + 1]);
    }

    [Fact]
    public void DropAbsent_LeavesAFileOutsideTheSourceUntouched()
    {
        using var source = new TemporaryDirectory();
        List<string> outside = ["dir", "/src", "--config", "/rules/gitleaks.toml"];
        List<string> dangling = ["dir", "/src", "--config"];

        Assert.False(ScannerOptionalRepositoryFile.DropAbsent(outside, "--config", source.Path));
        Assert.False(ScannerOptionalRepositoryFile.DropAbsent(dangling, "--config", source.Path));
        Assert.Equal(["dir", "/src", "--config", "/rules/gitleaks.toml"], outside);
        Assert.Equal(["dir", "/src", "--config"], dangling);
    }

    private static List<string> BuildScanner(string key, string sourceDirectory)
    {
        var scanner = Assert.Single(ScannerManifestCatalog.Default.Scanners, entry => entry.Key == key);
        return ScannerContainerProcessBuilder.Build(
            scanner,
            sourceDirectory,
            sourceDirectory,
            null,
            null,
            new Dictionary<string, string>(),
            null).ArgumentList.ToList();
    }

    private static List<string> BuildJscpd(string sourceDirectory)
    {
        var scanner = Assert.Single(ScannerManifestCatalog.Default.Scanners, entry => entry.Key == "jscpd");
        return ScannerContainerProcessBuilder.Build(
            scanner,
            sourceDirectory,
            sourceDirectory,
            null,
            null,
            new Dictionary<string, string>(),
            null).ArgumentList.ToList();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aetheus-jscpd-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
