// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Front.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests;

public class ErrorNotificationHandlerTests
{
    [Fact]
    public async Task SendAsync_SuccessResponse_NoNotification()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.OK);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(notif.Toasts());
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_Unauthorized_SilentNoNotification()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.Unauthorized);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(notif.Toasts());
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_BadRequest_NotifiesError()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.BadRequest);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Danger, notif.Toasts()[0].Severity);
        Assert.Equal("Error 400", notif.Toasts()[0].Summary);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_BadRequestTheCallerHandles_LeavesTheToastToTheCaller()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.BadRequest, new ApiError { Message = "refused" });
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/pipelines/5/run");
        request.Options.Set(ErrorNotificationHandler.CallerHandlesBadRequest, true);

        await client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(notif.Toasts());
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_ServerErrorOnARequestWhoseCallerHandlesBadRequest_StillNotifies()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.InternalServerError);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/pipelines/5/run");
        request.Options.Set(ErrorNotificationHandler.CallerHandlesBadRequest, true);

        await client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_InternalServerError_NotifiesError()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.InternalServerError);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Danger, notif.Toasts()[0].Severity);
        Assert.Equal("Error 500", notif.Toasts()[0].Summary);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_NotFound_NotifiesResourceNotFound()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.NotFound);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal("ResourceNotFound", notif.Toasts()[0].Detail);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_Forbidden_NotifiesAccessDenied()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.Forbidden);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal("AccessDenied", notif.Toasts()[0].Detail);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_Conflict_NotifiesConflicting()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.Conflict);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal("ConflictingOperation", notif.Toasts()[0].Detail);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_JsonErrorBody_ExtractsMessage()
    {
        var error = new ApiError { Message = "Custom error message" };
        var (notif, handler, client) = CreateSetup(HttpStatusCode.BadRequest, error);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal("Custom error message", notif.Toasts()[0].Detail);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_JsonStringBody_ExtractsMessage()
    {
        var (notif, handler, client) = CreateSetup(
            HttpStatusCode.BadRequest,
            "Pipeline definition is invalid.");

        await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"),
            Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("Pipeline definition is invalid.", Assert.Single(notif.Toasts()).Detail);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_UnknownStatusCode_ShowsGenericMessage()
    {
        var (notif, handler, client) = CreateSetup(HttpStatusCode.Gone);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal("RequestFailed", notif.Toasts()[0].Detail);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_OneShotErrorStream_DoesNotThrow_AndExtractsMessage()
    {
        // Regression: on Blazor WASM the error body is a one-shot BrowserHttpReadStream. The handler
        // reads it to surface the API message, then HttpClient re-buffers the same content
        // (ResponseContentRead) - a second read of the already-consumed stream threw
        // ObjectDisposedException, which bubbled to the ErrorBoundary instead of the caller's normal
        // non-2xx handling. The handler must buffer first so the stream is read exactly once.
        var notif = new OmniOverlayService(new FakeTimeProvider());
        var json = JsonSerializer.SerializeToUtf8Bytes(new ApiError { Message = "boom" });
        var innerHandler = new OneShotStubHandler(HttpStatusCode.BadRequest, json);
        var localizerMock = Substitute.For<IStringLocalizer<AppStrings>>();
        localizerMock[Arg.Any<string>()].Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        var toast = new NotifyHelper(notif, localizerMock);
        var handler = new ErrorNotificationHandler(toast, localizerMock) { InnerHandler = innerHandler };
        var client = new HttpClient(handler);

        // Default ResponseContentRead makes HttpClient buffer the content after the pipeline; without
        // the buffer-first fix that second read of the consumed one-shot stream throws here.
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        Assert.Single(notif.Toasts());
        Assert.Equal("boom", notif.Toasts()[0].Detail);
        client.Dispose();
        handler.Dispose();
    }

    private static async Task<string?> DetailOfAsync(HttpStatusCode status, string? body, string? mediaType)
    {
        var notif = new OmniOverlayService(new FakeTimeProvider());
        var localizerMock = Substitute.For<IStringLocalizer<AppStrings>>();
        localizerMock[Arg.Any<string>()].Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        var toast = new NotifyHelper(notif, localizerMock);
        using var handler = new ErrorNotificationHandler(toast, localizerMock) { InnerHandler = new RawBodyHandler(status, body, mediaType) };
        using var client = new HttpClient(handler);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"), Xunit.TestContext.Current.CancellationToken);

        return Assert.Single(notif.Toasts()).Detail;
    }

    [Theory]
    [InlineData("""{"detail":"The pipeline is locked.","title":"Conflict"}""", "The pipeline is locked.")]
    [InlineData("""{"title":"One or more validation errors occurred."}""", "One or more validation errors occurred.")]
    [InlineData("""{"detail":"  ","title":"Fallback title"}""", "Fallback title")]
    public async Task SendAsync_AProblemDetailsBody_SurfacesItsDetailThenItsTitle(string body, string expected) =>
        Assert.Equal(expected, await DetailOfAsync(HttpStatusCode.BadRequest, body, "application/problem+json"));

    [Theory]
    [InlineData("""{"detail":null,"title":null}""")]
    [InlineData("""{"other":"value"}""")]
    [InlineData("""{"message":""}""")]
    public async Task SendAsync_AJsonBodyWithNoUsableMessage_FallsBackToTheStatusMessage(string body) =>
        Assert.Equal("InvalidRequest", await DetailOfAsync(HttpStatusCode.BadRequest, body, "application/json"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_AnEmptyJsonBody_FallsBackToTheStatusMessage(string? body) =>
        Assert.Equal("UnexpectedServerError", await DetailOfAsync(HttpStatusCode.InternalServerError, body, "application/json"));

    // PLAN-003 P3-2: a sign-in e-mail another account holds is said in the user's language, not in the
    // API's English message.
    [Fact]
    public async Task SendAsync_AnEmailHeldByAnotherAccount_IsSaidInTheUsersLanguage() =>
        Assert.Equal("UserEmailInUse", await DetailOfAsync(HttpStatusCode.Conflict,
            """{"message":"This e-mail address is already used by another account.","code":"UserEmailInUse"}""",
            "application/json"));

    [Fact]
    public async Task SendAsync_ABodyThatIsNotValidJson_FallsBackToTheStatusMessage() =>
        Assert.Equal("AccessDenied", await DetailOfAsync(HttpStatusCode.Forbidden, "<html>proxy error</html>", "application/json"));

    [Fact]
    public async Task SendAsync_ANonJsonBody_IsNeverParsedAndFallsBackToTheStatusMessage() =>
        Assert.Equal("ConflictingOperation", await DetailOfAsync(HttpStatusCode.Conflict, """{"message":"ignored"}""", "text/plain"));

    private sealed class RawBodyHandler(HttpStatusCode statusCode, string? body, string? mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode);
            if (body is not null)
            {
                response.Content = new StringContent(body);
                response.Content.Headers.ContentType = new(mediaType!);
            }
            return Task.FromResult(response);
        }
    }

    private static (OmniOverlayService notif, ErrorNotificationHandler handler, HttpClient client) CreateSetup(
        HttpStatusCode statusCode, object? jsonBody = null)
    {
        var notif = new OmniOverlayService(new FakeTimeProvider());

        var innerHandler = new StubHandler(statusCode, jsonBody);
        var localizerMock = Substitute.For<IStringLocalizer<AppStrings>>();
        localizerMock[Arg.Any<string>()].Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        var toast = new NotifyHelper(notif, localizerMock);
        var handler = new ErrorNotificationHandler(toast, localizerMock)
        {
            InnerHandler = innerHandler
        };
        var client = new HttpClient(handler);
        return (notif, handler, client);
    }

    private sealed class StubHandler(HttpStatusCode statusCode, object? jsonBody = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode);
            if (jsonBody is not null)
                response.Content = JsonContent.Create(jsonBody);
            return Task.FromResult(response);
        }
    }

    private sealed class OneShotStubHandler(HttpStatusCode statusCode, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StreamContent(new OneShotStream(body))
            };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }
    }

    // Mimics BrowserHttpReadStream: readable exactly once. After the stream is drained to EOF, any
    // further read throws ObjectDisposedException - exactly what a second buffering pass would trigger.
    private sealed class OneShotStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        private bool _consumed;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_consumed) throw new ObjectDisposedException(nameof(OneShotStream));
            var n = _inner.Read(buffer, offset, count);
            if (n == 0) _consumed = true;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
