// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Extensions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.Services;

public class MetricsAndFactoryTests
{
    // --- MetricsController ---

    [Fact]
    public void MetricsController_Get_ReturnsOpenMetricsText()
    {
        var mockQueue = Substitute.For<IBackgroundTaskQueue>();
        mockQueue.CurrentLength.Returns(5);
        mockQueue.Capacity.Returns(1024);
        mockQueue.Dropped.Returns(2);
        mockQueue.Enqueued.Returns(100);
        mockQueue.Processed.Returns(93);

        var controller = new MetricsController(mockQueue);
        var result = controller.Get();

        Assert.IsType<ContentResult>(result);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("text/plain", result.ContentType!);
        Assert.Contains("aetheus_backgroundqueue_length 5", result.Content!);
        Assert.Contains("aetheus_backgroundqueue_capacity 1024", result.Content!);
        Assert.Contains("aetheus_backgroundqueue_dropped_total 2", result.Content!);
        Assert.Contains("aetheus_backgroundqueue_enqueued_total 100", result.Content!);
        Assert.Contains("aetheus_backgroundqueue_processed_total 93", result.Content!);
        Assert.Contains("# HELP", result.Content!);
        Assert.Contains("# TYPE", result.Content!);
    }

    [Fact]
    public void MetricsController_Get_ZeroValues()
    {
        var mockQueue = Substitute.For<IBackgroundTaskQueue>();
        mockQueue.CurrentLength.Returns(0);
        mockQueue.Capacity.Returns(1024);
        mockQueue.Dropped.Returns(0);
        mockQueue.Enqueued.Returns(0);
        mockQueue.Processed.Returns(0);

        var controller = new MetricsController(mockQueue);
        var result = controller.Get();

        Assert.Contains("aetheus_backgroundqueue_length 0", result.Content!);
        Assert.Contains("gauge", result.Content!);
        Assert.Contains("counter", result.Content!);
    }

    // --- BackgroundTaskQueue ---

    [Fact]
    public void BackgroundTaskQueue_InitialState()
    {
        var queue = new BackgroundTaskQueue();
        Assert.Equal(1024, queue.Capacity);
        Assert.Equal(0, queue.CurrentLength);
        Assert.Equal(0, queue.Dropped);
        Assert.Equal(0, queue.Enqueued);
        Assert.Equal(0, queue.Processed);
    }

    [Fact]
    public void BackgroundTaskQueue_Enqueue_IncrementsCounters()
    {
        var queue = new BackgroundTaskQueue();
        queue.Enqueue((_, _) => Task.CompletedTask);

        Assert.Equal(1, queue.CurrentLength);
        Assert.Equal(1, queue.Enqueued);
        Assert.Equal(0, queue.Processed);
    }

    [Fact]
    public void BackgroundTaskQueue_IncrementProcessed()
    {
        var queue = new BackgroundTaskQueue();
        queue.IncrementProcessed();
        Assert.Equal(1, queue.Processed);
    }

    [Fact]
    public void BackgroundTaskQueue_Enqueue_NullThrows()
    {
        var queue = new BackgroundTaskQueue();
        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
    }

    [Fact]
    public async Task BackgroundTaskQueue_DequeueAsync_ReturnsEnqueuedItem()
    {
        var queue = new BackgroundTaskQueue();
        var executed = false;
        Func<IServiceProvider, CancellationToken, Task> work = (_, _) => { executed = true; return Task.CompletedTask; };

        queue.Enqueue(work);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var dequeued = await queue.DequeueAsync(cts.Token);

        await dequeued(null!, TestContext.Current.CancellationToken);
        Assert.True(executed);
    }

    [Fact]
    public void BackgroundTaskQueue_ConcurrentOverflow_CountsExactEvictions()
    {
        var queue = new BackgroundTaskQueue();
        for (var i = 0; i < queue.Capacity; i++)
            queue.Enqueue((_, _) => Task.CompletedTask);

        const int concurrentWrites = 256;
        Parallel.For(0, concurrentWrites, _ => queue.Enqueue((_, _) => Task.CompletedTask));

        Assert.Equal(queue.Capacity, queue.CurrentLength);
        Assert.Equal(concurrentWrites, queue.Dropped);
        Assert.Equal(queue.Capacity + concurrentWrites, queue.Enqueued);
    }

    // --- ServerTaskFactory ---

    [Fact]
    public void ServerTaskFactory_Shell_CreatesCorrectly()
    {
        var task = ServerTaskFactory.Shell(1, "Deploy", "deploy.sh", 120);

        Assert.Equal(1, task.ServerId);
        Assert.Equal("Deploy", task.Name);
        Assert.Equal("deploy.sh", task.Command);
        Assert.Equal(ExecutorType.Shell, task.Executor);
        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
        Assert.Equal(120, task.TimeoutSeconds);
    }

    [Fact]
    public void ServerTaskFactory_Shell_DefaultTimeout()
    {
        var task = ServerTaskFactory.Shell(1, "Test", "test.sh");
        Assert.Equal(60, task.TimeoutSeconds);
    }

    [Fact]
    public void ServerTaskFactory_Create_WithExecutor()
    {
        var task = ServerTaskFactory.Create(2, "Docker", "restart nginx", ExecutorType.Docker, 300);

        Assert.Equal(2, task.ServerId);
        Assert.Equal("Docker", task.Name);
        Assert.Equal(ExecutorType.Docker, task.Executor);
        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
        Assert.Equal(300, task.TimeoutSeconds);
    }

    [Fact]
    public void ServerTaskFactory_Create_DefaultTimeout()
    {
        var task = ServerTaskFactory.Create(1, "Op", "cmd", ExecutorType.Shell);
        Assert.Equal(60, task.TimeoutSeconds);
    }

    // --- HostUrlExtensions ---

    [Fact]
    public void HostUrlExtensions_ReturnsConfiguredValue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aetheus:PublicApiBaseUrl"] = "https://api.example.com/"
            })
            .Build();

        var httpContext = new DefaultHttpContext();
        var result = HostUrlExtensions.ResolvePublicApiUrl(config, httpContext);
        Assert.Equal("https://api.example.com", result);
    }

    [Fact]
    public void HostUrlExtensions_FallsBackToRequest()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost", 5301);

        var result = HostUrlExtensions.ResolvePublicApiUrl(config, httpContext);
        Assert.Equal("https://localhost:5301", result);
    }

    [Fact]
    public void HostUrlExtensions_EmptyConfigFallsBack()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aetheus:PublicApiBaseUrl"] = ""
            })
            .Build();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("myhost");

        var result = HostUrlExtensions.ResolvePublicApiUrl(config, httpContext);
        Assert.Equal("http://myhost", result);
    }
}
