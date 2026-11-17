// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Declares the <c>PORT_*</c> entries libraries already hold, idempotently, so ports written before
/// the registry followed library writes stop looking free. It only adds claims: a port another owner already
/// holds is reported and left with its holder, and no existing reservation is moved or released.</summary>
public static class LibraryPortBackfill
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var repo = services.GetRequiredService<IPortAllocationRepository>();
        var registrar = services.GetRequiredService<LibraryPortRegistrar>();

        var carried = 0;
        var contested = 0;
        foreach (var source in await repo.GetPortLibrariesAsync(ct: ct).ConfigureAwait(false))
        {
            var (done, left) = await registrar.BackfillAsync(source, ct).ConfigureAwait(false);
            carried += done;
            contested += left;
        }

        logger.LogInformation(
            "Library port backfill: {Carried} port claim(s) carried in, {Contested} left to their existing holder.",
            carried, contested);
    }
}
