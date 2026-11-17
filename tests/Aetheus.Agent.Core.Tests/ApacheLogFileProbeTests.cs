// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-19: the Apache log probe opens the file, so a missing log and one the agent may not read get
/// different answers, and the diagnostic line the front localizes round-trips through its parser.</summary>
public sealed class ApacheLogFileProbeTests
{
    [Fact]
    public void AnExistingFile_IsReadable_AndAMissingOne_IsMissing()
    {
        var directory = Directory.CreateTempSubdirectory("aetheus-apache-log-");
        try
        {
            var log = Path.Combine(directory.FullName, "error.log");
            File.WriteAllText(log, "line");

            Assert.Equal(LogFileAccess.Readable, ApacheLogFileProbe.Probe(log));
            Assert.Equal(LogFileAccess.Missing, ApacheLogFileProbe.Probe(Path.Combine(directory.FullName, "access.log")));
            Assert.Equal(LogFileAccess.Missing, ApacheLogFileProbe.Probe(Path.Combine(directory.FullName, "absent", "error.log")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_NamesEveryCandidate_OnlyWhenNoneExists()
    {
        var resolution = ApacheLogFileProbe.Resolve(["/a/error.log", "/b/error_log"], _ => LogFileAccess.Missing);

        Assert.Null(resolution.ReadablePath);
        Assert.Equal(ApacheLogReadOutcome.NotFound, resolution.Failure);
        Assert.Equal(["/a/error.log", "/b/error_log"], resolution.Paths);
    }

    [Fact]
    public void Resolve_ADeniedCandidate_WinsOverAMissingOne()
    {
        var resolution = ApacheLogFileProbe.Resolve(
            ["/a/error.log", "/b/error_log"], path => path == "/b/error_log" ? LogFileAccess.Denied : LogFileAccess.Missing);

        Assert.Equal(ApacheLogReadOutcome.AccessDenied, resolution.Failure);
        Assert.Equal(["/b/error_log"], resolution.Paths);
    }

    [Theory]
    [InlineData(ApacheLogReadOutcome.NotFound)]
    [InlineData(ApacheLogReadOutcome.AccessDenied)]
    [InlineData(ApacheLogReadOutcome.ReadError)]
    public void TheDiagnosticLine_RoundTripsThroughItsParser(ApacheLogReadOutcome outcome)
    {
        var output = "[process] something\n" + ApacheLogDiagnostic.Format(outcome, "/var/log/apache2/error.log", "/var/log/httpd/error_log");

        Assert.True(ApacheLogDiagnostic.TryParse(output, out var parsed, out var paths));
        Assert.Equal(outcome, parsed);
        Assert.Equal(["/var/log/apache2/error.log", "/var/log/httpd/error_log"], paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Thu Nov 06] [core:error] AH00037: Symbolic link not allowed")]
    [InlineData("[apache-log] Unknown /var/log/apache2/error.log")]
    [InlineData("[apache-log] 7 /var/log/apache2/error.log")]
    [InlineData("[apache-log] AccessDenied")]
    public void OrdinaryLogLines_AndMalformedDiagnostics_AreNotADiagnostic(string output)
    {
        Assert.False(ApacheLogDiagnostic.TryParse(output, out _, out _));
    }
}
