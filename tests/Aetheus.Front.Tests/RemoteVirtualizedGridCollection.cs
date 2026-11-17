// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Tests;

/// <summary>
/// <see cref="AetheusGrid.RemoteVirtualization"/> is one static for the whole process, read by every grid at each render, and the tests
/// run in parallel. A test that turns it on is therefore seen by every grid another test renders meanwhile: a page that began paged
/// became virtualized between its render and its click and read its list one more time (<c>PackageFeedsAdminRowChangesTests</c>
/// saw 3 reads for 2, measured by flipping the flag between the two). Conversely, any other test's <c>RegisterServices</c> turns it
/// off again, so the test that wants the virtualized grid could be running on the paged one. A test that changes the static
/// belongs in this collection, which xUnit runs alone, after every parallel collection has finished.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RemoteVirtualizedGridCollection
{
    public const string Name = "RemoteVirtualizedGrid";
}
