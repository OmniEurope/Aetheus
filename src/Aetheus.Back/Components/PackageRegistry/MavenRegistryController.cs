// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PackageRegistry;

/// <summary>
/// Maven 2 repository endpoint for <c>mvn deploy</c> (HTTP PUT) and dependency resolution
/// (GET/HEAD). Same authentication as the NuGet and npm registries: an Aetheus personal access token
/// as the Basic password (Maven <c>settings.xml</c> server credentials) or as a Bearer token. Reading
/// needs any valid token; deploying needs the Admin role, and a read-only token is refused by the
/// scope middleware before the action runs.
/// </summary>
[ApiController]
[Route("api/packages/maven")]
[Authorize(AuthenticationSchemes = PackageRegistryAuthenticationHandler.SchemeName)]
public class MavenRegistryController(
    IMavenRegistryService registry,
    PackageRegistryUploadGate uploadGate) : ControllerBase
{
    private static readonly TimeSpan UploadWait = TimeSpan.FromMinutes(2);

    [HttpGet("{**path}")]
    [HttpHead("{**path}")]
    public async Task<IActionResult> Get(string path, CancellationToken ct)
    {
        if (!MavenRepositoryPath.TryParse(path, out var parsed, out _))
            return Missing();

        var content = await registry.GetAsync(parsed!, ct);
        if (content is null)
            return Missing();
        if (content.Bytes is not null)
            return File(content.Bytes, content.ContentType);

        var stream = registry.OpenStored(content.StoragePath!);
        return stream is null
            ? Missing()
            : File(stream, content.ContentType, enableRangeProcessing: true);
    }

    [HttpPut("{**path}")]
    [Authorize(Roles = "Admin")]
    [RequestSizeLimit(PackageRegistryDefaults.MaxRequestBodyBytes)]
    public async Task<IActionResult> Put(string path, CancellationToken ct)
    {
        if (!MavenRepositoryPath.TryParse(path, out var parsed, out var error))
            return BadRequest(error);

        using var uploadLease = await uploadGate.EnterAsync(UploadWait, ct);
        if (uploadLease is null)
            return StatusCode(StatusCodes.Status429TooManyRequests);

        await registry.PutAsync(parsed!, Request.Body, User.Identity?.Name ?? "unknown", ct);
        return StatusCode(StatusCodes.Status201Created);
    }

    /// <summary>A bare 404. Maven's resolver reads a problem-details body (RFC 9457) as a transfer
    /// failure and warns, where an empty 404 is the plain "not in this repository" it expects.</summary>
    private static ContentResult Missing() => new() { StatusCode = StatusCodes.Status404NotFound };
}
