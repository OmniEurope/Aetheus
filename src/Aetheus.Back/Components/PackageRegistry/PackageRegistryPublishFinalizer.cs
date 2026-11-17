// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PackageRegistry;

internal static class PackageRegistryPublishFinalizer
{
    public static async Task SaveAndNotifyAsync(
        IPackageRegistryRepository repository,
        IPackageRegistryStorage storage,
        IAuditService audit,
        IAdminChangeNotifier notifier,
        StoredPackagePayload payload,
        RegistryPackage package,
        string auditEntity,
        string auditDetails,
        CancellationToken ct)
    {
        repository.MarkPublished(package);
        try
        {
            await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (payload.CreatedNew)
                await storage.DeleteAsync(payload.RelativePath, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await audit.LogAsync(
            "Published", auditEntity, package.Id, auditDetails, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(
            AdminEntities.PackageRegistry, package.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }
}
