// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Data;

internal static class UserQueryExtensions
{
    public static IQueryable<User> IncludeRoles(this IQueryable<User> query) => query
        .Include(user => user.UserRoles)
            .ThenInclude(userRole => userRole.Role)
        .Include(user => user.OrganizationRoles)
            .ThenInclude(assignment => assignment.Role)
        .Include(user => user.OrganizationRoles)
            .ThenInclude(assignment => assignment.Organization)
        .AsSplitQuery();

    public static Task<User?> FindByUsernameWithRolesAsync(
        this IQueryable<User> query,
        string username,
        CancellationToken ct = default)
    {
        var normalizedUsername = username.Trim().ToLower();
        return query
            .AsNoTracking()
            .IncludeRoles()
            .FirstOrDefaultAsync(user => user.Username.ToLower() == normalizedUsername, ct);
    }

    /// <summary>The account whose sign-in e-mail is <paramref name="email"/> (PLAN-003 P3-2). E-mails are
    /// stored normalized (<see cref="UserEmail.Normalize"/>), so an exact match on the normalized value is
    /// case-insensitive and uses the unique index.</summary>
    public static Task<User?> FindByEmailWithRolesAsync(
        this IQueryable<User> query,
        string? email,
        CancellationToken ct = default)
    {
        var normalizedEmail = UserEmail.Normalize(email);
        if (normalizedEmail is null) return Task.FromResult<User?>(null);
        return query
            .AsNoTracking()
            .IncludeRoles()
            .FirstOrDefaultAsync(user => user.Email == normalizedEmail, ct);
    }
}

/// <summary>The single rule for a stored sign-in e-mail: trimmed, lower-case invariant, a blank value is no
/// e-mail. Every write and every lookup goes through it, so the unique index on <c>Users.Email</c> is
/// case-insensitive in effect.</summary>
internal static class UserEmail
{
    public static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}
