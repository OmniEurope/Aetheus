// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The Maven repository through the real HTTP pipeline and PostgreSQL, the way <c>mvn deploy</c> and
/// a dependency resolution talk to it: an Aetheus personal access token as the Basic password, PUT of
/// the artifact, its POM and their checksums, then GET of the generated <c>maven-metadata.xml</c> and
/// its checksum. The authentication boundaries are the registry's: no token is challenged, a read-only
/// token reads but cannot deploy, a path that leaves the layout is refused.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MavenRegistryHttpIntegrationTests(PostgresFixture fixture) : IDisposable
{
    private const string Root = "/api/packages/maven/";
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "aetheus-maven-it", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Pom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <modelVersion>4.0.0</modelVersion>
          <groupId>org.example.it</groupId><artifactId>hello</artifactId><version>1.0.0</version>
          <description>Integration test artifact</description>
        </project>
        """;

    [Fact]
    public async Task DeployThenResolve_OverHttp_WithThePersonalAccessTokenAsBasicPassword()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("PackageRegistry:BasePath", _storage));
        using var jwt = host.CreateClient();
        IntegrationAuth.SetBearer(jwt, await IntegrationAuth.LoginAsAdminAsync(jwt, Ct));
        var readWrite = await CreatePatAsync(jwt, PatScope.ReadWrite);
        var readOnly = await CreatePatAsync(jwt, PatScope.ReadOnly);

        using var anonymous = host.CreateClient();
        var challenged = await anonymous.GetAsync(Root + "org/example/it/hello/maven-metadata.xml", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        Assert.Contains("Basic", challenged.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        using var deployer = BasicClient(host, readWrite);
        var jar = Encoding.UTF8.GetBytes("not really a jar, but bytes are bytes");
        await PutAsync(deployer, "org/example/it/hello/1.0.0/hello-1.0.0.jar", jar, HttpStatusCode.Created);
        await PutAsync(deployer, "org/example/it/hello/1.0.0/hello-1.0.0.jar.sha1", Ascii(Sha1Hex(jar)), HttpStatusCode.Created);
        await PutAsync(deployer, "org/example/it/hello/1.0.0/hello-1.0.0.pom", Encoding.UTF8.GetBytes(Pom), HttpStatusCode.Created);
        await PutAsync(deployer, "org/example/it/hello/maven-metadata.xml",
            Encoding.UTF8.GetBytes("<metadata><versioning><release>9.9.9</release></versioning></metadata>"), HttpStatusCode.Created);

        // A wrong checksum, a POM for other coordinates and a tampered redeploy are refused.
        await PutAsync(deployer, "org/example/it/hello/1.0.0/hello-1.0.0.jar.sha1", Ascii(new string('0', 40)), HttpStatusCode.BadRequest);
        await PutAsync(deployer, "org/example/it/hello/1.0.0/hello-1.0.0.jar", Ascii("tampered"), HttpStatusCode.Conflict);
        await PutAsync(deployer, "org/example/it/hello/2.0.0/hello-2.0.0.pom", Encoding.UTF8.GetBytes(Pom), HttpStatusCode.BadRequest);

        using var reader = BasicClient(host, readOnly);
        Assert.Equal(jar, await reader.GetByteArrayAsync(Root + "org/example/it/hello/1.0.0/hello-1.0.0.jar", Ct));
        var head = await reader.SendAsync(new HttpRequestMessage(HttpMethod.Head, Root + "org/example/it/hello/1.0.0/hello-1.0.0.jar"), Ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(jar.Length, head.Content.Headers.ContentLength);

        var metadata = await reader.GetStringAsync(Root + "org/example/it/hello/maven-metadata.xml", Ct);
        var versioning = XDocument.Parse(metadata).Root!.Element("versioning")!;
        Assert.Equal("1.0.0", versioning.Element("release")!.Value);
        Assert.Equal(["1.0.0"], versioning.Descendants("version").Select(element => element.Value));
        Assert.Equal(Sha1Hex(Encoding.UTF8.GetBytes(metadata)),
            await reader.GetStringAsync(Root + "org/example/it/hello/maven-metadata.xml.sha1", Ct));
        Assert.Equal(Sha1Hex(jar), await reader.GetStringAsync(Root + "org/example/it/hello/1.0.0/hello-1.0.0.jar.sha1", Ct));

        var readOnlyDeploy = await reader.PutAsync(Root + "org/example/it/hello/1.0.1/hello-1.0.1.jar",
            new ByteArrayContent(jar), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, readOnlyDeploy.StatusCode);

        foreach (var hostile in new[] { "org/example/it/hello/1.0.0/..%2F..%2F..%2Fappsettings.json", "org/example/it/hello/1.0.0/%2E%2E%5Chello-1.0.0.jar" })
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(Root + hostile, Ct)).StatusCode);
        var hostilePut = await deployer.PutAsync(Root + "org/example/it/hello/1.0.0/%2E%2E", new ByteArrayContent(jar), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, hostilePut.StatusCode);

        var listed = await jwt.GetFromJsonAsync<PaginatedResult<PackageRegistryPackageDto>>(
            "/api/package-registry?kind=Maven", IntegrationJsonOptions.Default, Ct);
        var package = Assert.Single(listed!.Items);
        Assert.Equal(PackageRegistryKind.Maven, package.Kind);
        Assert.Equal("org.example.it:hello", package.Name);
        Assert.Equal("1.0.0", package.LatestVersion);
        Assert.Equal("Integration test artifact", package.Description);
    }

    private static async Task<string> CreatePatAsync(HttpClient jwt, PatScope scope)
    {
        var response = await jwt.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = $"maven-it-{scope}", Scope = scope, ExpirationDays = 1 }, Ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(IntegrationJsonOptions.Default, Ct);
        return created!.PlaintextToken;
    }

    private static HttpClient BasicClient(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + token)));
        return client;
    }

    private static async Task PutAsync(HttpClient client, string path, byte[] body, HttpStatusCode expected)
    {
        var response = await client.PutAsync(Root + path, new ByteArrayContent(body), Ct);
        Assert.True(expected == response.StatusCode,
            $"PUT {path}: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static string Sha1Hex(byte[] content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (Directory.Exists(_storage))
            Directory.Delete(_storage, recursive: true);
    }
}
