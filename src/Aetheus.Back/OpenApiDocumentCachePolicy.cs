// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.OutputCaching;

namespace Aetheus.Back;

/// <summary>
/// R-28: the OpenAPI document is static for the lifetime of a process (it only depends on the
/// controllers compiled into it), yet generating it walks every endpoint by reflection and cost
/// about 650 ms at each opening of Administration, API reference. This policy keeps the first
/// generated response for the process lifetime. The default output-cache policy refuses requests
/// carrying an Authorization header, so it cannot be used: the endpoint is admin-gated outside
/// Development, and authorization runs before the cache (the cache is mapped after
/// <c>UseAuthorization</c>), so a cached body is never served to a caller who failed the gate.
/// </summary>
internal sealed class OpenApiDocumentCachePolicy : IOutputCachePolicy
{
    internal const string Name = "openapi-document";

    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var isRead = HttpMethods.IsGet(context.HttpContext.Request.Method) || HttpMethods.IsHead(context.HttpContext.Request.Method);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = isRead;
        context.AllowCacheStorage = isRead;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = TimeSpan.FromDays(365);
        return ValueTask.CompletedTask;
    }

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        // Only a successful, cookie-free response is worth keeping for the process lifetime.
        var response = context.HttpContext.Response;
        if (response.StatusCode != StatusCodes.Status200OK || response.Headers.ContainsKey("Set-Cookie"))
            context.AllowCacheStorage = false;
        return ValueTask.CompletedTask;
    }
}
