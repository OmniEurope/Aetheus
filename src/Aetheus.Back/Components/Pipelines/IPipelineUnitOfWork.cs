// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Flushes the changes tracked on the request's shared <c>AppDbContext</c>. Declared once so that the
/// narrow pipeline ports needing a flush and the <see cref="IPipelineRepository"/> facade, which
/// composes them, share a single <c>SaveChangesAsync</c> member (audit AUD-07).
/// </summary>
public interface IPipelineUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct = default);
}
