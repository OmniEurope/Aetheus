// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// The 2 s poll fallback of a running step's logs, when the log hub cannot be reached, paced by a
/// simulated clock: each tick reads the task's tail into the window, a failed read is skipped, and a
/// tail for a step no longer selected is never applied.
/// </summary>
public sealed class PipelineRunLogPollTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly ObservedFakeClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly PipelineRunLogWindow _window = new();
    private readonly StateSignal _rendered = new();
    private int _renders;

    public PipelineRunLogPollTests() => _handler = BunitTestHelper.RegisterServices(this);

    private PipelineRunLogStreamer Streamer() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        NullLogger.Instance,
        _window,
        callback => callback(),
        () => { Interlocked.Increment(ref _renders); _rendered.Pulse(); },
        _clock);

    private static PipelineStepRunDto Running(int taskId) =>
        new() { Id = taskId, TaskId = taskId, Status = TaskExecutionStatus.Running };

    private int TailReads(int taskId) =>
        _handler.Requests.ToList().Count(request => request.Url.Contains($"api/logs/task/{taskId}?", StringComparison.Ordinal));

    /// <summary>Moves the clock one poll interval at a time, once the poll's timer exists, each time
    /// after the previous tick's read was sent, until <paramref name="expectedReads"/> tails were read.</summary>
    private async Task TickAsync(int taskId, int expectedReads)
    {
        await _clock.WhenAsync(() => _clock.Pending(PollInterval) == 1, "the poll timer armed");
        while (TailReads(taskId) < expectedReads)
        {
            var reads = TailReads(taskId);
            _clock.Advance(PollInterval);
            await _handler.WhenRequestedAsync(
                request => request.Url.Contains($"api/logs/task/{taskId}?", StringComparison.Ordinal),
                reads + 1,
                $"tail read {reads + 1} of task {taskId}");
        }
    }

    [Fact]
    public async Task EachTick_ReadsTheTailIntoTheWindow()
    {
        _handler.SetJsonResponse("api/logs/task/30", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 30, Message = "restoring", Timestamp = DateTime.UtcNow }
        });
        await using var streamer = Streamer();

        await streamer.UpdateAsync(Running(30));
        await TickAsync(30, 1);

        // The window is filled, then the page told to render, by the tick that read the tail.
        await _rendered.WhenAsync(() => Volatile.Read(ref _renders) > 0, "the tail applied to the window");
        Assert.Equal(["restoring"], _window.Lines[30].Select(line => line.Message));
        Assert.True(Volatile.Read(ref _renders) > 0);
    }

    [Fact]
    public async Task AFailedRead_IsSkipped_AndTheNextTickReadsAgain()
    {
        _handler.SetResponse("api/logs/task/31", System.Net.HttpStatusCode.InternalServerError);
        await using var streamer = Streamer();

        await streamer.UpdateAsync(Running(31));
        await TickAsync(31, 2);

        Assert.False(_window.Lines.ContainsKey(31));
        Assert.Equal(0, Volatile.Read(ref _renders));
    }

    [Fact]
    public async Task AfterTheSelectionMoved_TheOldTaskIsNoLongerPolled()
    {
        _handler.SetJsonResponse("api/logs/task/32", new List<TaskLogDto> { new() { Id = 1, TaskId = 32, Message = "old" } });
        _handler.SetJsonResponse("api/logs/task/33", new List<TaskLogDto> { new() { Id = 2, TaskId = 33, Message = "new" } });
        await using var streamer = Streamer();

        await streamer.UpdateAsync(Running(32));
        await TickAsync(32, 1);
        await streamer.UpdateAsync(Running(33));
        var oldReads = TailReads(32);
        await TickAsync(33, 2);

        Assert.Equal(oldReads, TailReads(32));
        Assert.Contains(_window.Lines[33], line => line.Message == "new");
    }
}
