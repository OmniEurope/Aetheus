// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-24: the registry side of a digest-pinned image bump, against a scripted HTTP handler. No real registry is called here:
/// what is proved is the request the resolver makes (a HEAD on the manifest, an anonymous Docker Hub token, no other host) and every
/// refusal. Whether MCR or Docker Hub still answer this way is not proved by these tests.
/// </summary>
public sealed class ImageDigestResolverTests
{
    private const string Digest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Url, string? Authorization, string Accept)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), string.Join(",", request.Headers.Accept)));
            return Task.FromResult(answer(request));
        }
    }

    private static RegistryImageDigestResolver Resolver(ScriptedHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(RegistryImageDigestResolver.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new RegistryImageDigestResolver(factory, NullLogger<RegistryImageDigestResolver>.Instance);
    }

    private static HttpResponseMessage WithDigest(string digest = Digest)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
        return response;
    }

    private static HttpResponseMessage Challenge(string realm)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", $"Bearer realm=\"{realm}\",service=\"registry.docker.io\"");
        return response;
    }

    [Fact]
    public async Task Mcr_AnswersWithItsDigest_FromAHeadOnTheManifestListAndNothingElse()
    {
        var handler = new ScriptedHandler(_ => WithDigest());

        var answer = await Resolver(handler).ResolveAsync("mcr.microsoft.com/dotnet/aspnet", "10.0.15", TestContext.Current.CancellationToken);

        Assert.Equal(Digest, answer.Digest);
        Assert.Null(answer.Failure);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Equal("https://mcr.microsoft.com/v2/dotnet/aspnet/manifests/10.0.15", request.Url.AbsoluteUri);
        Assert.Contains("application/vnd.oci.image.index.v1+json", request.Accept, StringComparison.Ordinal);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task DockerHub_AskedAnAnonymousPullTokenFromItsOwnAuthService_ThenReadsTheManifest()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.Host switch
        {
            "auth.docker.io" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t0k\"}", Encoding.UTF8, "application/json") },
            _ => request.Headers.Authorization is null ? Challenge("https://auth.docker.io/token") : WithDigest()
        });

        var answer = await Resolver(handler).ResolveAsync("node", "20.11.1-alpine3.19", TestContext.Current.CancellationToken);

        Assert.Equal(Digest, answer.Digest);
        Assert.Equal(
            ["https://registry-1.docker.io/v2/library/node/manifests/20.11.1-alpine3.19",
             "https://auth.docker.io/token?service=registry.docker.io&scope=repository%3Alibrary%2Fnode%3Apull",
             "https://registry-1.docker.io/v2/library/node/manifests/20.11.1-alpine3.19"],
            handler.Requests.Select(request => request.Url.AbsoluteUri));
        Assert.Equal("Bearer t0k", handler.Requests[2].Authorization);
    }

    [Fact]
    public async Task ATokenServiceOnAnotherHost_IsNeverCalled_SoTheRegistryCannotSendUsElsewhere()
    {
        var handler = new ScriptedHandler(_ => Challenge("https://evil.example/token"));

        var answer = await Resolver(handler).ResolveAsync("docker.io/library/node", "20.11.1", TestContext.Current.CancellationToken);

        Assert.Null(answer.Digest);
        Assert.Contains("no anonymous pull token", answer.Failure, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("ghcr.io/team/app")]
    [InlineData("registry.local:5000/team/app")]
    [InlineData("169.254.169.254/team/app")]
    public async Task ARegistryThatIsNotMcrNorDockerHub_IsNeverContacted(string image)
    {
        var handler = new ScriptedHandler(_ => WithDigest());

        var answer = await Resolver(handler).ResolveAsync(image, "1.0.0", TestContext.Current.CancellationToken);

        Assert.Null(answer.Digest);
        Assert.Contains("not an image name on a registry the digest is read from", answer.Failure, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("mcr.microsoft.com/dotnet/aspnet", "10.0.15/../../x")]
    [InlineData("mcr.microsoft.com/dotnet/aspnet", "")]
    [InlineData("mcr.microsoft.com/dotnet/../aspnet", "10.0.15")]
    [InlineData("mcr.microsoft.com/Dotnet?x=1", "10.0.15")]
    public async Task AReferenceThatIsNotAnImageReference_IsRefusedBeforeAnyRequest(string image, string tag)
    {
        var handler = new ScriptedHandler(_ => WithDigest());

        var answer = await Resolver(handler).ResolveAsync(image, tag, TestContext.Current.CancellationToken);

        Assert.Null(answer.Digest);
        Assert.False(string.IsNullOrWhiteSpace(answer.Failure));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ATagTheRegistryDoesNotKnow_IsAFailureWithItsReason()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var answer = await Resolver(handler).ResolveAsync("mcr.microsoft.com/dotnet/aspnet", "10.0.99", TestContext.Current.CancellationToken);

        Assert.Null(answer.Digest);
        Assert.Contains("has no tag 10.0.99", answer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegistryThatAnswersAnError_IsAFailureWithTheStatus()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var answer = await Resolver(handler).ResolveAsync("mcr.microsoft.com/dotnet/aspnet", "10.0.15", TestContext.Current.CancellationToken);

        Assert.Null(answer.Digest);
        Assert.Contains("answered 429", answer.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sha256:short")]
    [InlineData("md5:2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData("")]
    public async Task AnAnswerWithoutAUsableSha256Digest_IsAFailure_NotAGuess(string digest)
    {
        var handler = new ScriptedHandler(_ => digest.Length == 0 ? new HttpResponseMessage(HttpStatusCode.OK) : WithDigest(digest));

        var answer = await Resolver(handler).ResolveAsync("mcr.microsoft.com/dotnet/aspnet", "10.0.15", TestContext.Current.CancellationToken);

        Assert.Null(answer.Digest);
        Assert.Contains("no sha256 digest", answer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoNetwork_IsAFailureWithTheReason_NotAnException()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("no route to host"));

        var answer = await Resolver(handler).ResolveAsync("mcr.microsoft.com/dotnet/aspnet", "10.0.15", TestContext.Current.CancellationToken);

        Assert.Null(answer.Digest);
        Assert.Contains("did not answer", answer.Failure, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", answer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACanceledCall_StaysCanceled_ItIsNotTurnedIntoAFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Resolver(handler).ResolveAsync("mcr.microsoft.com/dotnet/aspnet", "10.0.15", cancellation.Token));
    }
}
