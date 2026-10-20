// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.SystemLogs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.SystemLogs;

/// <summary>
/// F-004. Every field of a client error report is caller-controlled, so a newline in any of them
/// forged whole entries in a text log sink: a report could fabricate an unrelated event, or push a
/// real one out of view. Lengths were already bounded on the DTO; what was missing is that a value
/// must stay ONE line.
/// </summary>
public sealed class ClientErrorsControllerTests
{
    private readonly ILogger<ClientErrorsController> _logger = Substitute.For<ILogger<ClientErrorsController>>();

    private ClientErrorsController Sut() => new(_logger);

    [Fact]
    public void Report_NewlineInAnyField_IsFlattenedBeforeLogging()
    {
        var captured = new List<object?>();
        _logger.When(logger => logger.Log(
                Arg.Any<LogLevel>(), Arg.Any<EventId>(), Arg.Any<object>(),
                Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(call => captured.AddRange(
                ((IReadOnlyList<KeyValuePair<string, object?>>)call[2]!).Select(pair => pair.Value)));

        var result = Sut().Report(new ClientErrorLogRequest
        {
            CorrelationId = "abc\r\ndef",
            Summary = "boom\nFATAL forged entry",
            Message = "line one\nline two",
            Path = "/pipelines\n/admin"
        });

        Assert.IsType<NoContentResult>(result);
        var strings = captured.OfType<string>().ToList();
        Assert.NotEmpty(strings);
        Assert.All(strings, value =>
        {
            Assert.DoesNotContain('\n', value);
            Assert.DoesNotContain('\r', value);
        });
        // Flattened, not truncated: the report is still readable, it just cannot forge a line.
        Assert.Contains(strings, value => value.Contains("FATAL forged entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_MissingPath_LogsTheUnknownPlaceholderRatherThanNull()
    {
        string? logged = null;
        _logger.When(logger => logger.Log(
                Arg.Any<LogLevel>(), Arg.Any<EventId>(), Arg.Any<object>(),
                Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(call => logged = ((IReadOnlyList<KeyValuePair<string, object?>>)call[2]!)
                .First(pair => pair.Key == "ClientPath").Value as string);

        Sut().Report(new ClientErrorLogRequest
        {
            CorrelationId = "abc",
            Summary = "boom",
            Message = "details",
            Path = null
        });

        Assert.Equal("(unknown)", logged);
    }

    [Fact]
    public void R2100_Report_LogsThePageAddressWithoutItsQuery_AndTheStackTraceOnOneLine()
    {
        var logged = CaptureLoggedValues();

        Sut().Report(new ClientErrorLogRequest
        {
            CorrelationId = "abc",
            Summary = "Unhandled client exception: ObjectDisposedException",
            Message = "CancellationTokenSource_Disposed",
            Path = "/pipelines/runs/7?tab=logs&token=s3cr3t#step-2",
            StackTrace = "   at Aetheus.Front.Components.Dashboard.Home.RefreshAsync()\n   at System.Threading.Timer.Fire()"
        });

        Assert.Equal("/pipelines/runs/7", logged["ClientPath"]);
        var stack = Assert.IsType<string>(logged["ClientStackTrace"]);
        Assert.Contains("Home.RefreshAsync", stack, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', stack);
        Assert.DoesNotContain(logged.Values.OfType<string>(), value => value.Contains("s3cr3t", StringComparison.Ordinal));
    }

    [Fact]
    public void R2100_Report_ABlankPath_IsUnknownRatherThanAnEmptyAddress()
    {
        var logged = CaptureLoggedValues();

        Sut().Report(new ClientErrorLogRequest { CorrelationId = "abc", Summary = "boom", Message = "details", Path = "" });

        Assert.Equal("(unknown)", logged["ClientPath"]);
        Assert.Equal("(none)", logged["ClientStackTrace"]);
    }

    [Fact]
    public void R2100_StackTrace_IsBoundedOnTheContract()
    {
        var property = typeof(ClientErrorLogRequest).GetProperty(nameof(ClientErrorLogRequest.StackTrace))!;
        var length = (System.ComponentModel.DataAnnotations.StringLengthAttribute)Attribute.GetCustomAttribute(
            property, typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute))!;

        Assert.Equal(4000, length.MaximumLength);
    }

    [Theory]
    [InlineData(ClientErrorLogLevels.Warning, LogLevel.Warning)]
    [InlineData(ClientErrorLogLevels.Error, LogLevel.Error)]
    [InlineData(null, LogLevel.Error)]
    public void R1_89_Report_IsLoggedAtTheRequestedLevel_AndNoLevelStaysAnError(string? level, LogLevel expected)
    {
        var levels = new List<LogLevel>();
        _logger.When(logger => logger.Log(
                Arg.Any<LogLevel>(), Arg.Any<EventId>(), Arg.Any<object>(),
                Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(call => levels.Add((LogLevel)call[0]));

        Sut().Report(new ClientErrorLogRequest { CorrelationId = "abc", Summary = "boom", Message = "details", Level = level });

        Assert.Equal([expected], levels);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(ClientErrorLogLevels.Error, true)]
    [InlineData(ClientErrorLogLevels.Warning, true)]
    [InlineData("warning", false)]
    [InlineData("Critical", false)]
    [InlineData("", false)]
    public void R1_89_Level_IsValidatedToTheKnownSet(string? level, bool valid)
    {
        var request = new ClientErrorLogRequest { CorrelationId = "abc", Summary = "boom", Message = "details", Level = level };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var ok = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true);

        Assert.Equal(valid, ok);
    }

    private Dictionary<string, object?> CaptureLoggedValues()
    {
        var logged = new Dictionary<string, object?>();
        _logger.When(logger => logger.Log(
                Arg.Any<LogLevel>(), Arg.Any<EventId>(), Arg.Any<object>(),
                Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(call =>
            {
                foreach (var pair in (IReadOnlyList<KeyValuePair<string, object?>>)call[2]!)
                    logged[pair.Key] = pair.Value;
            });
        return logged;
    }
}
