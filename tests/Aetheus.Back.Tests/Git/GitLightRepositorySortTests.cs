// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Git;

/// <summary>Every repository column of the accessible list sorts both ways; an unknown column sorts on
/// the name.</summary>
public sealed class GitLightRepositorySortTests
{
    private static readonly GitInternalRepo A = Repo(1, "api", "zeta", "beta", "main", day: 2, push: 3);
    private static readonly GitInternalRepo B = Repo(2, "back", "alpha", "alpha", "develop", day: 3, push: 1);
    private static readonly GitInternalRepo C = Repo(3, "cli", "alpha", "gamma", "release", day: 1, push: 2);

    [Theory]
    [InlineData("ProjectName", false, new[] { 2, 3, 1 })]
    [InlineData("ProjectName", true, new[] { 1, 2, 3 })]
    [InlineData("Description", false, new[] { 2, 1, 3 })]
    [InlineData("Description", true, new[] { 3, 1, 2 })]
    [InlineData("DefaultBranch", false, new[] { 2, 1, 3 })]
    [InlineData("DefaultBranch", true, new[] { 3, 1, 2 })]
    [InlineData("CreatedAt", false, new[] { 3, 1, 2 })]
    [InlineData("CreatedAt", true, new[] { 2, 1, 3 })]
    [InlineData("LastPushAt", false, new[] { 2, 3, 1 })]
    [InlineData("LastPushAt", true, new[] { 1, 3, 2 })]
    [InlineData("Name", true, new[] { 3, 2, 1 })]
    [InlineData("Name", false, new[] { 1, 2, 3 })]
    [InlineData(null, true, new[] { 1, 2, 3 })]
    public void OrderAccessible_OrdersOnTheRequestedColumn(string? sortBy, bool descending, int[] expected) =>
        Assert.Equal(expected, GitLightRepository.OrderAccessible(new[] { C, A, B }.AsQueryable(), sortBy, descending).Select(r => r.Id));

    private static GitInternalRepo Repo(int id, string name, string project, string description, string branch, int day, int push) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        DefaultBranch = branch,
        CreatedAt = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc),
        LastPushAt = new DateTime(2026, 10, push, 0, 0, 0, DateTimeKind.Utc),
        Project = new Project { Name = project },
    };
}
