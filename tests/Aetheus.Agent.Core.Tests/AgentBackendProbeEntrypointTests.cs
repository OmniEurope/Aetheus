// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;
using Aetheus.Agent.Core.Workers;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Agent.Core.Tests;

public sealed class AgentBackendProbeEntrypointTests
{
    [Fact]
    public async Task TryRunAsync_WithoutProbeArgument_DoesNotInvokeProbe()
    {
        var invoked = false;
        var result = await AgentBackendProbeEntrypoint.TryRunAsync(
            [], BuildConfiguration(), (_, _, _, _, _) =>
            {
                invoked = true;
                return Task.FromResult(0);
            }, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(invoked);
    }

    [Fact]
    public async Task TryRunAsync_WithProbeArgument_UsesBoundConfigurationAndReturnsProbeExitCode()
    {
        string? observedUrl = null;
        string[]? observedArgs = null;
        bool? observedAllowInsecure = null;
        string? observedPin = null;
        var args = new[] { "--probe-backend", "--timeout", "7" };

        var result = await AgentBackendProbeEntrypoint.TryRunAsync(
            args, BuildConfiguration(), (url, receivedArgs, allowInsecure, pin, _) =>
            {
                observedUrl = url;
                observedArgs = receivedArgs;
                observedAllowInsecure = allowInsecure;
                observedPin = pin;
                return Task.FromResult(17);
            }, TestContext.Current.CancellationToken);

        Assert.Equal(17, result);
        Assert.Equal("https://backend.example", observedUrl);
        Assert.Same(args, observedArgs);
        Assert.True(observedAllowInsecure);
        Assert.Equal(new string('A', 64), observedPin);
    }

    [Fact]
    public async Task TryRunAsync_RemoteAllowInsecureWithoutPin_FailsBeforeNetworkProbe()
    {
        var invoked = false;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Aetheus:ServerUrl"] = "https://backend.example",
                ["Aetheus:AllowInsecureCerts"] = "true"
            }).Build();

        var result = await AgentBackendProbeEntrypoint.TryRunAsync(
            ["--probe-backend"], configuration, (_, _, _, _, _) =>
            {
                invoked = true;
                return Task.FromResult(0);
            }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.False(invoked);
    }

    [Fact]
    public async Task TryRunAsync_LocalAllowInsecureWithoutPin_InvokesNetworkProbe()
    {
        var invoked = false;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Aetheus:ServerUrl"] = "https://127.0.0.1:5303",
                ["Aetheus:AllowInsecureCerts"] = "true"
            }).Build();

        var result = await AgentBackendProbeEntrypoint.TryRunAsync(
            ["--probe-backend"], configuration, (_, _, _, pin, _) =>
            {
                invoked = true;
                Assert.Null(pin);
                return Task.FromResult(0);
            }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.True(invoked);
    }

    [Fact]
    public void LinuxAndWindowsPrograms_DelegateProbeModeToSharedEntrypoint()
    {
        var root = FindRepoRoot();
        foreach (var project in new[] { "Aetheus.Agent.Linux", "Aetheus.Agent.Windows" })
        {
            var program = File.ReadAllText(Path.Combine(root, "src", project, "Program.cs"));
            Assert.Contains("AgentBackendProbeEntrypoint.TryRunAsync(args, builder.Configuration)", program, StringComparison.Ordinal);
            Assert.DoesNotContain("BackendProbe.RunAsync", program, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WindowsProgram_LoadsConfigurationFromInstallationDirectory()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Agent.Windows", "Program.cs"));

        Assert.Contains("ContentRootPath = AppContext.BaseDirectory", program, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aetheus:ServerUrl"] = "https://backend.example",
            ["Aetheus:AllowInsecureCerts"] = "true",
            ["Aetheus:PinnedServerCertThumbprint"] = new string('A', 64)
        }).Build();

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
