// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

public class PackageOperationExecutorTests
{
    private static PackageOperationExecutor Build() =>
        new(Options.Create(new AetheusAgentOptions()), NullLogger<PackageOperationExecutor>.Instance);

    [Theory]
    [InlineData(OperationKind.ServiceInstall, true)]
    [InlineData(OperationKind.ServiceUninstall, true)]
    [InlineData(OperationKind.ServiceStart, false)]
    [InlineData(OperationKind.DockerPullImage, false)]
    public void CanHandle_OnlyPackageKinds(OperationKind kind, bool expected)
        => Assert.Equal(expected, Build().CanHandle(kind));

    [Fact]
    public async Task ExecuteAsync_NonAllowListedPackage_ReturnsFailureWithoutSpawning()
    {
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.ServiceInstall, "evil-package", 60,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(messages, m => m.Contains("allow-list", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildAptArgv_Install_ProducesExactArgv()
        => Assert.Equal(
            new[] { "-n", "/usr/bin/apt-get", "install", "-y", "nginx" },
            PackageOperationExecutor.BuildAptArgv("install", "nginx"));

    // Uninstall purges package configuration as well as binaries, including packages already in rc
    // state.
    [Fact]
    public void Uninstall_PurgesThePackage_NeverRemovesIt()
    {
        Assert.Equal("install", PackageOperationExecutor.AptVerbFor(OperationKind.ServiceInstall));
        Assert.Equal("purge", PackageOperationExecutor.AptVerbFor(OperationKind.ServiceUninstall));
        Assert.Equal(
            new[] { "-n", "/usr/bin/apt-get", "purge", "-y", "dovecot-core" },
            PackageOperationExecutor.BuildAptArgv(
                PackageOperationExecutor.AptVerbFor(OperationKind.ServiceUninstall), "dovecot-core"));
    }

    // Fresh-box guard: install first refreshes the apt index via this argv-exact `apt-get update`.
    [Fact]
    public void BuildAptUpdateArgv_ProducesExactArgv()
        => Assert.Equal(
            new[] { "-n", "/usr/bin/apt-get", "update" },
            PackageOperationExecutor.BuildAptUpdateArgv());
}
