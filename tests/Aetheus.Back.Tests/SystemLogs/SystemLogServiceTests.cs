// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OmniEurope.Diagnostics.Logs;

namespace Aetheus.Back.Tests;

/// <summary>The system logs service over the real OmniEurope.Diagnostics journal and a real folder: the package
/// reads the files, the service maps, splits by the organization's rules and applies the grid's filters.</summary>
public class SystemLogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly SystemLogService _sut;

    public SystemLogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aetheus_log_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(_tempDir);
        _provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(environment)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddOmniLogs(options => options.Directory = _tempDir)
            .BuildServiceProvider();
        _scope = _provider.CreateScope();
        _sut = new SystemLogService(
            _scope.ServiceProvider.GetRequiredService<ILogJournal>(), NullLogger<SystemLogService>.Instance, TimeProvider.System);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* best-effort cleanup */ }
    }

    private Task WriteAsync(string fileName, params string[] lines) =>
        File.WriteAllLinesAsync(Path.Combine(_tempDir, fileName), lines, TestContext.Current.CancellationToken);

    private static string Line(string time, string level, string message, string category = "App", string? correlation = null) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["Timestamp"] = $"2025-01-15T{time}Z",
            ["Level"] = level,
            ["SourceContext"] = category,
            ["Message"] = message,
            ["CorrelationId"] = correlation
        });

    [Fact]
    public async Task GetLogFilesAsync_EmptyDir_ReturnsEmpty()
    {
        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Empty(files);
    }

    [Fact]
    public async Task GetLogFilesAsync_WithLogFiles_ReturnsMostRecentlyWrittenFirst()
    {
        await WriteAsync("old.json", "{}");
        await WriteAsync("new.json", "{}");
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, "old.json"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, "new.json"), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(["new.json", "old.json"], files.Select(file => file.FileName));
    }

    [Fact]
    public async Task GetLogFilesAsync_IgnoresFilesThePatternDoesNotMatch_AndTheRulesFile()
    {
        await WriteAsync("readme.txt", "not a log");
        await WriteAsync("legacy.log", "plain text");
        await WriteAsync(RulesFile, "{}");
        await WriteAsync("app.json", "{}");

        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal("app.json", Assert.Single(files).FileName);
    }

    private const string RulesFile = OmniEurope.Diagnostics.LogsOptions.DefaultRulesFile;

    [Fact]
    public async Task GetLogEntriesAsync_EmptyDir_ReturnsEmpty()
    {
        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetLogEntriesAsync_MapsTheEntriesNewestFirst()
    {
        await WriteAsync("app.json",
            Line("10:30:00.123", "Information", "first", correlation: "corr-1"),
            Line("10:31:00.456", "Error", "second", category: "Other"));

        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["second", "first"], result.Items.Select(entry => entry.Message));
        var first = result.Items[1];
        Assert.Equal(new DateTime(2025, 1, 15, 10, 30, 0, 123, DateTimeKind.Utc), first.Timestamp);
        Assert.Equal("Information", first.Level);
        Assert.Equal("App", first.Category);
        Assert.Equal("corr-1", first.CorrelationId);
        Assert.Equal([1, 2], result.Items.Select(entry => entry.Id));
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersByExactLevel()
    {
        await WriteAsync("app.json",
            Line("10:30:00", "Information", "info"), Line("10:31:00", "Error", "error"), Line("10:32:00", "Fatal", "fatal"));

        var result = await _sut.GetLogEntriesAsync(null, "Error", null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Equal("error", Assert.Single(result.Items).Message);
    }

    [Fact]
    public async Task GetLogEntriesAsync_UnknownLevel_ReturnsNothing()
    {
        await WriteAsync("app.json", Line("10:30:00", "Information", "info"));

        var result = await _sut.GetLogEntriesAsync(null, "ERR", null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersByPeriod()
    {
        await WriteAsync("app.json",
            Line("10:00:00", "Information", "early"), Line("11:00:00", "Information", "middle"), Line("12:00:00", "Information", "late"));

        var result = await _sut.GetLogEntriesAsync(
            null, null, null,
            new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc), new DateTime(2025, 1, 15, 11, 30, 0, DateTimeKind.Utc),
            1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Equal("middle", Assert.Single(result.Items).Message);
    }

    /// <summary>The virtualized grid sends its header filters and its sort; they apply to the whole
    /// filtered log before paging, not to one block.</summary>
    [Fact]
    public async Task GetLogEntriesAsync_AppliesColumnFiltersAndSortBeforePaging()
    {
        await WriteAsync("app.json",
            Line("10:30:00.123", "Information", "alpha one"),
            Line("10:31:00.456", "Error", "beta two"),
            Line("10:32:00.789", "Information", "alpha three"));

        var filtered = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 1,
            TestContext.Current.CancellationToken, sortBy: "Timestamp", sortDescending: false,
            filters: [new GridFilter { Field = "Message", Operator = GridFilterOperator.Contains, Value = "alpha" }]);

        Assert.Equal(2, filtered.TotalCount);
        Assert.Equal("alpha one", Assert.Single(filtered.Items).Message);

        var byLevel = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50,
            TestContext.Current.CancellationToken,
            filters: [new GridFilter { Field = "Level", Operator = GridFilterOperator.In, Value = "Error" }]);

        Assert.Equal("beta two", Assert.Single(byLevel.Items).Message);
    }

    [Fact]
    public async Task GetLogEntriesAsync_UnknownColumn_IsABadRequest()
    {
        await WriteAsync("app.json", Line("10:30:00", "Information", "alpha"));

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50,
            TestContext.Current.CancellationToken, sortBy: "Exception"));
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersBySearch_InMessageCategoryAndCorrelationId()
    {
        await WriteAsync("app.json",
            Line("10:30:00", "Information", "Hello world"),
            Line("10:31:00", "Information", "Goodbye world"),
            Line("10:32:00", "Error", "Failure", category: "Billing", correlation: "agent-update-42"));

        var byMessage = await _sut.GetLogEntriesAsync(null, null, "hello", null, null, 1, 50, ct: TestContext.Current.CancellationToken);
        var byCategory = await _sut.GetLogEntriesAsync(null, null, "billing", null, null, 1, 50, ct: TestContext.Current.CancellationToken);
        var byCorrelation = await _sut.GetLogEntriesAsync(null, null, "agent-update-42", null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Hello world", Assert.Single(byMessage.Items).Message);
        Assert.Equal("Failure", Assert.Single(byCategory.Items).Message);
        Assert.Equal("agent-update-42", Assert.Single(byCorrelation.Items).CorrelationId);
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersByFileName()
    {
        await WriteAsync("a.json", Line("10:30:00", "Information", "From A"));
        await WriteAsync("b.json", Line("10:30:00", "Information", "From B"));

        var result = await _sut.GetLogEntriesAsync("a.json", null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Equal("From A", Assert.Single(result.Items).Message);
    }

    [Fact]
    public async Task GetLogEntriesAsync_Paginates()
    {
        await WriteAsync("big.json", [.. Enumerable.Range(1, 30).Select(i => Line($"10:{i:D2}:00.000", "Information", $"Line {i}"))]);

        var page1 = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        var page2 = await _sut.GetLogEntriesAsync(null, null, null, null, null, 2, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, page1.Items.Count);
        Assert.Equal(10, page2.Items.Count);
        Assert.Equal(30, page1.TotalCount);
        Assert.Equal("Line 30", page1.Items[0].Message);
        Assert.Equal("Line 20", page2.Items[0].Message);
    }

    [Fact]
    public async Task GetLogEntriesAsync_SplitsTheTwoTabsByTheOrganizationRules_WithContainsCategoryMatching()
    {
        await WriteAsync("app.json",
            Line("10:30:00", "Warning", "Slow request GET /api/a: 1500 ms", category: "Aetheus.Back.Middleware.SlowRequestDiagnosticsMiddleware"),
            Line("10:31:00", "Warning", "Slow request GET /api/b: 1600 ms", category: "Other"),
            Line("10:32:00", "Information", "Started"));
        var rules = new[]
        {
            new SystemLogExclusionRuleDto(1, 7, "Slow request", "SlowRequestDiagnostics", true, null, DateTime.UtcNow)
        };

        var kept = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50,
            TestContext.Current.CancellationToken, exclusionRules: rules, onlyExcluded: false);
        var moved = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50,
            TestContext.Current.CancellationToken, exclusionRules: rules, onlyExcluded: true);

        Assert.Equal(["Started", "Slow request GET /api/b: 1600 ms"], kept.Items.Select(entry => entry.Message));
        Assert.Equal("Slow request GET /api/a: 1500 ms", Assert.Single(moved.Items).Message);
    }

    [Fact]
    public async Task DownloadLogFileAsync_ValidFile_ReturnsReadableStream()
    {
        var content = "log data here";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.json"), content, cancellationToken: TestContext.Current.CancellationToken);

        await using var stream = await _sut.DownloadLogFileAsync("test.json", ct: TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        Assert.Equal(content, await reader.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadLogFileAsync_InvalidFileName_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.DownloadLogFileAsync("../../../etc/passwd", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadLogFileAsync_NotFound_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.DownloadLogFileAsync("nonexistent.json", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PurgeOldLogsAsync_DeletesOldFiles_AndKeepsTheRulesFile()
    {
        var oldFile = Path.Combine(_tempDir, "old.json");
        await WriteAsync("old.json", "{}");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));
        var rulesFile = Path.Combine(_tempDir, RulesFile);
        await WriteAsync(RulesFile, "{}");
        File.SetLastWriteTimeUtc(rulesFile, DateTime.UtcNow.AddDays(-10));
        var recentFile = Path.Combine(_tempDir, "recent.json");
        await WriteAsync("recent.json", "{}");

        var deleted = await _sut.PurgeOldLogsAsync(5, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(rulesFile));
        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public async Task PurgeOldLogsAsync_NoOldFiles_ReturnsZero()
    {
        await WriteAsync("recent.json", "{}");

        var deleted = await _sut.PurgeOldLogsAsync(5, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
    }

    /// <summary>The file Aetheus writes (<see cref="JsonFileLoggerProvider"/>, flat JSON) is read by the package's
    /// journal: level, category, message, exception and correlation id come back as logged.</summary>
    [Fact]
    public async Task TheFileTheAetheusLoggerWrites_IsReadByTheJournal()
    {
        using (var writer = new JsonFileLoggerProvider(_tempDir, LogLevel.Information, instance: "blue"))
        {
            var logger = writer.CreateLogger("Aetheus.Back.Tests.Category");
            logger.LogInformation("Deployed {Version}", "1.2.3");
            logger.Log(LogLevel.Error, new EventId(1), new InvalidOperationException("boom"), "Failed");
        }

        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Failed", "Deployed 1.2.3"], result.Items.Select(entry => entry.Message));
        var failed = result.Items[0];
        Assert.Equal("Error", failed.Level);
        Assert.Equal("Aetheus.Back.Tests.Category", failed.Category);
        Assert.Contains("boom", failed.Exception, StringComparison.Ordinal);
        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);
        Assert.StartsWith("aetheus-blue-", Assert.Single(files).FileName, StringComparison.Ordinal);
    }
}
