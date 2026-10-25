// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

public class ExecutorHelperTests
{
    [Fact]
    public async Task StreamOutputAsync_ReadsAllLines()
    {
        var lines = new List<string>();
        var reader = CreateStreamReader("line1\nline2\nline3\n");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Info,
            (msg, level) => { lines.Add(msg); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, lines.Count);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line2", lines[1]);
        Assert.Equal("line3", lines[2]);
    }

    [Fact]
    public async Task StreamOutputAsync_PassesCorrectLevel()
    {
        var levelsReceived = new List<TaskLogLevel>();
        var reader = CreateStreamReader("test\n");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Info,
            (msg, level) => { levelsReceived.Add(level); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Single(levelsReceived);
        Assert.Equal(TaskLogLevel.Info, levelsReceived[0]);
    }

    // R-36: standard error is a channel, not a verdict. An unmarked line is a Warning (visible, not red) and
    // keeps the mark of its channel; nothing is dropped.
    [Fact]
    public async Task StreamOutputAsync_UnmarkedStderrLine_IsAWarningAndKeepsItsChannelMark()
    {
        var received = new List<(string Text, TaskLogLevel Level)>();
        var reader = CreateStreamReader("Volume aetheus-qa-rollback-2848-x Creating\n");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Error,
            (msg, level) => { received.Add((msg, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(received);
        Assert.Equal(TaskLogLevel.Warning, entry.Level);
        Assert.Equal("[stderr] Volume aetheus-qa-rollback-2848-x Creating", entry.Text);
    }

    [Fact]
    public async Task StreamOutputAsync_StderrLineThatStatesAnError_StaysAnErrorUntouched()
    {
        var received = new List<(string Text, TaskLogLevel Level)>();
        var reader = CreateStreamReader("ERROR the scan failed\n");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Error,
            (msg, level) => { received.Add((msg, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(received);
        Assert.Equal(TaskLogLevel.Error, entry.Level);
        Assert.Equal("ERROR the scan failed", entry.Text);
    }

    [Fact]
    public async Task StreamOutputAsync_EmptyStream_NoCallbacks()
    {
        var callCount = 0;
        var reader = CreateStreamReader("");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Info,
            (msg, level) => { callCount++; return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task StreamOutputAsync_CancellationRequested_StopsReading()
    {
        using var cts = new CancellationTokenSource();
        var lines = new List<string>();
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var clientPipe = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In,
            pipe.ClientSafePipeHandle);
        using var reader = new StreamReader(clientPipe);

        var task = ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Info,
            (msg, level) => { lines.Add(msg); return Task.CompletedTask; },
            cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(pipe.IsConnected);
        Assert.Empty(lines);
    }

    // Chantier D: benign journalctl notices on stderr (level Error) are downgraded to Info.
    [Theory]
    [InlineData("Hint: You are currently not seeing messages from other users and the system.")]
    [InlineData("      Pass -q to disable this notice.")]
    [InlineData("Pass --quiet to turn off this notice.")]
    [InlineData("-- No entries --")]
    [InlineData("No journal files were opened due to insufficient permissions.")]
    public async Task StreamOutputAsync_BenignJournalctlNotice_DowngradedToInfo(string line)
    {
        var levels = new List<TaskLogLevel>();
        var reader = CreateStreamReader(line + "\n");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Error,
            (msg, level) => { levels.Add(level); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Single(levels);
        Assert.Equal(TaskLogLevel.Info, levels[0]);
    }

    [Fact]
    public async Task StreamOutputAsync_GenuineError_StaysError()
    {
        var levels = new List<TaskLogLevel>();
        var reader = CreateStreamReader("error: Connection refused while contacting the database\n");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Error,
            (msg, level) => { levels.Add(level); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Single(levels);
        Assert.Equal(TaskLogLevel.Error, levels[0]);
    }

    [Fact]
    public async Task StreamOutputAsync_BenignNoticeOnStdout_StaysInfo()
    {
        // A "Hint:" arriving on stdout (level Info) is never reconsidered - only Error lines are.
        var levels = new List<TaskLogLevel>();
        var reader = CreateStreamReader("Hint: this is an stdout hint\n");

        await ExecutorHelper.StreamOutputAsync(reader, TaskLogLevel.Info,
            (msg, level) => { levels.Add(level); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Single(levels);
        Assert.Equal(TaskLogLevel.Info, levels[0]);
    }

    private static StreamReader CreateStreamReader(string content)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return new StreamReader(stream);
    }
}
