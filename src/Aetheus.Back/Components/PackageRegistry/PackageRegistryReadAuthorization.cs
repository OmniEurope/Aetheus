// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Authorization;

namespace Aetheus.Back.Components.PackageRegistry;

/// <summary>
/// The policy every package registry controller carries at class level (R-58). An authenticated
/// caller (a valid personal access token) passes as before. A caller without credentials passes only
/// on a read action marked <see cref="AnonymousPackageDownloadAttribute"/>, and only for a package an
/// administrator opened to anonymous download. Everything else, the NuGet search, the npm ping and
/// search, every publish, and any package not opened, is challenged with a 401 exactly as before.
/// </summary>
internal static class PackageRegistryPolicies
{
    public const string Read = "PackageRegistryRead";

    public static IServiceCollection AddPackageRegistryReadPolicy(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, PackageRegistryReadHandler>();
        services.Configure<AuthorizationOptions>(options => options.AddPolicy(Read, policy => policy
            .AddAuthenticationSchemes(PackageRegistryAuthenticationHandler.SchemeName)
            .AddRequirements(new PackageRegistryReadRequirement())));
        return services;
    }
}

/// <summary>Marks a read action whose package may be served without credentials when the package
/// allows it. The kind says how the package name is read from the route.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AnonymousPackageDownloadAttribute(PackageRegistryKind kind) : Attribute
{
    public PackageRegistryKind Kind { get; } = kind;
}

internal sealed class PackageRegistryReadRequirement : IAuthorizationRequirement;

internal sealed class PackageRegistryReadHandler(IPackageRegistryRepository repository)
    : AuthorizationHandler<PackageRegistryReadRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PackageRegistryReadRequirement requirement)
    {
        if (context.User.Identities.Any(identity => identity.IsAuthenticated))
        {
            context.Succeed(requirement);
            return;
        }

        if (context.Resource is not HttpContext http
            || !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)))
            return;
        var marker = http.GetEndpoint()?.Metadata.GetMetadata<AnonymousPackageDownloadAttribute>();
        if (marker is null)
            return;

        var normalizedName = ResolveNormalizedName(marker.Kind, http.Request.RouteValues);
        if (normalizedName is not null
            && await repository.IsAnonymousDownloadAllowedAsync(marker.Kind, normalizedName, http.RequestAborted)
                .ConfigureAwait(false))
            context.Succeed(requirement);
    }

    /// <summary>The stored (normalized) package name the request reads, resolved the way each
    /// protocol's service resolves it; null when the route names no valid package.</summary>
    internal static string? ResolveNormalizedName(PackageRegistryKind kind, RouteValueDictionary routeValues)
    {
        switch (kind)
        {
            case PackageRegistryKind.NuGet:
                var id = routeValues["id"] as string;
                return string.IsNullOrWhiteSpace(id) || id.Length > MavenRepositoryPath.MaxNameLength
                    ? null
                    : id.ToLowerInvariant();
            case PackageRegistryKind.Npm:
                return routeValues["path"] is string npmPath ? NpmRegistryPath.PackageName(npmPath) : null;
            case PackageRegistryKind.Maven:
                return routeValues["path"] is string mavenPath
                    && MavenRepositoryPath.TryParse(mavenPath, out var parsed, out _)
                    ? parsed!.Coordinates
                    : null;
            default:
                return null;
        }
    }
}
