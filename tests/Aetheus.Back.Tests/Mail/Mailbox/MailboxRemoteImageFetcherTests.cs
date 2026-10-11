// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Mail.Mailbox;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recette R1-17: "show images" fetches only raster images, bounded, through the SSRF guard.</summary>
public sealed class MailboxRemoteImageFetcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Image(string type, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static MailboxRemoteImageFetcher Fetcher(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MailboxRemoteImageFetcher.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new MailboxRemoteImageFetcher(factory);
    }

    [Fact]
    public async Task A_raster_image_comes_back_as_a_data_url()
    {
        var fetcher = Fetcher(new StubHandler(_ => Image("image/png", [1, 2, 3])));

        var result = await fetcher.FetchAsync(["https://cdn.example/a.png"], Ct);

        Assert.Equal("data:image/png;base64,AQID", result["https://cdn.example/a.png"]);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    public async Task Anything_but_a_raster_image_is_dropped(string type)
    {
        var fetcher = Fetcher(new StubHandler(_ => Image(type, [1])));

        Assert.Empty(await fetcher.FetchAsync(["https://cdn.example/a"], Ct));
    }

    [Fact]
    public async Task An_image_over_the_size_limit_is_dropped()
    {
        var fetcher = Fetcher(new StubHandler(_ => Image("image/png", new byte[MailboxRemoteImageFetcher.MaxImageBytes + 1])));

        Assert.Empty(await fetcher.FetchAsync(["https://cdn.example/big.png"], Ct));
    }

    [Fact]
    public async Task A_redirect_is_followed_by_hand_a_bounded_number_of_times()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/final.png"
            ? Image("image/gif", [7])
            : new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/final.png", UriKind.Relative) } });
        var loop = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.example/again") } });

        var followed = await Fetcher(handler).FetchAsync(["https://cdn.example/start"], Ct);
        var looping = await Fetcher(loop).FetchAsync(["https://cdn.example/start"], Ct);

        Assert.Equal("data:image/gif;base64,Bw==", followed["https://cdn.example/start"]);
        Assert.Empty(looping);
        Assert.Equal(4, loop.Requested.Count);
    }

    [Fact]
    public async Task A_redirect_to_another_scheme_is_not_followed()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("file:///etc/passwd") } });

        Assert.Empty(await Fetcher(handler).FetchAsync(["https://cdn.example/a.png"], Ct));
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task The_number_of_images_is_bounded()
    {
        var handler = new StubHandler(_ => Image("image/png", [1]));
        var urls = Enumerable.Range(0, MailboxRemoteImageFetcher.MaxImages + 5).Select(index => $"https://cdn.example/{index}.png").ToList();

        var result = await Fetcher(handler).FetchAsync(urls, Ct);

        Assert.Equal(MailboxRemoteImageFetcher.MaxImages, result.Count);
        Assert.Equal(MailboxRemoteImageFetcher.MaxImages, handler.Requested.Count);
    }

    [Fact]
    public async Task The_registered_client_refuses_a_loopback_target_that_does_answer()
    {
        using var server = new HttpListener();
        var port = FreePort();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        var serving = ServeOneImageAsync(server);
        var url = $"http://localhost:{port}/a.png";

        // The module's own registration: the shared SSRF-protected handler, private targets refused.
        var services = new ServiceCollection();
        services.AddMailModule();
        await using var provider = services.BuildServiceProvider();
        var guarded = new MailboxRemoteImageFetcher(provider.GetRequiredService<IHttpClientFactory>());
        var blocked = await guarded.FetchAsync([url], Ct);

        // The same target through a plain client answers: the refusal above is the guard, not a dead port.
        using var plain = new HttpClientHandler();
        var reached = await Fetcher(plain).FetchAsync([url], Ct);

        Assert.Empty(blocked);
        Assert.Equal("data:image/png;base64,CQ==", reached[url]);
        server.Stop();
        await serving;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task ServeOneImageAsync(HttpListener server)
    {
        try
        {
            while (server.IsListening)
            {
                var context = await server.GetContextAsync();
                context.Response.ContentType = "image/png";
                await context.Response.OutputStream.WriteAsync(new byte[] { 9 });
                context.Response.Close();
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The listener was stopped at the end of the test.
        }
    }
}
