// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-58, through the real HTTP pipeline and PostgreSQL: a client project's deploy image build runs on
/// an agent without a token and must restore the observability packages. An administrator opens a
/// package to anonymous download; from then on a call without credentials reads that package (NuGet
/// version list, archive, manifest, registration; npm packument and tarball), while every other
/// package, the search endpoints and every publication keep answering 401. Closing the option
/// restores the 401.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PackageRegistryAnonymousDownloadIntegrationTests(PostgresFixture fixture) : IDisposable
{
    private const string NuGet = "/api/packages/nuget/";
    private const string Npm = "/api/packages/npm/";
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "aetheus-anon-registry-it", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenedPackages_ReadWithoutToken_WhileOtherPackagesSearchAndPublishStayAuthenticated()
    {
        await fixture.ResetAsync();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("PackageRegistry:BasePath", _storage));
        using var admin = host.CreateClient();
        IntegrationAuth.SetBearer(admin, await IntegrationAuth.LoginAsAdminAsync(admin, Ct));
        var token = await CreatePatAsync(admin);

        using var publisher = host.CreateClient();
        publisher.DefaultRequestHeaders.Add("X-NuGet-ApiKey", token);
        await PushNuGetAsync(publisher, "Aetheus.Telemetry", "1.2.1");
        await PushNuGetAsync(publisher, "Aetheus.WebAnalytics", "1.1.1");
        await PushNuGetAsync(publisher, "Aetheus.Private", "1.0.0");
        await PublishNpmAsync(publisher, token, "@aetheus/web-analytics", "1.1.1");
        await PublishNpmAsync(publisher, token, "@aetheus/private", "1.0.0");

        using var anonymous = host.CreateClient();

        // R-58 (d1272f9ad, PackageRegistryDefaults.IsPublicByDefault): the observability packages are open
        // from their first publication; any other freshly published package is private.
        await AssertStatusAsync(anonymous, NuGet + "v3/flatcontainer/aetheus.telemetry/index.json", HttpStatusCode.OK);
        await AssertStatusAsync(anonymous, Npm + "@aetheus%2fweb-analytics", HttpStatusCode.OK);
        await AssertStatusAsync(anonymous, NuGet + "v3/flatcontainer/aetheus.private/index.json", HttpStatusCode.Unauthorized);

        var packages = await ListPackagesAsync(admin);
        foreach (var name in new[] { "Aetheus.Telemetry", "Aetheus.WebAnalytics", "@aetheus/web-analytics" })
            await SetAnonymousDownloadAsync(admin, packages[name], true);
        Assert.Equal(["@aetheus/web-analytics", "Aetheus.Telemetry", "Aetheus.WebAnalytics"],
            (await ListAnonymousAsync(admin)).Order(StringComparer.Ordinal));

        // The option is administrator-only.
        var anonymousToggle = await anonymous.PutAsJsonAsync(
            $"/api/package-registry/{packages["Aetheus.Private"]}/anonymous-download",
            new { allowAnonymousDownload = true }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousToggle.StatusCode);

        // What dotnet restore reads, for both NuGet packages.
        foreach (var (id, version) in new[] { ("aetheus.telemetry", "1.2.1"), ("aetheus.webanalytics", "1.1.1") })
        {
            var versions = await GetJsonAsync(anonymous, NuGet + $"v3/flatcontainer/{id}/index.json");
            Assert.Equal(version, versions["versions"]![0]!.GetValue<string>());
            await AssertStatusAsync(anonymous, NuGet + $"v3/flatcontainer/{id}/{version}/{id}.{version}.nupkg", HttpStatusCode.OK);
            await AssertStatusAsync(anonymous, NuGet + $"v3/flatcontainer/{id}/{version}/{id}.nuspec", HttpStatusCode.OK);
            await AssertStatusAsync(anonymous, NuGet + $"v3/registration/{id}/index.json", HttpStatusCode.OK);
            await AssertStatusAsync(anonymous, NuGet + $"v3/registration/{id}/{version}.json", HttpStatusCode.OK);
        }

        // npm: packument, version document and tarball of the opened package.
        var packument = await GetJsonAsync(anonymous, Npm + "@aetheus%2fweb-analytics");
        var tarball = packument["versions"]!["1.1.1"]!["dist"]!["tarball"]!.GetValue<string>();
        await AssertStatusAsync(anonymous, Npm + "@aetheus/web-analytics/1.1.1", HttpStatusCode.OK);
        await AssertStatusAsync(anonymous, new Uri(tarball).PathAndQuery, HttpStatusCode.OK);

        // Everything else keeps its credentials: another package (existing or not), search, ping, publish.
        await AssertStatusAsync(anonymous, NuGet + "v3/flatcontainer/aetheus.private/index.json", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, NuGet + "v3/flatcontainer/aetheus.private/1.0.0/aetheus.private.1.0.0.nupkg", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, NuGet + "v3/registration/aetheus.private/index.json", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, NuGet + "v3/flatcontainer/does.not.exist/index.json", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, NuGet + "v3/query?q=aetheus", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, Npm + "@aetheus%2fprivate", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, Npm + "-/v1/search?text=aetheus", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, Npm + "-/ping", HttpStatusCode.Unauthorized);
        var anonymousPush = await anonymous.PutAsync(NuGet + "v2/package", NuGetPushContent("Aetheus.Telemetry", "9.9.9"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPush.StatusCode);
        var anonymousNpmPublish = await anonymous.PutAsync(Npm + "@aetheus%2fweb-analytics",
            new StringContent(NpmPublishDocument("@aetheus/web-analytics", "9.9.9"), Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousNpmPublish.StatusCode);
        var anonymousUnlist = await anonymous.DeleteAsync(NuGet + "v2/package/aetheus.telemetry/1.2.1", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousUnlist.StatusCode);

        // Closing the option closes the door again.
        await SetAnonymousDownloadAsync(admin, packages["Aetheus.Telemetry"], false);
        await AssertStatusAsync(anonymous, NuGet + "v3/flatcontainer/aetheus.telemetry/index.json", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, NuGet + "v3/flatcontainer/aetheus.webanalytics/index.json", HttpStatusCode.OK);
    }

    private static async Task<string> CreatePatAsync(HttpClient jwt)
    {
        var response = await jwt.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = "anon-registry-it", Scope = PatScope.ReadWrite, ExpirationDays = 1 }, Ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(IntegrationJsonOptions.Default, Ct);
        return created!.PlaintextToken;
    }

    private static async Task<Dictionary<string, int>> ListPackagesAsync(HttpClient admin)
    {
        var page = await GetJsonAsync(admin, "/api/package-registry?pageSize=50");
        return page["items"]!.AsArray().ToDictionary(
            item => item!["name"]!.GetValue<string>(),
            item => item!["id"]!.GetValue<int>());
    }

    private static async Task<List<string>> ListAnonymousAsync(HttpClient admin)
    {
        var page = await GetJsonAsync(admin, "/api/package-registry?pageSize=50");
        return page["items"]!.AsArray()
            .Where(item => item!["allowAnonymousDownload"]!.GetValue<bool>())
            .Select(item => item!["name"]!.GetValue<string>())
            .ToList();
    }

    private static async Task SetAnonymousDownloadAsync(HttpClient admin, int packageId, bool allowed)
    {
        var response = await admin.PutAsJsonAsync(
            $"/api/package-registry/{packageId}/anonymous-download",
            new { allowAnonymousDownload = allowed }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"Toggle anonymous download of package {packageId}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {path}: expected OK, got {response.StatusCode}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    private static async Task AssertStatusAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.True(expected == response.StatusCode,
            $"GET {path}: expected {expected}, got {response.StatusCode}");
        if (expected == HttpStatusCode.Unauthorized)
            Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    private static async Task PushNuGetAsync(HttpClient publisher, string id, string version)
    {
        var response = await publisher.PutAsync(NuGet + "v2/package", NuGetPushContent(id, version), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Push {id} {version}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task PublishNpmAsync(HttpClient publisher, string token, string name, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Npm + Uri.EscapeDataString(name))
        {
            Content = new StringContent(NpmPublishDocument(name, version), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await publisher.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Publish {name} {version}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static MultipartFormDataContent NuGetPushContent(string id, string version)
    {
        var nuspec = $"""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>Aetheus</authors>
                <description>R-58 integration test package.</description>
              </metadata>
            </package>
            """;
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry($"{id}.nuspec");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(nuspec);
        }

        var file = new ByteArrayContent(archive.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, "package", "package.nupkg" } };
    }

    private static string NpmPublishDocument(string name, string version)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/package.json")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name, version })))
            });
        }

        return new JsonObject
        {
            ["name"] = name,
            ["versions"] = new JsonObject { [version] = new JsonObject { ["name"] = name, ["version"] = version } },
            ["dist-tags"] = new JsonObject { ["latest"] = version },
            ["_attachments"] = new JsonObject
            {
                ["package.tgz"] = new JsonObject { ["data"] = Convert.ToBase64String(output.ToArray()) }
            }
        }.ToJsonString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_storage))
            Directory.Delete(_storage, recursive: true);
    }
}
