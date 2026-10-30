// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-119: enabling and disabling an Apache module was an <c>a2enmod x &amp;&amp; systemctl restart apache2</c> shell
/// string run without root. It is a typed operation now: <c>sudo -n aetheus-apache-module enable|disable</c>, a fixed
/// argv the aetheus-apache grant names word for word, the module name on stdin, checked here and again by the helper.</summary>
public sealed class ApacheModuleOperationTests
{
    private readonly List<(IReadOnlyList<string> Argv, string StandardInput, int Timeout)> _runs = [];
    private readonly List<(string Message, TaskLogLevel Level)> _output = [];

    private ApacheOperationExecutor Executor(bool isLinux = true, int exitCode = 0) =>
        new(Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 10, MaxTimeoutSeconds = 120 }),
            NullLogger<ApacheOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunModuleHelperAsync = (argv, standardInput, timeout, _, _) =>
            {
                _runs.Add((argv, standardInput, timeout));
                return Task.FromResult(new ExecutorResult(exitCode, false));
            }
        };

    private Task<ExecutorResult> RunAsync(ApacheOperationExecutor executor, OperationKind kind, string module, int timeout = 90) =>
        executor.ExecuteAsync(kind, module, new Dictionary<string, string>(), timeout,
            (message, level) => { _output.Add((message, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(OperationKind.ApacheEnableModule, "enable")]
    [InlineData(OperationKind.ApacheDisableModule, "disable")]
    public async Task ModuleOperation_RunsTheHelperWithAFixedArgv_AndTheModuleOnStdin(OperationKind kind, string word)
    {
        var result = await RunAsync(Executor(), kind, "proxy_http", timeout: 500);

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(_runs);
        Assert.Equal(["-n", "/usr/local/lib/aetheus/aetheus-apache-module", word], run.Argv);
        Assert.Equal("proxy_http\n", run.StandardInput);
        Assert.Equal(120, run.Timeout);
    }

    [Fact]
    public async Task ModuleOperation_ReportsTheHelperFailure()
    {
        var result = await RunAsync(Executor(exitCode: 1), OperationKind.ApacheEnableModule, "ssl");

        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("-f")]
    [InlineData("ssl\nreboot")]
    [InlineData("ssl\n")]
    [InlineData("a b")]
    [InlineData("mods-available/ssl")]
    public async Task ModuleOperation_RefusesAnythingButAModuleName_WithoutRunningAnything(string module)
    {
        var result = await RunAsync(Executor(), OperationKind.ApacheEnableModule, module);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(_runs);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Error);
    }

    [Fact]
    public async Task ModuleOperation_IsLinuxOnly()
    {
        var result = await RunAsync(Executor(isLinux: false), OperationKind.ApacheDisableModule, "ssl");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(_runs);
    }

    [Fact]
    public void ModuleKinds_AreHandledByTheApacheExecutor_AndNeedTheApacheModulesCapability()
    {
        var executor = Executor();
        Assert.True(executor.CanHandle(OperationKind.ApacheEnableModule));
        Assert.True(executor.CanHandle(OperationKind.ApacheDisableModule));
        Assert.Equal(AgentCapabilities.ApacheModules, AgentCapabilities.RequiredFor(OperationKind.ApacheEnableModule));
        Assert.Equal(AgentCapabilities.ApacheModules, AgentCapabilities.RequiredFor(OperationKind.ApacheDisableModule));
        Assert.Contains(AgentCapabilities.ApacheModules, AgentCapabilities.SoftwareCapabilities);
    }

    /// <summary>The aetheus-apache drop-in grants exactly the two argv the executor sends, no wildcard, and the
    /// installer renders the helper they name.</summary>
    [Fact]
    public void TheApacheGrant_NamesEachHelperArgvTheAgentSends()
    {
        var dropIn = LinuxHostConfigTemplates.Read("apache/sudoers.d/aetheus-apache");
        foreach (var kind in new[] { OperationKind.ApacheEnableModule, OperationKind.ApacheDisableModule })
        {
            var argv = ApacheOperationExecutor.BuildModuleHelperArgv(kind)!;
            var granted = string.Join(' ', argv.Skip(1)).Replace(ApacheOperationExecutor.ModuleHelperPath, "#{APACHE_MODULE_HELPER_PATH}#", StringComparison.Ordinal);
            Assert.Contains(granted, dropIn, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("#{APACHE_MODULE_HELPER_PATH}# *", dropIn, StringComparison.Ordinal);
        Assert.Contains("Defaults!AETHEUS_APACHE_MODULE timestamp_timeout=0\n", dropIn, StringComparison.Ordinal);
        Assert.Contains("NOPASSWD: AETHEUS_APACHE_MODULE\n", dropIn, StringComparison.Ordinal);
    }

    /// <summary>The helper re-validates the name it reads on stdin before running a2enmod/a2dismod as root.</summary>
    [Fact]
    public void TheModuleHelper_ReadsTheNameOnStdin_AndChecksIt()
    {
        var helper = LinuxHostConfigTemplates.Read("apache/aetheus-apache-module");
        Assert.Contains("read -r module", helper, StringComparison.Ordinal);
        Assert.Contains("/usr/sbin/a2enmod", helper, StringComparison.Ordinal);
        Assert.Contains("/usr/sbin/a2dismod", helper, StringComparison.Ordinal);
        Assert.Contains("/bin/systemctl restart apache2.service", helper, StringComparison.Ordinal);
    }
}
