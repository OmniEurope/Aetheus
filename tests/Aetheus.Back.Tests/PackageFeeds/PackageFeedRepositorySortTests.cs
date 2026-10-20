// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageFeeds;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.PackageFeeds;

/// <summary>Every package feed column sorts both ways with stable tie-breakers; an unknown column sorts
/// on the name.</summary>
public sealed class PackageFeedRepositorySortTests
{
    private static readonly PackageFeed A = Feed(1, "alpha", (PackageFeedType)1, "https://c.example", packages: 2, day: 3);
    private static readonly PackageFeed B = Feed(2, "beta", (PackageFeedType)0, "https://a.example", packages: 3, day: 1);
    private static readonly PackageFeed C = Feed(3, "gamma", (PackageFeedType)1, "https://b.example", packages: 1, day: 2);

    [Theory]
    [InlineData("feedType", false, new[] { 2, 1, 3 })]
    [InlineData("FeedType", true, new[] { 1, 3, 2 })]
    [InlineData("upstreamUrl", false, new[] { 2, 3, 1 })]
    [InlineData("upstreamUrl", true, new[] { 1, 3, 2 })]
    [InlineData("packageCount", false, new[] { 3, 1, 2 })]
    [InlineData("packageCount", true, new[] { 2, 1, 3 })]
    [InlineData(" createdAt ", false, new[] { 2, 3, 1 })]
    [InlineData("createdAt", true, new[] { 1, 3, 2 })]
    [InlineData("name", true, new[] { 3, 2, 1 })]
    [InlineData(null, false, new[] { 1, 2, 3 })]
    public void ApplySort_OrdersOnTheRequestedColumn(string? sortBy, bool descending, int[] expected) =>
        Assert.Equal(expected, PackageFeedRepository.ApplySort(new[] { C, A, B }.AsQueryable(), sortBy, descending).Select(f => f.Id));

    private static PackageFeed Feed(int id, string name, PackageFeedType type, string upstream, int packages, int day) => new()
    {
        Id = id,
        Name = name,
        FeedType = type,
        UpstreamUrl = upstream,
        CreatedAt = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc),
        Packages = Enumerable.Range(0, packages).Select(_ => new PackageEntry()).ToList(),
    };
}
