// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniEurope.Blazor.Components;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>
/// R-47: reading the parameters of a launch never stops in silence and never stops with two toasts. The localizer
/// of the test host answers each key with itself, so a toast detail names the sentence that was chosen.
/// </summary>
public sealed class RunParametersReadingTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public RunParametersReadingTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private NotifyHelper Toast => Services.GetRequiredService<NotifyHelper>();

    private IStringLocalizer<Aetheus.Front.Resources.AppStrings> Localizer => Services.GetRequiredService<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>();

    private Task<List<PipelineRunParameterDto>?> ReadAsync(ApiClient? api = null, string? branch = null) =>
        RunParametersReading.ReadOrNotifyAsync(api ?? Services.GetRequiredService<ApiClient>(), Toast, Localizer, 12, branch);

    [Fact]
    public async Task AnAnsweredRead_ReturnsTheDeclaredParameters_WithoutAToast()
    {
        _handler.SetJsonResponse("api/pipelines/12/parameters", new List<PipelineRunParameterDto>
        {
            new() { Name = "version", DisplayName = "Version", Required = true }
        });

        var declared = await ReadAsync();

        var parameter = Assert.Single(declared!);
        Assert.Equal("version", parameter.Name);
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public async Task TheBranchIsSentInTheRequest()
    {
        _handler.SetJsonResponse("api/pipelines/12/parameters", new List<PipelineRunParameterDto>());

        await ReadAsync(branch: "feature/x");

        Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.Contains("sourceBranch=feature%2Fx", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "PreflightSessionExpired")]
    [InlineData(HttpStatusCode.Forbidden, "PreflightForbidden")]
    [InlineData(HttpStatusCode.NotFound, "RunNotFound")]
    [InlineData(HttpStatusCode.InternalServerError, "RunParametersReadFailed")]
    [InlineData(HttpStatusCode.BadGateway, "RunParametersReadFailed")]
    [InlineData(HttpStatusCode.BadRequest, "RunParametersReadFailed")]
    public async Task AFailedRead_ShowsExactlyOneDangerToast_WithTheSentenceOfItsStatus(HttpStatusCode status, string key)
    {
        _handler.SetResponse("api/pipelines/12/parameters", status);

        var declared = await ReadAsync();

        Assert.Null(declared);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Equal(key, toast.Detail);
    }

    [Fact]
    public async Task ABadRequestWithAReason_ShowsThatReasonInsteadOfAGenericSentence()
    {
        _handler.SetRawResponse(
            HttpMethod.Get, "api/pipelines/12/parameters", "{\"message\":\"Invalid pipeline YAML at line 4\"}", "application/json", HttpStatusCode.BadRequest);

        var declared = await ReadAsync();

        Assert.Null(declared);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("Invalid pipeline YAML at line 4", toast.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransportFailure_ShowsTheUnreachableSentence_Once()
    {
        var api = new ApiClient(new HttpClient(new ThrowingHandler(new HttpRequestException("down"))) { BaseAddress = new Uri("http://test/") });

        var declared = await ReadAsync(api);

        Assert.Null(declared);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Equal("RunParametersServerUnreachable", toast.Detail);
    }

    [Fact]
    public async Task ABodyThatIsNotJson_IsReportedAsUnreachable_NotThrown()
    {
        _handler.SetRawResponse("api/pipelines/12/parameters", "<html>proxy</html>");

        var declared = await ReadAsync();

        Assert.Null(declared);
        Assert.Equal("RunParametersServerUnreachable", Assert.Single(Services.Toasts()).Detail);
    }

    [Fact]
    public async Task ABugException_IsNotSwallowedAsATransportFailure()
    {
        var api = new ApiClient(new HttpClient(new ThrowingHandler(new ArgumentException("bug"))) { BaseAddress = new Uri("http://test/") });

        await Assert.ThrowsAsync<ArgumentException>(() => ReadAsync(api));

        Assert.Empty(Services.Toasts());
    }

    public static TheoryData<Exception> TransportFailures => new()
    {
        new HttpRequestException("no answer"),
        new JsonException("not json"),
        new TaskCanceledException("timeout"),
        new OperationCanceledException(),
        new TimeoutRejectedException(),
        new BrokenCircuitException()
    };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public void IsTransportFailure_IsTrueForWhatAReadOfThisApiCanThrow(Exception exception) =>
        Assert.True(RunParametersReading.IsTransportFailure(exception));

    public static TheoryData<Exception> Bugs => new()
    {
        new ArgumentException("bug"),
        new InvalidOperationException("bug"),
        new NullReferenceException(),
        new NotSupportedException()
    };

    [Theory]
    [MemberData(nameof(Bugs))]
    public void IsTransportFailure_IsFalseForABug(Exception exception) =>
        Assert.False(RunParametersReading.IsTransportFailure(exception));

    [Fact]
    public void NotifyUnreachable_RaisesOneDangerToast()
    {
        RunParametersReading.NotifyUnreachable(Toast, Localizer);

        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Equal("RunParametersServerUnreachable", toast.Detail);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
