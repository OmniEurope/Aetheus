// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.AiTasks;

/// <summary>A scheduled AI task runs once per occurrence that fell in the last two minutes, records it,
/// and a bad schedule or a failed launch never stops the other definitions.</summary>
public sealed class AiTaskSchedulerServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 10, 0, 30, DateTimeKind.Utc);
    private const string Hourly = "0 0 * * * *";

    [Fact]
    public void DueOccurrence_IsTheRecentOccurrenceNotYetRun()
    {
        var due = AiTaskSchedulerService.DueOccurrence(new AiTaskDefinition { Schedule = Hourly }, Now);

        Assert.Equal(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc), due);
    }

    [Fact]
    public void DueOccurrence_AlreadyRun_IsNotDueAgain()
    {
        var definition = new AiTaskDefinition { Schedule = Hourly, LastScheduledAt = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc) };

        Assert.Null(AiTaskSchedulerService.DueOccurrence(definition, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0 30 * * * *")]
    public void DueOccurrence_NoScheduleOrNoOccurrenceInTheWindow_IsNotDue(string? schedule) =>
        Assert.Null(AiTaskSchedulerService.DueOccurrence(new AiTaskDefinition { Schedule = schedule }, Now));

    [Fact]
    public async Task Tick_RunsTheDueDefinitionsAndSkipsABrokenOne()
    {
        var broken = new AiTaskDefinition { Id = 1, Schedule = "not a cron" };
        var failing = new AiTaskDefinition { Id = 2, Schedule = Hourly };
        var due = new AiTaskDefinition { Id = 3, Schedule = Hourly };
        var notDue = new AiTaskDefinition { Id = 4, Schedule = "0 30 * * * *" };
        var repo = Substitute.For<IAiTaskRepository>();
        repo.GetScheduledDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([broken, failing, due, notDue]);
        var service = Substitute.For<IAiTaskService>();
        service.RunNowAsync(2, null, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("no runner"));
        service.RunNowAsync(3, null, Arg.Any<CancellationToken>()).Returns(new ServerTask { Id = 30 });
        var services = new ServiceCollection().AddSingleton(repo).AddSingleton(service).BuildServiceProvider();
        var sut = new AiTaskSchedulerService(
            services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(new DateTimeOffset(Now)),
            NullLogger<AiTaskSchedulerService>.Instance);

        await sut.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(broken.LastScheduledAt);
        Assert.Null(failing.LastScheduledAt);
        Assert.Equal(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc), due.LastScheduledAt);
        Assert.Null(notDue.LastScheduledAt);
        await service.DidNotReceive().RunNowAsync(4, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
