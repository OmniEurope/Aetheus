// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Toolchains;
using Aetheus.Shared.Components.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-6: every container started for pipeline work carries a low CPU weight (256 against the Docker
/// default of 1024) so the production containers on the same machine keep priority under contention.</summary>
public sealed class PipelineContainerCpuWeightTests
{
    private const string ImmutableImage = "img@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string OptionValue(IList<string> arguments, string option)
    {
        var index = arguments.IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.5")]
    public void StepContainer_HasTheLowCpuWeight_WithAndWithoutACpuCeiling(string? cpus)
    {
        var executor = new ContainerRunExecutor(
            Options.Create(new AetheusAgentOptions()),
            NullLogger<ContainerRunExecutor>.Instance,
            new ToolchainResolver(),
            TimeProvider.System);

        var args = executor.BuildDockerRun(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1, Cpus = cpus, Memory = "512m" },
            new ToolchainResolution { IsSuccess = true, Image = ImmutableImage, Shell = "sh" },
            "/host/ws", "/host/home", [],
            ".prom-step-00000000000000000000000000000000.sh",
            "prom-cpu", null, null).ArgumentList;

        Assert.Equal("256", OptionValue(args, "--cpu-shares"));
        Assert.Equal("512m", OptionValue(args, "--memory"));
        if (cpus is null) Assert.DoesNotContain("--cpus", args);
        else Assert.Equal(cpus, OptionValue(args, "--cpus"));
    }

    [Fact]
    public void ScannerContainer_HasTheLowCpuWeight_AlongsideItsCpuCeiling()
    {
        var scanner = Assert.Single(ScannerManifestCatalog.Default.Scanners, entry => entry.Key == "zap-api");

        var process = ScannerContainerProcessBuilder.Build(
            scanner, Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory(), null, null,
            new Dictionary<string, string>
            {
                ["AETHEUS_DAST_TARGET_URL"] = "http://127.0.0.1:8080",
                ["AETHEUS_DAST_API_SPECIFICATION_URL"] = "http://127.0.0.1:8080/openapi/v1.json",
                ["AETHEUS_DAST_API_SPECIFICATION_FORMAT"] = "openapi",
            },
            null);

        Assert.Equal("256", OptionValue(process.ArgumentList, "--cpu-shares"));
        Assert.Equal("2.0", OptionValue(process.ArgumentList, "--cpus"));
    }

    [Fact]
    public void OpenGrepSandboxContainer_HasTheLowCpuWeight_AlongsideItsCpuCeiling()
    {
        var scanner = new ScannerManifestEntry
        {
            Key = "opengrep",
            RuntimeImage = ImmutableImage,
            Memory = "1g",
            Cpus = "1.0",
        };

        var process = OpenGrepSandbox.BuildDockerProcess(scanner, "/bin/opengrep", [], [], "1000:1000");

        Assert.Equal("256", OptionValue(process.ArgumentList, "--cpu-shares"));
        Assert.Equal("1.0", OptionValue(process.ArgumentList, "--cpus"));
    }
}
