// SPDX-License-Identifier: EUPL-1.2
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Components.PackageRegistry;

/// <summary>
/// Mints the package registry credential of a pipeline run (the <c>GITHUB_TOKEN</c> model): the
/// <c>publish-observability</c> step receives it as <c>AETHEUS_PACKAGE_TOKEN</c> instead of a personal
/// access token read from a vault.
/// </summary>
public interface IPackageRunTokenIssuer
{
    /// <summary>The token for <paramref name="runId"/> of <paramref name="projectId"/>, or why none is issued:
    /// the run's pipeline has no owner, or its owner is not an active administrator.</summary>
    Task<PackageRunTokenGrant> MintAsync(int runId, int projectId, CancellationToken ct = default);
}

/// <summary>Either the minted <see cref="Token"/> or the <see cref="Refusal"/> the step is refused with.</summary>
public sealed record PackageRunTokenGrant(string? Token, string? Refusal);

/// <summary>Who stands behind a run, for its registry credential: the owner of its pipeline
/// (<c>Pipeline.CreatedByUsername</c>) must be an active user holding the Admin role, the authority an
/// Admin personal access token had.</summary>
public enum PackageRunOwnerState
{
    RunNotFound,
    OwnerMissing,
    OwnerInactive,
    OwnerNotAdmin,
    Allowed
}

/// <summary>The scoped issuer: the owner check against the database, then the stateless token.</summary>
internal sealed class PackageRunTokenIssuer(PackageRunTokens tokens, IPackageRegistryRepository repository)
    : IPackageRunTokenIssuer
{
    public async Task<PackageRunTokenGrant> MintAsync(int runId, int projectId, CancellationToken ct = default)
    {
        var state = await repository.GetRunOwnerStateAsync(runId, projectId, ct).ConfigureAwait(false);
        return state switch
        {
            PackageRunOwnerState.Allowed => new(tokens.Mint(runId, projectId), null),
            PackageRunOwnerState.OwnerMissing => new(null,
                "the pipeline has no owner; an administrator must save it once to become its owner."),
            PackageRunOwnerState.OwnerInactive => new(null, "the pipeline owner's account is disabled."),
            PackageRunOwnerState.OwnerNotAdmin => new(null, "the pipeline owner does not hold the Admin role."),
            _ => new(null, "the run does not belong to a pipeline of this project.")
        };
    }
}

/// <summary>
/// The run-scoped registry credential. It is <c>aetheus-pkg-run.{runId}.{projectId}.{expiry}.{mac}</c>, the
/// MAC being an HMAC-SHA256 over the run, the project and the expiry under the
/// <see cref="TokenHashPurpose.PackageRunToken"/> key, so it cannot be forged or moved to another run or
/// project, and it carries its own expiry: nothing is stored, no migration.
/// <list type="bullet">
/// <item>Issued only when the pipeline owner is an active Admin (<see cref="PackageRunTokenIssuer"/>), and
/// accepted only while the run is <c>Running</c> in that project and its owner is still an active Admin
/// (one database check in the authentication handler, fail closed): demoting or disabling the owner revokes
/// its live tokens.</item>
/// <item>Its principal carries no role: it reads what any authenticated caller reads, and publishes only
/// the observability packages (<see cref="PackageRegistryDefaults.IsObservabilityPackage"/>), never
/// unlists, relists, deletes or administers anything.</item>
/// </list>
/// </summary>
internal sealed class PackageRunTokens(TokenHashKeys keys, TimeProvider timeProvider)
{
    public const string Prefix = "aetheus-pkg-run.";

    /// <summary>Claim carrying the run id on a principal authenticated by a run token.</summary>
    public const string RunClaim = "aetheus:package_run";

    /// <summary>Claim carrying the project id on a principal authenticated by a run token.</summary>
    public const string ProjectClaim = "aetheus:package_run_project";

    /// <summary>Upper bound of a token's life; the run-state check ends it sooner, as soon as the run ends.
    /// The publication step itself is capped at two hours by the agent.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(3);

    /// <summary>The signed token; the owner check is the issuer's, before this.</summary>
    public string Mint(int runId, int projectId)
    {
        var expiry = timeProvider.GetUtcNow().Add(Ttl).ToUnixTimeSeconds();
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{runId}.{projectId}.{expiry}.")
            + Convert.ToHexStringLower(Mac(runId, projectId, expiry));
    }

    /// <summary>The run and project of a well-formed, authentic, unexpired token; otherwise <c>null</c>. The
    /// run state and its owner are the caller's second check.</summary>
    public (int RunId, int ProjectId)? Validate(string token)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = token[Prefix.Length..].Split('.');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var runId)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var projectId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
            || parts[3].Length != 64)
            return null;

        Span<byte> presented = stackalloc byte[32];
        if (Convert.FromHexString(parts[3], presented, out _, out var written) != OperationStatus.Done
            || written != presented.Length
            || !CryptographicOperations.FixedTimeEquals(presented, Mac(runId, projectId, expiry)))
            return null;
        if (expiry <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
            return null;
        return (runId, projectId);
    }

    private byte[] Mac(int runId, int projectId, long expiry) =>
        keys.Sign(
            TokenHashPurpose.PackageRunToken,
            string.Create(CultureInfo.InvariantCulture, $"package-run-token:v1:{runId}:{projectId}:{expiry}"));

    /// <summary>Whether a presented credential is a run token (and not a personal access token).</summary>
    public static bool IsRunToken(string token) => token.StartsWith(Prefix, StringComparison.Ordinal);

    internal static string Describe(int runId) => string.Create(CultureInfo.InvariantCulture, $"pipeline-run-{runId}");
}
