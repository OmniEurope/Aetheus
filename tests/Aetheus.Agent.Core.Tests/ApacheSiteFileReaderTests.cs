// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary> Production 2026-10-27 (project W6): an enabled site certbot wrote as root (mode 0600) cannot be read by the
/// agent account, so the project deletion kept every certificate. The site is read through the root-owned read-only
/// helper instead. These tests prove the argv the sudo grant allows, what travels on stdin, what is refused before the
/// helper is ever called, and how a helper failure is reported. The helper itself is run for real in
/// <c>tests/js/deploy/agent-host-helpers.test.mjs</c>; no sudo is involved here. </summary>
public sealed class ApacheSiteFileReaderTests
{
    private const string Enabled = "/etc/apache2/sites-enabled";

    private readonly List<(IReadOnlyList<string> Argv, string Input)> _calls = [];

    private ApacheSiteFileReader Reader(ExecutorResult result, params string[] lines) => new(NullLogger.Instance)
    {
        RunHelperAsync = (argv, input, _, _) =>
        {
            _calls.Add((argv, input));
            return Task.FromResult((result, (IReadOnlyList<string>)lines));
        },
    };

    [Fact]
    public async Task ReadAsync_SendsThePathOnStdin_WithTheOneArgvWordTheGrantAllows_AndReturnsTheFile()
    {
        var reader = Reader(new ExecutorResult(0, false), "<VirtualHost *:443>", "  ServerName x", "</VirtualHost>", "[process] exit-code=0");

        var read = await reader.ReadAsync(Enabled, "prometheus-le-ssl.conf", TestContext.Current.CancellationToken);

        Assert.Equal("<VirtualHost *:443>\n  ServerName x\n</VirtualHost>", read.Text);
        Assert.Null(read.Failure);
        var call = Assert.Single(_calls);
        Assert.Equal(["-n", "/usr/local/lib/aetheus/aetheus-apache-read-site", "read"], call.Argv);
        Assert.Equal(Enabled + "/prometheus-le-ssl.conf\n", call.Input);
    }

    [Theory]
    [InlineData("../../etc/shadow")]
    [InlineData("../sites-available/x.conf")]
    [InlineData("/etc/shadow")]
    [InlineData("sub/x.conf")]
    [InlineData("-x.conf")]
    [InlineData(".hidden.conf")]
    [InlineData("x.conf\n/etc/shadow")]
    [InlineData("x.conf\n")]
    [InlineData("x y.conf")]
    [InlineData("")]
    public async Task ReadAsync_RefusesAName_ThatCouldLeaveTheSiteDirectories_WithoutCallingTheHelper(string name)
    {
        var reader = Reader(new ExecutorResult(0, false), "must never be read");

        var read = await reader.ReadAsync(Enabled, name, TestContext.Current.CancellationToken);

        Assert.Null(read.Text);
        Assert.Contains("not a plain site file name", read.Failure, StringComparison.Ordinal);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ReadAsync_ReportsTheHelpersRefusal_WithItsExitCodeAndItsReason()
    {
        var reader = Reader(new ExecutorResult(1, false), "[stderr] aetheus-apache-read-site: x resolves outside the Apache site directories: refused", "[process] exit-code=1");

        var read = await reader.ReadAsync(Enabled, "x.conf", TestContext.Current.CancellationToken);

        Assert.Null(read.Text);
        Assert.Equal("the read helper refused or failed (exit code 1): aetheus-apache-read-site: x resolves outside the Apache site directories: refused", read.Failure);
    }

    [Fact]
    public async Task ReadAsync_ReportsAMissingSudoGrant_NeverAnEmptyFile()
    {
        // `sudo -n` without the grant (an agent installed before the helper existed) exits non-zero with nothing on stdout.
        var reader = Reader(new ExecutorResult(1, false), "[stderr] sudo: a password is required", "[process] exit-code=1");

        var read = await reader.ReadAsync(Enabled, "x.conf", TestContext.Current.CancellationToken);

        Assert.Null(read.Text);
        Assert.Contains("a password is required", read.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ReportsATimeout()
    {
        var reader = Reader(new ExecutorResult(-1, true));

        var read = await reader.ReadAsync(Enabled, "x.conf", TestContext.Current.CancellationToken);

        Assert.Null(read.Text);
        Assert.Contains("did not answer within", read.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ReportsAHelperThatCannotRun()
    {
        var reader = new ApacheSiteFileReader(NullLogger.Instance)
        {
            RunHelperAsync = (_, _, _, _) => throw new InvalidOperationException("no sudo here"),
        };

        var read = await reader.ReadAsync(Enabled, "x.conf", TestContext.Current.CancellationToken);

        Assert.Null(read.Text);
        Assert.Contains("could not run: no sudo here", read.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_KeepsEveryOutputLineInTheText_SoNothingPrintedIsLeftOutOfTheCheck()
    {
        var reader = Reader(new ExecutorResult(0, false), "[stderr] SSLCertificateFile /etc/letsencrypt/live/h/fullchain.pem", "[process] exit-code=0");

        var read = await reader.ReadAsync(Enabled, "x.conf", TestContext.Current.CancellationToken);

        Assert.Contains("/etc/letsencrypt/live/h/", read.Text, StringComparison.Ordinal);
    }
}
