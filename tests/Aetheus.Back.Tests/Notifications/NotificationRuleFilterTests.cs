// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>The optional <c>FilterJson</c> of a channel rule is applied when an event is sent to its channel.</summary>
public class NotificationRuleFilterTests
{
    private readonly INotificationRepository _repo = Substitute.For<INotificationRepository>();
    private readonly INotificationEmailer _emailer = Substitute.For<INotificationEmailer>();
    private readonly CapturingLogger _logger = new();
    private readonly NotificationService _sut;

    public NotificationRuleFilterTests()
    {
        _repo.GetActiveAdministratorEmailsAsync(Arg.Any<CancellationToken>()).Returns(["admin@example.org"]);
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Sent("no-reply@example.org")]);
        _sut = new NotificationService(
            _repo,
            Substitute.For<IAuditService>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(),
            Substitute.For<IHttpClientFactory>(),
            _logger,
            Substitute.For<Aetheus.Back.Services.IEncryptionService>(),
            TimeProvider.System,
            Substitute.For<IUserNotificationService>(),
            _emailer);
    }

    private void GivenRuleWithFilter(string? filterJson) =>
        _repo.GetRulesForEventAsync("pipeline.failed", Arg.Any<CancellationToken>()).Returns(
        [
            new NotificationRule
            {
                Id = 5,
                EventType = "pipeline.failed",
                NotificationChannelId = 1,
                FilterJson = filterJson,
                Channel = new NotificationChannel { Id = 1, Name = "ops-mail", Type = NotificationChannelType.Email, ConfigurationJson = "{}" }
            }
        ]);

    private Task SendFailureOfProjectAsync(int projectId) =>
        _sut.SendEventAsync(
            "pipeline.failed", new { PipelineName = "nightly", ProjectId = projectId, Status = "Failed" },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task FilterThatDoesNotMatch_SendsNothingToTheChannel()
    {
        GivenRuleWithFilter("""{"projectId":7}""");

        await SendFailureOfProjectAsync(8);

        await _emailer.DidNotReceive().SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FilterThatMatches_SendsToTheChannel()
    {
        GivenRuleWithFilter("""{"projectId":7,"status":["Failed","Rejected"]}""");

        await SendFailureOfProjectAsync(7);

        await _emailer.Received(1).SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public async Task AbsentOrEmptyFilter_KeepsTodaysBehaviour(string? filterJson)
    {
        GivenRuleWithFilter(filterJson);

        await SendFailureOfProjectAsync(8);

        await _emailer.Received(1).SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"projectId":{"nested":1}}""")]
    public async Task InvalidFilter_SkipsTheRuleWithAWarning_AndDoesNotThrow(string filterJson)
    {
        GivenRuleWithFilter(filterJson);

        await SendFailureOfProjectAsync(7);

        await _emailer.DidNotReceive().SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidFilterOfOneRule_DoesNotStopTheOtherRules()
    {
        _repo.GetRulesForEventAsync("pipeline.failed", Arg.Any<CancellationToken>()).Returns(
        [
            new NotificationRule
            {
                Id = 1, EventType = "pipeline.failed", NotificationChannelId = 1, FilterJson = "{broken",
                Channel = new NotificationChannel { Id = 1, Name = "broken", Type = NotificationChannelType.Email, ConfigurationJson = "{}" }
            },
            new NotificationRule
            {
                Id = 2, EventType = "pipeline.failed", NotificationChannelId = 2, FilterJson = null,
                Channel = new NotificationChannel { Id = 2, Name = "plain", Type = NotificationChannelType.Email, ConfigurationJson = "{}" }
            }
        ]);

        await SendFailureOfProjectAsync(7);

        await _emailer.Received(1).SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"PROJECTID":7}""", true)]
    [InlineData("""{"projectId":"7"}""", true)]
    [InlineData("""{"status":"failed"}""", true)]
    [InlineData("""{"projectId":7,"status":"Success"}""", false)]
    [InlineData("""{"missing":"x"}""", false)]
    [InlineData("""{"missing":null}""", true)]
    [InlineData("""{"projectId":[1,2,7]}""", true)]
    [InlineData("""{"projectId":[1,2]}""", false)]
    public void Evaluate_ComparesTopLevelPayloadProperties(string filter, bool matches)
    {
        var verdict = NotificationRuleFilter.Evaluate(filter, """{"PipelineName":"nightly","ProjectId":7,"Status":"Failed"}""");

        Assert.Equal(matches ? NotificationRuleFilterVerdict.Match : NotificationRuleFilterVerdict.NoMatch, verdict);
    }

    private sealed class CapturingLogger : ILogger<NotificationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
