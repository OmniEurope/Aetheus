// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git.Events;

namespace Aetheus.Back.Components.Git;

public interface IGitSmartHttpService
{
    Task<(string ContentType, byte[] Body)?> GetInfoRefsAsync(int projectId, string slug, string service, CancellationToken ct = default);
    Task<GitSmartHttpResponse?> ExecuteServiceAsync(int projectId, string slug, string service, Stream requestBody, CancellationToken ct = default);
    Task MarkPushedAsync(int projectId, string slug, IReadOnlyList<GitRefUpdate> updatedRefs, CancellationToken ct = default);
    Task<bool> ValidateBasicAuthAsync(string username, string password, CancellationToken ct = default);

    /// <summary>True when the pipeline run is still in a non-terminal (Running) state. Run clone tokens
    /// stay HMAC-valid until expiry, so the auth handler gates them on the run still being active: a
    /// token leaked after the run finished can no longer clone.</summary>
    Task<bool> IsRunActiveAsync(int runId, CancellationToken ct = default);
}

public sealed record GitSmartHttpResponse(string ContentType, Stream Body, IReadOnlyList<GitRefUpdate> UpdatedRefs);
