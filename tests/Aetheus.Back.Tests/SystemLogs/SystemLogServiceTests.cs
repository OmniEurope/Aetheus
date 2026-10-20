// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests;

public class SystemLogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SystemLogService _sut;

    public SystemLogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aetheus_log_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:FileLog:Directory"] = _tempDir
            })
            .Build();

        _sut = new SystemLogService(config, NullLogger<SystemLogService>.Instance, TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task GetLogFilesAsync_EmptyDir_ReturnsEmpty()
    {
        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Empty(files);
    }

    [Fact]
    public async Task GetLogFilesAsync_WithLogFiles_ReturnsSorted()
    {
        var oldPath = Path.Combine(_tempDir, "old.log");
        var newPath = Path.Combine(_tempDir, "new.log");
        await File.WriteAllTextAsync(oldPath, "old data", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(newPath, "new data", cancellationToken: TestContext.Current.CancellationToken);
        // Set explicit, well-separated mtimes instead of relying on a Task.Delay to space them out
        // (a real-clock race: flaky on hosts with coarse filesystem mtime granularity). Matches the
        // deterministic approach already used by PurgeOldLogsAsync_DeletesOldFiles.
        File.SetLastWriteTimeUtc(oldPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, files.Count);
        Assert.Equal("new.log", files[0].FileName);
    }

    [Fact]
    public async Task GetLogFilesAsync_IgnoresNonLogFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "readme.txt"), "not a log", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"), "log data", cancellationToken: TestContext.Current.CancellationToken);

        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(files);
        Assert.Equal("app.log", files[0].FileName);
    }

    [Fact]
    public async Task GetLogFilesAsync_IncludesJsonFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "structured.json"), "{}", cancellationToken: TestContext.Current.CancellationToken);

        var files = await _sut.GetLogFilesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(files);
        Assert.Equal("structured.json", files[0].FileName);
    }

    [Fact]
    public async Task GetLogEntriesAsync_EmptyDir_ReturnsEmpty()
    {
        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetLogEntriesAsync_ParsesPlainTextLines()
    {
        var logContent = "2025-01-15 10:30:00.123 [INF] Test message\n2025-01-15 10:31:00.456 [ERR] Error happened\n";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"), logContent, cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.True(result.TotalCount >= 2);
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersByLevel()
    {
        var logContent = "2025-01-15 10:30:00.123 [INF] Info message\n2025-01-15 10:31:00.456 [ERR] Error message\n";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"), logContent, cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync(null, "ERR", null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.All(result.Items, e => Assert.Equal("Error", e.Level));
    }

    /// <summary>The virtualized grid sends its header filters and its sort; they apply to the whole
    /// filtered log before paging, not to one block.</summary>
    [Fact]
    public async Task GetLogEntriesAsync_AppliesColumnFiltersAndSortBeforePaging()
    {
        var logContent = "2025-01-15 10:30:00.123 [INF] alpha one\n2025-01-15 10:31:00.456 [ERR] beta two\n"
            + "2025-01-15 10:32:00.789 [INF] alpha three\n";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"), logContent, cancellationToken: TestContext.Current.CancellationToken);

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
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"), "2025-01-15 10:30:00.123 [INF] alpha\n",
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50,
            TestContext.Current.CancellationToken, sortBy: "Exception"));
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersBySearch()
    {
        var logContent = "2025-01-15 10:30:00.123 [INF] Hello world\n2025-01-15 10:31:00.456 [INF] Goodbye world\n";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"), logContent, cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync(null, null, "Hello", null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.All(result.Items, e => Assert.Contains("Hello", e.Message));
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersByCorrelationId()
    {
        const string json = "{\"Timestamp\":\"2025-01-15T10:30:00.123Z\",\"Level\":\"Error\",\"Message\":\"Failure\",\"SourceContext\":\"App\",\"CorrelationId\":\"agent-update-42\"}\n";
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "structured.json"),
            json,
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync(
            null, null, "agent-update-42", null, null, 1, 50,
            ct: TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Items);
        Assert.Equal("agent-update-42", entry.CorrelationId);
    }

    [Fact]
    public async Task GetLogEntriesAsync_FiltersByFileName()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "a.log"), "2025-01-15 10:30:00.123 [INF] From A\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "b.log"), "2025-01-15 10:30:00.123 [INF] From B\n", cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync("a.log", null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.All(result.Items, e => Assert.Contains("From A", e.Message));
    }

    [Fact]
    public async Task GetLogEntriesAsync_Paginates()
    {
        var lines = string.Join("\n",
            Enumerable.Range(1, 30).Select(i => $"2025-01-15 10:{i:D2}:00.000 [INF] Line {i}"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "big.log"), lines, cancellationToken: TestContext.Current.CancellationToken);

        var page1 = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        var page2 = await _sut.GetLogEntriesAsync(null, null, null, null, null, 2, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, page1.Items.Count);
        Assert.Equal(10, page2.Items.Count);
        Assert.Equal(30, page1.TotalCount);
    }

    [Fact]
    public async Task DownloadLogFileAsync_ValidFile_ReturnsReadableStream()
    {
        var content = "log data here";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.log"), content, cancellationToken: TestContext.Current.CancellationToken);

        await using var stream = await _sut.DownloadLogFileAsync("test.log", ct: TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        Assert.Equal(content, await reader.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.IsType<FileStream>(stream);
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
            _sut.DownloadLogFileAsync("nonexistent.log", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PurgeOldLogsAsync_DeletesOldFiles()
    {
        var oldFile = Path.Combine(_tempDir, "old.log");
        await File.WriteAllTextAsync(oldFile, "old data", cancellationToken: TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));

        var recentFile = Path.Combine(_tempDir, "recent.log");
        await File.WriteAllTextAsync(recentFile, "recent data", cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _sut.PurgeOldLogsAsync(5, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public async Task PurgeOldLogsAsync_NoOldFiles_ReturnsZero()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "recent.log"), "data", cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _sut.PurgeOldLogsAsync(5, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task GetLogEntriesAsync_ParsesJsonLines()
    {
        var json = "{\"Timestamp\":\"2025-01-15T10:30:00.123Z\",\"Level\":\"Information\",\"Message\":\"JSON log entry\",\"SourceContext\":\"App\"}\n";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "structured.json"), json, cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Items);
    }

    [Theory]
    [InlineData("[VRB] m", "Verbose", "m")]
    [InlineData("[VERBOSE] m", "Verbose", "m")]
    [InlineData("[DBG] m", "Debug", "m")]
    [InlineData("[DEBUG] m", "Debug", "m")]
    [InlineData("[INF] m", "Information", "m")]
    [InlineData("[INFORMATION] m", "Information", "m")]
    [InlineData("[WRN] m", "Warning", "m")]
    [InlineData("[WARNING] m", "Warning", "m")]
    [InlineData("[ERR]   m", "Error", "m")]
    [InlineData("[ERROR] m", "Error", "m")]
    [InlineData("[FTL] m", "Fatal", "m")]
    [InlineData("[FATAL] m", "Fatal", "m")]
    [InlineData("[wrn] m", "wrn", "m")]
    [InlineData("[CUSTOM] m", "CUSTOM", "m")]
    [InlineData("no level tag", "Information", "no level tag")]
    [InlineData("[unclosed tag", "Information", "[unclosed tag")]
    public async Task GetLogEntriesAsync_PlainTextLine_ReadsTimestampLevelAndMessage(
        string afterTimestamp, string expectedLevel, string expectedMessage)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"),
            "2025-01-15 10:30:00.123 " + afterTimestamp + "\n", cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Items);
        Assert.Equal(new DateTime(2025, 1, 15, 10, 30, 0, 123, DateTimeKind.Utc), entry.Timestamp.ToUniversalTime());
        Assert.Equal(expectedLevel, entry.Level);
        Assert.Equal(expectedMessage, entry.Message);
    }

    [Fact]
    public async Task GetLogEntriesAsync_PlainTextLineWithoutTimestamp_KeepsTheWholeLineAsInformation()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "app.log"),
            "[ERR] not a timestamped line\n", cancellationToken: TestContext.Current.CancellationToken);

        var result = await _sut.GetLogEntriesAsync(null, null, null, null, null, 1, 50, ct: TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Items);
        Assert.Equal("Information", entry.Level);
        Assert.Equal("[ERR] not a timestamped line", entry.Message);
    }
}
