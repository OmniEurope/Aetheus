// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineYamlEditorCoordinatorTests
{
    [Fact]
    public async Task LoadSuggestionsAsync_UsesAggregateKeyPagesWithoutLibraryDetails()
    {
        var handler = new SuggestionHandler();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test/") });
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        var sut = new PipelineYamlEditorCoordinator(api, localizer, NullLogger.Instance);

        await sut.LoadSuggestionsAsync(7, null);

        Assert.Contains(handler.Requests, uri =>
            uri.Contains("suggestion-keys", StringComparison.Ordinal)
            && uri.Contains("page=2", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, uri =>
            uri.Contains("api/variable-libraries/", StringComparison.Ordinal)
            && !uri.Contains("names", StringComparison.Ordinal)
            && !uri.Contains("suggestion-keys", StringComparison.Ordinal));
    }

    // SITE-FUNC-AUTOCOMPLETE-001: a network failure on one suggestion lookup threw out of
    // OnAfterRenderAsync and replaced the whole pipeline form with the error boundary.
    [Fact]
    public async Task LoadSuggestionsAsync_NetworkFailure_ReturnsNoSuggestionsAndLogs()
    {
        var handler = new SuggestionHandler { FailingSegment = "api/vaults/names" };
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test/") });
        var logger = Substitute.For<ILogger>();
        var sut = new PipelineYamlEditorCoordinator(api, Substitute.For<IStringLocalizer<AppStrings>>(), logger);

        var (serverNames, libraryNames, vaultNames) = await sut.LoadSuggestionsAsync(7, null);

        Assert.Empty(serverNames);
        Assert.Empty(libraryNames);
        Assert.Empty(vaultNames);
        logger.Received(1).Log(LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(),
            Arg.Any<HttpRequestException>(), Arg.Any<Func<object, Exception?, string>>());
    }

    private sealed class SuggestionHandler : HttpMessageHandler
    {
        public string? FailingSegment { get; init; }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add(uri);
            if (FailingSegment is not null && uri.Contains(FailingSegment, StringComparison.Ordinal))
                throw new HttpRequestException("network error");
            object payload = uri.Contains("suggestion-keys", StringComparison.Ordinal)
                ? uri.Contains("page=2", StringComparison.Ordinal)
                    ? new PaginatedResult<string>
                    {
                        Items = ["KEY-201"],
                        TotalCount = 201,
                        Page = 2,
                        PageSize = 200
                    }
                    : new PaginatedResult<string>
                    {
                        Items = Enumerable.Range(1, 200).Select(index => $"KEY-{index}").ToList(),
                        TotalCount = 201,
                        Page = 1,
                        PageSize = 200
                    }
                : new List<string>();
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
