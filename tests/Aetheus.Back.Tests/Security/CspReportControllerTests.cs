// SPDX-License-Identifier: EUPL-1.2

using System.Text;
using Aetheus.Back.Components.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Tests.Security;

/// <summary> The endpoint exists so the next blocked script names itself in the backend logs. It is
/// anonymous, so it must never fail loudly and never let attacker-controlled text into a log line
/// unshaped. </summary>
public sealed class CspReportControllerTests
{
    private readonly FakeLogger _logger = new();

    private CspReportController Controller() => new(_logger);

    [Fact]
    public void A_Violation_Is_Logged_With_What_The_Browser_Refused()
    {
        var result = Controller().Report(new CspReportEnvelope
        {
            CspReport = new CspReportBody
            {
                DocumentUri = "https://app.example/pipelines/runs/2281",
                BlockedUri = "inline",
                ViolatedDirective = "script-src",
                SourceFile = "https://app.example/_framework/blazor.webassembly.js",
                LineNumber = 1,
                ScriptSample = "alert(1)"
            }
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("script-src", _logger.Last, StringComparison.Ordinal);
        Assert.Contains("pipelines/runs/2281", _logger.Last, StringComparison.Ordinal);
        Assert.Contains("alert(1)", _logger.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Report_Body_Stays_On_One_Line_And_Bounded()
    {
        // The sample is attacker-controlled: newlines would forge extra log entries, and an unbounded
        // sample would let one violation flood the log.
        Controller().Report(new CspReportEnvelope
        {
            CspReport = new CspReportBody
            {
                ViolatedDirective = "script-src",
                ScriptSample = "first\nsecond" + new string('x', 5_000)
            }
        });

        Assert.DoesNotContain('\n', _logger.Last);
        Assert.True(_logger.Last.Length < 2_000, $"Log line is {_logger.Last.Length} characters long.");
    }

    [Fact]
    public void A_Sample_Full_Of_Windows_Line_Endings_Is_Folded_Not_Fatal()
    {
        // Folding CRLF pairs shortens the string: cutting at the original length threw here.
        var sample = string.Concat(Enumerable.Repeat("\r\n", 400)) + "alert(1)";

        Controller().Report(new CspReportEnvelope
        {
            CspReport = new CspReportBody { ViolatedDirective = "script-src", ScriptSample = sample }
        });

        Assert.DoesNotContain('\r', _logger.Last);
        Assert.DoesNotContain('\n', _logger.Last);
        Assert.True(_logger.Last.Length < 2_000, $"Log line is {_logger.Last.Length} characters long.");
    }

    [Fact]
    public void The_Legacy_Report_Uri_Body_Is_Read_Field_By_Field()
    {
        var report = Assert.Single(CspReportController.ParseReports(Utf8("""
            {"csp-report":{"document-uri":"https://a/runs/1","blocked-uri":"inline",
            "violated-directive":"script-src","source-file":"https://a/x.js","line-number":12,
            "script-sample":"alert(1)"}}
            """)));

        Assert.Equal("https://a/runs/1", report.DocumentUri);
        Assert.Equal("inline", report.BlockedUri);
        Assert.Equal("script-src", report.ViolatedDirective);
        Assert.Equal("https://a/x.js", report.SourceFile);
        Assert.Equal(12, report.LineNumber);
        Assert.Equal("alert(1)", report.ScriptSample);
    }

    [Fact]
    public void The_Reporting_Api_Batch_Is_Read_Under_Its_Own_Field_Names()
    {
        // The Reporting API posts an ARRAY, and names the same fields without hyphens.
        var report = Assert.Single(CspReportController.ParseReports(Utf8("""
            [{"type":"network-error","body":{}},
             {"type":"csp-violation","age":0,"url":"https://a/runs/1",
              "body":{"documentURL":"https://a/runs/1","blockedURL":"inline",
                      "effectiveDirective":"script-src-elem","sourceFile":"https://a/x.js",
                      "lineNumber":12,"sample":"alert(1)"}}]
            """)));

        Assert.Equal("https://a/runs/1", report.DocumentUri);
        Assert.Equal("inline", report.BlockedUri);
        Assert.Equal("script-src-elem", report.ViolatedDirective);
        Assert.Equal("https://a/x.js", report.SourceFile);
        Assert.Equal(12, report.LineNumber);
        Assert.Equal("alert(1)", report.ScriptSample);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("\"junk\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"type":"csp-violation"}]""")]
    public void Anything_That_Is_Not_A_Report_Yields_Nothing(string body) =>
        Assert.Empty(CspReportController.ParseReports(Utf8(body)));


    [Fact]
    public void A_Batch_Cannot_Turn_One_Request_Into_A_Log_Flood()
    {
        // A minimal report is ~50 bytes, so the 8 KiB body cap alone still allows ~150 log lines per
        // accepted request. A browser batches a handful; the surplus must be dropped.
        var entry = """{"type":"csp-violation","body":{"blockedURL":"i"}},""";
        var batch = "[" + string.Concat(Enumerable.Repeat(entry, 150)).TrimEnd(',') + "]";

        var reports = CspReportController.ParseReports(Utf8(batch));

        Assert.True(reports.Count <= 8, $"{reports.Count} reports logged for one request.");
    }

    private static ReadOnlyMemory<byte> Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void An_Empty_Or_Malformed_Report_Is_Accepted_Silently()
    {
        // A browser can do nothing with an error here; answering 4xx only invites retries.
        Assert.IsType<NoContentResult>(Controller().Report(null));
        Assert.IsType<NoContentResult>(Controller().Report(new CspReportEnvelope()));
        Assert.Equal(string.Empty, _logger.Last);
    }

    [Theory]
    [InlineData("moz-extension", "inline")]
    [InlineData("moz-extension://4f1c/content.js", "inline")]
    [InlineData(null, "chrome-extension://abcdef/inject.js")]
    [InlineData("safari-web-extension://x/a.js", "eval")]
    public void A_Report_From_A_Browser_Extension_Goes_To_Debug(string? sourceFile, string blockedUri)
    {
        // Seen locally on 2026-10-05: a Firefox extension injecting inline script and style, refused by
        // the policy as it should be, logged as three warnings that said nothing about Aetheus.
        Controller().Report(new CspReportEnvelope
        {
            CspReport = new CspReportBody
            {
                DocumentUri = "https://localhost:5403/",
                BlockedUri = blockedUri,
                ViolatedDirective = "script-src-elem",
                SourceFile = sourceFile,
                LineNumber = 74
            }
        });

        Assert.Equal(LogLevel.Debug, _logger.LastLevel);
        Assert.Contains("script-src-elem", _logger.Last, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sandbox eval code", "self['8qkyitlarc0prlgt3p'] = true; (func")]
    [InlineData("blob", "self['e8spdwh55uzzxkxtvm'] = true; (func")]
    public void A_Report_From_A_Firefox_Extension_Sandbox_Goes_To_Debug(string sourceFile, string sample)
    {
        // The two production reports of R1-10 (2026-10-19 and 2026-10-21), as logged.
        Controller().Report(new CspReportEnvelope
        {
            CspReport = new CspReportBody
            {
                DocumentUri = "https://app.example/pipelines/runs/2780",
                BlockedUri = "inline",
                ViolatedDirective = "script-src-elem",
                SourceFile = sourceFile,
                LineNumber = 19,
                ScriptSample = sample
            }
        });

        Assert.Equal(LogLevel.Debug, _logger.LastLevel);
    }

    [Fact]
    public void A_Blob_Report_Without_The_Extension_Marker_Stays_A_Warning()
    {
        Controller().Report(new CspReportEnvelope
        {
            CspReport = new CspReportBody
            {
                DocumentUri = "https://app.example/",
                BlockedUri = "inline",
                ViolatedDirective = "script-src-elem",
                SourceFile = "blob",
                ScriptSample = "window.location = 'https://evil.example'"
            }
        });

        Assert.Equal(LogLevel.Warning, _logger.LastLevel);
    }

    [Fact]
    public void A_Report_From_The_Page_Itself_Stays_A_Warning()
    {
        Controller().Report(new CspReportEnvelope
        {
            CspReport = new CspReportBody
            {
                DocumentUri = "https://app.example/",
                BlockedUri = "inline",
                ViolatedDirective = "script-src-elem",
                SourceFile = "https://app.example/_framework/blazor.webassembly.js"
            }
        });

        Assert.Equal(LogLevel.Warning, _logger.LastLevel);
    }

    private sealed class FakeLogger : ILogger<CspReportController>
    {
        public string Last { get; private set; } = string.Empty;

        public LogLevel? LastLevel { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Last = formatter(state, exception);
            LastLevel = logLevel;
        }
    }
}
