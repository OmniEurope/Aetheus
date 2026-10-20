// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PortRegistry;

public sealed class PortRegistryService(
    IPortRegistryRepository repo,
    TimeProvider timeProvider,
    ILogger<PortRegistryService> logger) : IPortRegistryService
{
    public async Task<List<PortReservationDto>> GetServerReservationsAsync(
        int serverId, CancellationToken ct = default)
    {
        var reservations = await repo.GetReservationsAsync(serverId, ct).ConfigureAwait(false);
        return [.. reservations.Select(ToDto)];
    }

    public async Task<PortCheckResultDto> CheckPortsAsync(
        int serverId, IReadOnlyCollection<int> ports, int? contextProjectId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var serverName = await repo.FindServerNameAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} was not found.");

        var observation = await repo.FindObservationStateAsync(serverId, ct).ConfigureAwait(false);
        var lastScanAt = observation?.LastScanAt;

        var wanted = Normalize(ports);
        var claims = await repo.GetReservationsForPortsAsync(serverId, wanted, ct).ConfigureAwait(false);

        var entries = new List<PortCheckEntryDto>(wanted.Count);
        foreach (var port in wanted)
        {
            // The two sources are read separately and crossed, never merged: "nobody claims it" and
            // "nothing is listening on it" are different facts, and a port can fail on either one.
            // TCP throughout: a deployment binds TCP, so a UDP listener on the same number answers a
            // different question and must not make the port read as unusable.
            var claim = claims.Find(reservation =>
                reservation.Port == port
                && reservation.Source != PortReservationSource.Observed
                && NormalizeProtocol(reservation.Protocol) == PortRegistryLimits.TcpProtocol);
            var sighting = claims.Find(reservation =>
                reservation.Port == port
                && reservation.Source == PortReservationSource.Observed
                && NormalizeProtocol(reservation.Protocol) == PortRegistryLimits.TcpProtocol);

            entries.Add(new PortCheckEntryDto
            {
                Port = port,
                IsFree = claim is null,
                OwnerLabel = claim?.OwnerLabel,
                OwnerProjectId = claim?.ProjectId,
                Source = claim?.Source,
                DeclaredAt = claim?.DeclaredAt,
                Observation = ObservationFor(sighting, claim, lastScanAt),
                ObservedHolder = sighting?.OwnerLabel,
                ContextProjectId = contextProjectId
            });
        }

        return new PortCheckResultDto
        {
            ServerId = serverId,
            ServerName = serverName,
            Entries = entries,
            LastScanAt = lastScanAt,
            ObservationAvailable = observation?.ObservationAvailable == true
        };
    }

    /// <summary>
    /// Crosses one port's two sources. Only a sighting from the LAST scan counts as listening: an
    /// observation row is deleted when its port stops listening, and a claim keeps the stamp of when it
    /// was last seen, so both are compared against the server's own scan time rather than merely
    /// existing. No scan is launched here; the check reports what is known and says when it is stale.
    /// </summary>
    private static PortObservationState ObservationFor(
        ServerPortReservation? sighting, ServerPortReservation? claim, DateTime? lastScanAt)
    {
        if (lastScanAt is not { } scan) return PortObservationState.NeverScanned;
        if (sighting is not null) return PortObservationState.Listening;
        return claim?.ObservedAt >= scan ? PortObservationState.Listening : PortObservationState.NotListening;
    }

    public async Task<List<PortConflictDto>> FindConflictsAsync(
        int serverId, IReadOnlyCollection<int> ports, string ownerKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var wanted = Normalize(ports);
        if (wanted.Count == 0) return [];

        var claims = await repo.GetReservationsForPortsAsync(serverId, wanted, ct).ConfigureAwait(false);

        // A port this owner already holds is not a conflict, whatever else was recorded about it. The
        // grouping matters since observations exist: a project redeploying on its own port also has an
        // Observed row for it (owner key "observed"), and comparing row by row would make every
        // redeployment collide with its own running service.
        // TCP only: the caller is about to bind TCP, so a UDP listener on the same number is not in its
        // way and must not refuse the deployment.
        var conflicts = new List<PortConflictDto>();
        foreach (var group in claims
                     .Where(reservation =>
                         NormalizeProtocol(reservation.Protocol) == PortRegistryLimits.TcpProtocol)
                     .GroupBy(reservation => reservation.Port)
                     .OrderBy(group => group.Key))
        {
            if (group.Any(reservation =>
                    string.Equals(reservation.OwnerKey, ownerKey, StringComparison.Ordinal)))
                continue;

            // Name a declared or manual holder when there is one: "portfolio-prod-front" tells the
            // operator who to talk to, where the observed "docker-proxy" only says something is there.
            var blocking = group.FirstOrDefault(reservation => reservation.Source != PortReservationSource.Observed)
                ?? group.First();
            conflicts.Add(new PortConflictDto
            {
                Port = blocking.Port,
                OwnerLabel = blocking.OwnerLabel,
                Source = blocking.Source,
                DeclaredAt = blocking.DeclaredAt
            });
        }

        return conflicts;
    }

    public async Task DeclareAsync(
        int serverId, IReadOnlyCollection<int> ports, string ownerKey, string ownerLabel,
        int? projectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        var wanted = Normalize(ports);
        if (wanted.Count == 0) return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var label = Truncate(ownerLabel, PortRegistryLimits.MaxOwnerLabelLength);
        var key = Truncate(ownerKey, PortRegistryLimits.MaxOwnerKeyLength);

        var existing = await repo
            .GetTrackedReservationsForPortsAsync(serverId, wanted, PortReservationSource.Declared, ct)
            .ConfigureAwait(false);

        foreach (var port in wanted)
        {
            var row = existing.Find(reservation => reservation.Port == port);
            if (row is null)
            {
                repo.TrackReservation(new ServerPortReservation
                {
                    ServerId = serverId,
                    Port = port,
                    OwnerKey = key,
                    OwnerLabel = label,
                    ProjectId = projectId,
                    Source = PortReservationSource.Declared,
                    DeclaredAt = now,
                    UpdatedAt = now
                });
                continue;
            }

            if (!string.Equals(row.OwnerKey, key, StringComparison.Ordinal))
            {
                // Rejects a conflicting port claim even if preflight was bypassed or lost a race,
                // preserving the registry's ownership guarantee.
                throw new ConflictException(
                    $"Port {port} on this server is declared by '{row.OwnerLabel}', so '{label}' "
                    + "cannot take it.");
            }

            row.OwnerLabel = label;
            row.ProjectId = projectId;
            row.UpdatedAt = now;
        }

        await ReleaseAbandonedAsync(serverId, key, wanted, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PortReservationDto> AddManualReservationAsync(
        int serverId, CreatePortReservationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await repo.ServerExistsAsync(serverId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Server {serverId} was not found.");

        // Only a declared or manual claim blocks: turning an observed listener into a reservation is the
        // whole point of the "Reserve" action, so its own sighting must not refuse it.
        var claims = await repo
            .GetReservationsForPortsAsync(serverId, [request.Port], ct)
            .ConfigureAwait(false);
        var ownerKey = ManualOwnerKey(request.OwnerLabel);
        var holder = claims.Find(reservation =>
            reservation.Source != PortReservationSource.Observed
            && !string.Equals(reservation.OwnerKey, ownerKey, StringComparison.Ordinal));
        if (holder is not null)
            throw new ConflictException(
                $"Port {request.Port} is already registered to '{holder.OwnerLabel}' on this server.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var reservation = new ServerPortReservation
        {
            ServerId = serverId,
            Port = request.Port,
            OwnerKey = ManualOwnerKey(request.OwnerLabel),
            OwnerLabel = Truncate(request.OwnerLabel, PortRegistryLimits.MaxOwnerLabelLength),
            ProjectId = request.ProjectId,
            Source = PortReservationSource.Manual,
            DeclaredAt = now,
            UpdatedAt = now
        };
        repo.TrackReservation(reservation);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDto(reservation);
    }

    public async Task<bool> ReleaseAsync(int serverId, int reservationId, CancellationToken ct = default)
    {
        var reservation = await repo.FindReservationAsync(reservationId, ct).ConfigureAwait(false);
        if (reservation is null || reservation.ServerId != serverId) return false;
        repo.RemoveReservation(reservation);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Recording what a scan saw is a responsibility of its own, kept out of the claim
    /// registry so neither can quietly start correcting the other.</summary>
    private readonly PortObservationWriter _observations = new(repo, timeProvider, logger);

    public Task ReplaceObservedAsync(
        int serverId, IReadOnlyCollection<ObservedPortDto> ports, DateTime observedAt,
        CancellationToken ct = default) =>
        _observations.ReplaceAsync(serverId, ports, observedAt, ct);

    public async Task<bool> ReleaseOwnedAsync(
        int serverId, int port, string ownerKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);

        // Declared and Manual only: an observation is what the host shows, and deleting it would just
        // make it come back on the next scan while pretending the port was freed.
        var released = false;
        foreach (var source in new[] { PortReservationSource.Declared, PortReservationSource.Manual })
        {
            var rows = await repo
                .GetTrackedReservationsForPortsAsync(serverId, [port], source, ct)
                .ConfigureAwait(false);
            foreach (var row in rows.Where(row =>
                         string.Equals(row.OwnerKey, ownerKey, StringComparison.Ordinal)))
            {
                repo.RemoveReservation(row);
                released = true;
            }
        }

        if (released) await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return released;
    }

    public async Task SyncOwnedPortAsync(
        int? serverId, int? port, string ownerKey, string ownerLabel, int? projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);

        var key = Truncate(ownerKey, PortRegistryLimits.MaxOwnerKeyLength);
        var wanted = serverId is { } server && port is > 0 and <= 65535 ? (server, port.Value) : ((int, int)?)null;

        // Released first, unconditionally: this owner states one port, so a previous one on another
        // server (or none at all) must not survive as a claim nothing backs.
        var existing = await repo.GetTrackedManualReservationsForOwnerAsync(key, ct).ConfigureAwait(false);
        var keep = existing.Find(row =>
            wanted is { } target && row.ServerId == target.Item1 && row.Port == target.Item2);
        foreach (var stale in existing.Where(row => !ReferenceEquals(row, keep)))
            repo.RemoveReservation(stale);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var label = Truncate(ownerLabel, PortRegistryLimits.MaxOwnerLabelLength);
        if (wanted is not { } claim)
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        if (keep is not null)
        {
            keep.OwnerLabel = label;
            keep.ProjectId = projectId;
            keep.UpdatedAt = now;
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        // Refused rather than skipped: a descriptor left claiming a port the registry says belongs to
        // somebody else is precisely the invisible collision this feature exists to end.
        var conflicts = await FindConflictsAsync(claim.Item1, [claim.Item2], key, ct).ConfigureAwait(false);
        var blocking = conflicts.Find(conflict => conflict.Source != PortReservationSource.Observed);
        if (blocking is not null)
            throw new ConflictException(
                $"Port {claim.Item2} on this server is already registered to '{blocking.OwnerLabel}'.");

        repo.TrackReservation(new ServerPortReservation
        {
            ServerId = claim.Item1,
            Port = claim.Item2,
            OwnerKey = key,
            OwnerLabel = label,
            ProjectId = projectId,
            Source = PortReservationSource.Manual,
            DeclaredAt = now,
            UpdatedAt = now
        });
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The identity a server app's port is recorded under.</summary>
    internal static string ServerAppOwnerKey(int serverAppId) => $"server-app:{serverAppId}";

    /// <summary>The identity a project server's port is recorded under.</summary>
    internal static string ProjectServerOwnerKey(int projectServerId) => $"project-server:{projectServerId}";

    public async Task<int> ReleaseProjectPortsAsync(int projectId, CancellationToken ct = default)
    {
        // Observations are left alone: they describe what the host shows, and a deleted project does
        // not stop its container from listening.
        var rows = await repo
            .GetTrackedReservationsForProjectAsync(ProjectOwnerKey(projectId), ct)
            .ConfigureAwait(false);
        if (rows.Count == 0) return 0;

        foreach (var row in rows) repo.RemoveReservation(row);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Released {Count} port reservation(s) held by deleted project {ProjectId}.", rows.Count, projectId);
        return rows.Count;
    }

    public async Task<PortRangeDto> GetPortRangeAsync(int serverId, CancellationToken ct = default)
    {
        var range = await repo.FindPortRangeAsync(serverId, ct).ConfigureAwait(false);
        return range is null
            ? new PortRangeDto
            {
                From = PortRegistryLimits.DefaultRangeFrom,
                To = PortRegistryLimits.DefaultRangeTo,
                IsExplicit = false
            }
            : new PortRangeDto { From = range.From, To = range.To, IsExplicit = true };
    }

    public async Task<PortRangeDto> SetPortRangeAsync(
        int serverId, PortRangeDto range, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!await repo.ServerExistsAsync(serverId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Server {serverId} was not found.");

        if (range.From < PortRegistryLimits.MinAllocatablePort || range.To > 65535)
            throw new BadRequestException(
                $"A port window must stay within {PortRegistryLimits.MinAllocatablePort}-65535; "
                + "below that a port needs privileges to bind.");
        if (range.From > range.To)
            throw new BadRequestException(
                $"The window start ({range.From}) is after its end ({range.To}).");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await repo.FindPortRangeAsync(serverId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            repo.TrackPortRange(new ServerPortRange
            {
                ServerId = serverId,
                From = range.From,
                To = range.To,
                UpdatedAt = now
            });
        }
        else
        {
            existing.From = range.From;
            existing.To = range.To;
            existing.UpdatedAt = now;
        }

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PortRangeDto { From = range.From, To = range.To, IsExplicit = true };
    }

    public async Task<List<int>> AllocateAsync(
        int serverId, int count, string ownerKey, string ownerLabel, int? projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        if (count is < 1 or > PortRegistryLimits.MaxPortsPerAllocation)
            throw new BadRequestException(
                $"Ask for between 1 and {PortRegistryLimits.MaxPortsPerAllocation} ports.");
        if (!await repo.ServerExistsAsync(serverId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Server {serverId} was not found.");

        var window = await GetPortRangeAsync(serverId, ct).ConfigureAwait(false);
        var taken = (await repo.GetTakenPortsAsync(serverId, ct).ConfigureAwait(false)).ToHashSet();

        var free = new List<int>(count);
        for (var port = window.From; port <= window.To && free.Count < count; port++)
        {
            if (IsReservedByConvention(port) || taken.Contains(port)) continue;
            free.Add(port);
        }

        if (free.Count < count)
            throw new ConflictException(
                $"The allocation window {window.From}-{window.To} has only {free.Count} free port(s) "
                + $"left, and {count} were requested.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var key = Truncate(ownerKey, PortRegistryLimits.MaxOwnerKeyLength);
        var label = Truncate(ownerLabel, PortRegistryLimits.MaxOwnerLabelLength);
        foreach (var port in free)
        {
            repo.TrackReservation(new ServerPortReservation
            {
                ServerId = serverId,
                Port = port,
                OwnerKey = key,
                OwnerLabel = label,
                ProjectId = projectId,
                Source = PortReservationSource.Declared,
                DeclaredAt = now,
                UpdatedAt = now
            });
        }

        // No ReleaseAbandonedAsync here, unlike DeclareAsync: an allocation ADDS to what the owner
        // already holds. Reusing the declaration path would silently release the ports of the very
        // deployment that is asking for more.
        try
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // Losing the race against a concurrent allocation is the unique index doing its job, not a
            // server fault: reported as a conflict the caller can act on rather than as a 500.
            logger.LogInformation(
                ex, "Allocation on server {ServerId} lost the race for ports {Ports}.",
                serverId, string.Join(", ", free));
            throw new ConflictException(
                "Another allocation took these ports first. Ask again to be handed the next free ones.");
        }

        return free;
    }

    /// <summary>
    /// Ports the fleet never hands out, whatever the window says. The single floor covers the whole
    /// convention: 80 and 443 belong to the reverse proxy on every host and are already below it, so
    /// naming them again would only add a check that can never fire.
    /// </summary>
    private static bool IsReservedByConvention(int port) =>
        port < PortRegistryLimits.MinAllocatablePort;

    /// <summary>
    /// Drops the declared ports this owner held on the server and no longer asks for. Without it a
    /// project that moves from 10031 to 10041 would keep blocking 10031 for everybody else forever -
    /// the main way a declarative registry drifts from reality.
    /// </summary>
    private async Task ReleaseAbandonedAsync(
        int serverId, string ownerKey, IReadOnlyCollection<int> keep, CancellationToken ct)
    {
        var owned = await repo
            .GetTrackedReservationsForOwnerAsync(serverId, ownerKey, PortReservationSource.Declared, ct)
            .ConfigureAwait(false);
        foreach (var reservation in owned.Where(reservation => !keep.Contains(reservation.Port)))
            repo.RemoveReservation(reservation);
    }

    /// <summary>
    /// The identity a project's deployments use. An allocation made FOR a project must be recorded
    /// under it, otherwise the project's own pipeline would later be refused its own ports by the
    /// conflict gate - the allocation would block exactly the deployment it was made for.
    /// </summary>
    internal static string ProjectOwnerKey(int projectId) => $"project:{projectId}";

    /// <summary>Reading a stored protocol goes through the writer that produced it, so both sides
    /// agree on what an unreadable value means.</summary>
    private static string NormalizeProtocol(string? protocol) =>
        PortObservationWriter.NormalizeProtocol(protocol);

    internal static string ManualOwnerKey(string ownerLabel) =>
        "manual:" + ownerLabel.Trim().ToLowerInvariant();

    private static List<int> Normalize(IReadOnlyCollection<int> ports) =>
    [
        .. ports
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .Order()
            .Take(PortRegistryLimits.MaxPortsPerCheck)
    ];

    private static string Truncate(string value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static PortReservationDto ToDto(ServerPortReservation reservation) => new()
    {
        Id = reservation.Id,
        ServerId = reservation.ServerId,
        Port = reservation.Port,
        Protocol = NormalizeProtocol(reservation.Protocol),
        ProjectId = reservation.ProjectId,
        ProjectName = reservation.Project?.Name,
        OwnerLabel = reservation.OwnerLabel,
        Source = reservation.Source,
        DeclaredAt = reservation.DeclaredAt,
        UpdatedAt = reservation.UpdatedAt,
        ObservedAt = reservation.ObservedAt
    };
}
