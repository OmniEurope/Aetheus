// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-8: a move of a scheduled action is written where its sort keeps its schedule, through that sort's
/// own endpoint, in the form of its sort (five fields for a scan and a backup, six for an AI task). The HTTP calls are
/// recorded by a handler: this proves the request the page sends, not the answer of the backend (the HTTP integration
/// test of the planner proves that one).</summary>
public sealed class ScheduledActionWriterTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(Status);
        }
    }

    private static (ApiClient Api, RecordingHandler Handler) Client(HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler { Status = status };
        return (new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    private static ScheduledServerActionDto Action(ServerActionKind kind, int sourceId) =>
        new() { Id = $"{kind}:{sourceId}", Kind = kind, SourceId = sourceId, ServerId = 99 };

    [Fact]
    public async Task AScan_IsStoredThroughTheRkhunterScheduleOfItsServer()
    {
        var (api, handler) = Client();

        var stored = await ScheduledActionWriter.StoreAsync(api, Action(ServerActionKind.RkhunterScan, 7), "30 3 * * *", TestContext.Current.CancellationToken);

        Assert.True(stored.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/servers/7/rkhunter/schedule", request.Path);
        Assert.Contains("\"cronExpression\":\"30 3 * * *\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAiTask_IsStoredThroughItsOwnScheduleEndpoint_WithTheSixFieldExpression()
    {
        var (api, handler) = Client();

        var stored = await ScheduledActionWriter.StoreAsync(api, Action(ServerActionKind.AiTask, 3), "0 30 3 * * *", TestContext.Current.CancellationToken);

        Assert.True(stored.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        // The task's id, never the id of the server the card names.
        Assert.Equal("/api/ai/tasks/3/schedule", request.Path);
        Assert.Contains("\"schedule\":\"0 30 3 * * *\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackup_IsStoredThroughItsOwnScheduleEndpoint_WithTheFiveFieldCron()
    {
        var (api, handler) = Client();

        var stored = await ScheduledActionWriter.StoreAsync(api, Action(ServerActionKind.AppBackup, 5), "15 1 * * *", TestContext.Current.CancellationToken);

        Assert.True(stored.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/backups/5/schedule", request.Path);
        Assert.Contains("\"scheduleCron\":\"15 1 * * *\"", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ARefusedWrite_IsReportedAsNotStored(HttpStatusCode status)
    {
        var (api, _) = Client(status);

        var stored = await ScheduledActionWriter.StoreAsync(api, Action(ServerActionKind.AiTask, 3), "0 0 2 * * *", TestContext.Current.CancellationToken);

        Assert.False(stored.Success);
    }

    [Theory]
    [InlineData(ServerActionKind.RkhunterScan, "30 3 * * *", "0 30 3 * * *")]
    [InlineData(ServerActionKind.AppBackup, "15 1 * * *", "0 15 1 * * *")]
    [InlineData(ServerActionKind.AiTask, "0 30 3 * * *", "0 30 3 * * *")]
    public void TheStoredFormOfEachSort_RoundTripsThroughTheEngineFields(ServerActionKind kind, string stored, string engine)
    {
        Assert.Equal(engine, ServerActionDefaults.ToEngineFields(kind, stored));
        Assert.Equal(stored, ServerActionDefaults.ToStoredFields(kind, engine));
    }
}
