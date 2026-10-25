// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;

namespace Aetheus.Back.Components.PackageRegistry;

[ApiController]
[Route("api/packages/nuget")]
[Authorize(AuthenticationSchemes = PackageRegistryAuthenticationHandler.SchemeName)]
public class NuGetRegistryController(
    INuGetRegistryService registry,
    PackageRegistryUploadGate uploadGate) : ControllerBase
{
    [HttpGet("v3/index.json")]
    [AllowAnonymous]
    public IActionResult GetServiceIndex()
    {
        return Ok(new
        {
            version = "3.0.0",
            resources = new object[]
            {
                Resource("v3/flatcontainer/", "PackageBaseAddress/3.0.0"),
                Resource("v3/registration/", "RegistrationsBaseUrl/3.6.0"),
                Resource("v3/query", "SearchQueryService/3.5.0"),
                Resource("v2/package", "PackagePublish/2.0.0")
            }
        });
    }

    [HttpGet("v3/flatcontainer/{id}/index.json")]
    public async Task<IActionResult> GetVersions(string id, CancellationToken ct)
    {
        var versions = await registry.GetVersionsAsync(id, ct);
        return versions is null ? NotFound() : Ok(new { versions });
    }

    [HttpGet("v3/flatcontainer/{id}/{version}/{fileName}")]
    public async Task<IActionResult> Download(
        string id, string version, string fileName, CancellationToken ct)
    {
        var packageVersion = await registry.GetVersionAsync(id, version, ct);
        if (packageVersion is null)
            return NotFound();

        var normalizedId = id.ToLowerInvariant();
        var normalizedVersion = packageVersion.NormalizedVersion;
        if (string.Equals(
            fileName,
            $"{normalizedId}.{normalizedVersion}.nupkg",
            StringComparison.OrdinalIgnoreCase))
        {
            var stream = registry.OpenContent(packageVersion);
            return stream is null
                ? NotFound()
                : File(stream, PackageRegistryDefaults.NuGetContentType, enableRangeProcessing: true);
        }

        if (string.Equals(fileName, $"{normalizedId}.nuspec", StringComparison.OrdinalIgnoreCase))
            return Content(registry.ReadManifest(packageVersion), "text/xml");

        return NotFound();
    }

    [HttpGet("v3/registration/{id}/index.json")]
    public async Task<IActionResult> GetRegistration(string id, CancellationToken ct)
    {
        var registration = await registry.GetRegistrationAsync(id, ct);
        if (registration is null)
            return NotFound();

        return Ok(BuildRegistration(registration));
    }

    [HttpGet("v3/registration/{id}/{version}.json")]
    public async Task<IActionResult> GetRegistrationLeaf(
        string id, string version, CancellationToken ct)
    {
        var registration = await registry.GetRegistrationVersionAsync(id, version, ct);
        var registrationVersion = registration?.Versions.SingleOrDefault();
        return registration is null || registrationVersion is null
            ? NotFound()
            : Ok(BuildRegistrationLeaf(registration, registrationVersion));
    }

    [HttpGet("v3/query")]
    public async Task<IActionResult> Search(
        [FromQuery(Name = "q")] string? query,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        [FromQuery] bool prerelease = false,
        CancellationToken ct = default)
    {
        var result = await registry.SearchAsync(query, skip, take, prerelease, ct);
        var packages = result.Packages.Select(package => new
        {
            id = package.Id,
            version = package.Version,
            description = package.Description,
            authors = package.Authors,
            tags = package.Tags,
            registration = BuildUrl($"v3/registration/{Uri.EscapeDataString(package.Id.ToLowerInvariant())}/index.json"),
            versions = package.Versions.Select(version => new
            {
                version,
                downloads = 0,
                @id = BuildUrl(
                    $"v3/registration/{Uri.EscapeDataString(package.Id.ToLowerInvariant())}/{Uri.EscapeDataString(version.ToLowerInvariant())}.json")
            })
        });
        return Ok(new { totalHits = result.TotalHits, data = packages });
    }

    [HttpPut("v2/package")]
    [Authorize(Roles = "Admin")]
    [RequestSizeLimit(PackageRegistryDefaults.MaxRequestBodyBytes)]
    public async Task<IActionResult> Publish(CancellationToken ct)
    {
        using var uploadLease = uploadGate.TryEnter();
        if (uploadLease is null)
            return StatusCode(StatusCodes.Status429TooManyRequests);
        if (!Request.HasFormContentType)
            return BadRequest("A NuGet publish request must use multipart/form-data.");

        var form = await Request.ReadFormAsync(ct);
        var package = form.Files.FirstOrDefault();
        if (package is null || package.Length == 0)
            return BadRequest("The NuGet publish request contains no package.");

        await registry.PublishAsync(
            package.OpenReadStream,
            User.Identity?.Name ?? "unknown",
            ct);
        return StatusCode(StatusCodes.Status201Created);
    }

    [HttpDelete("v2/package/{id}/{version}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Unlist(string id, string version, CancellationToken ct)
    {
        var changed = await registry.SetListedAsync(
            id, version, false, User.Identity?.Name ?? "unknown", ct);
        return changed ? NoContent() : NotFound();
    }

    [HttpPost("v2/package/{id}/{version}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Relist(string id, string version, CancellationToken ct)
    {
        var changed = await registry.SetListedAsync(
            id, version, true, User.Identity?.Name ?? "unknown", ct);
        return changed ? NoContent() : NotFound();
    }

    private object Resource(string relativePath, string type)
        => new Dictionary<string, object>
        {
            ["@id"] = BuildUrl(relativePath),
            ["@type"] = type
        };

    private JsonObject BuildRegistration(NuGetRegistration registration)
    {
        var normalizedId = registration.Id.ToLowerInvariant();
        var registrationUrl = BuildUrl(
            $"v3/registration/{Uri.EscapeDataString(normalizedId)}/index.json");
        var items = new JsonArray();
        foreach (var version in registration.Versions)
            items.Add(BuildRegistrationLeaf(registration, version));

        var lower = registration.Versions.First().Version;
        var upper = registration.Versions.Last().Version;
        return new JsonObject
        {
            ["@id"] = registrationUrl,
            ["@type"] = new JsonArray("catalog:CatalogRoot", "PackageRegistration", "catalog:Permalink"),
            ["count"] = 1,
            ["items"] = new JsonArray
            {
                new JsonObject
                {
                    ["@id"] = registrationUrl,
                    ["@type"] = "catalog:CatalogPage",
                    ["count"] = registration.Versions.Count,
                    ["lower"] = lower,
                    ["upper"] = upper,
                    ["items"] = items
                }
            }
        };
    }

    private JsonObject BuildRegistrationLeaf(
        NuGetRegistration registration,
        NuGetRegistrationVersion version)
    {
        var normalizedId = registration.Id.ToLowerInvariant();
        var normalizedVersion = version.Version.ToLowerInvariant();
        var registrationUrl = BuildUrl(
            $"v3/registration/{Uri.EscapeDataString(normalizedId)}/index.json");
        var packageContent = BuildUrl(
            $"v3/flatcontainer/{Uri.EscapeDataString(normalizedId)}/{Uri.EscapeDataString(normalizedVersion)}/{Uri.EscapeDataString(normalizedId + "." + normalizedVersion + ".nupkg")}");
        var dependencyGroups = new JsonArray();
        foreach (var group in version.DependencyGroups)
        {
            var dependencies = new JsonArray();
            foreach (var dependency in group.Dependencies)
            {
                dependencies.Add(new JsonObject
                {
                    ["@id"] = BuildUrl(
                        $"v3/registration/{Uri.EscapeDataString(dependency.Id.ToLowerInvariant())}/index.json"),
                    ["id"] = dependency.Id,
                    ["range"] = dependency.Range,
                    ["registration"] = BuildUrl(
                        $"v3/registration/{Uri.EscapeDataString(dependency.Id.ToLowerInvariant())}/index.json")
                });
            }

            dependencyGroups.Add(new JsonObject
            {
                ["targetFramework"] = group.TargetFramework,
                ["dependencies"] = dependencies
            });
        }

        var leafUrl = BuildUrl(
            $"v3/registration/{Uri.EscapeDataString(normalizedId)}/{Uri.EscapeDataString(normalizedVersion)}.json");
        var catalogEntry = new JsonObject
        {
            ["@id"] = leafUrl,
            ["@type"] = "PackageDetails",
            ["authors"] = registration.Authors,
            ["dependencyGroups"] = dependencyGroups,
            ["description"] = registration.Description,
            ["id"] = registration.Id,
            ["listed"] = version.IsListed,
            ["packageContent"] = packageContent,
            ["published"] = version.PublishedAt,
            ["tags"] = registration.Tags,
            ["version"] = version.Version
        };
        if (version.IsDeprecated)
        {
            // NuGet registration "deprecation": a reason from the protocol's list and a message.
            catalogEntry["deprecation"] = new JsonObject
            {
                ["@id"] = $"{leafUrl}#deprecation",
                ["reasons"] = new JsonArray("Other"),
                ["message"] = PackageRegistryDefaults.DeprecationMessage
            };
        }

        return new JsonObject
        {
            ["@id"] = leafUrl,
            ["@type"] = "Package",
            ["packageContent"] = packageContent,
            ["registration"] = registrationUrl,
            ["catalogEntry"] = catalogEntry
        };
    }

    private string BuildUrl(string relativePath)
        => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/packages/nuget/{relativePath.TrimStart('/')}";
}
