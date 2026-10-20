// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Organizations;

/// <summary>Every organization column sorts both ways with the name then the id as tie-breakers; an
/// unknown column sorts on the name.</summary>
public sealed class OrganizationRepositorySortTests
{
    private static readonly Organization A = Org(1, "acme", "a-slug", "zeta", members: 3, projects: 1, day: 2);
    private static readonly Organization B = Org(2, "beta", "c-slug", "alpha", members: 1, projects: 2, day: 3);
    private static readonly Organization C = Org(3, "core", "b-slug", "alpha", members: 2, projects: 3, day: 1);

    [Theory]
    [InlineData("slug", false, new[] { 1, 3, 2 })]
    [InlineData("Slug", true, new[] { 2, 3, 1 })]
    [InlineData("description", false, new[] { 2, 3, 1 })]
    [InlineData("description", true, new[] { 1, 2, 3 })]
    [InlineData("memberCount", false, new[] { 2, 3, 1 })]
    [InlineData("memberCount", true, new[] { 1, 3, 2 })]
    [InlineData("projectCount", false, new[] { 1, 2, 3 })]
    [InlineData("projectCount", true, new[] { 3, 2, 1 })]
    [InlineData(" createdAt ", false, new[] { 3, 1, 2 })]
    [InlineData("createdAt", true, new[] { 2, 1, 3 })]
    [InlineData("name", true, new[] { 3, 2, 1 })]
    [InlineData(null, false, new[] { 1, 2, 3 })]
    [InlineData("unknown", false, new[] { 1, 2, 3 })]
    public void ApplySort_OrdersOnTheRequestedColumn(string? sortBy, bool descending, int[] expected) =>
        Assert.Equal(expected, OrganizationRepository.ApplySort(new[] { C, A, B }.AsQueryable(), sortBy, descending).Select(o => o.Id));

    private static Organization Org(int id, string name, string slug, string description, int members, int projects, int day) => new()
    {
        Id = id,
        Name = name,
        Slug = slug,
        Description = description,
        CreatedAt = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc),
        Members = Enumerable.Range(0, members).Select(_ => new OrganizationMember()).ToList(),
        Projects = Enumerable.Range(0, projects).Select(_ => new Project()).ToList(),
    };
}
