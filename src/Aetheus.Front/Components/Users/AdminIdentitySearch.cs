// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

/// <summary> The one search of the Users, Organizations and Roles pages. The term lives here so it
/// survives the move from one section to another, and each section's result count is counted once
/// per term for the section links. The pages filter their grid with <see cref="Search"/> and reload
/// when the term changes. </summary>
public sealed class AdminIdentitySearch(ApiClient api)
{
    public const string UsersSection = "users";
    public const string OrganizationsSection = "organizations";
    public const string RolesSection = "roles";

    private static readonly string[] Sections = [UsersSection, OrganizationsSection, RolesSection];

    private Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>The text as typed.</summary>
    public string Term { get; private set; } = string.Empty;

    /// <summary>The term the API filters by; null when there is nothing to search.</summary>
    public string? Search => string.IsNullOrWhiteSpace(Term) ? null : Term.Trim();

    /// <summary>Raised after the term and the counts changed.</summary>
    public event Func<Task>? Changed;

    /// <summary>The number of results of a section for the current term; null when there is no term or
    /// the count could not be read.</summary>
    public int? CountFor(string section) =>
        Search is not null && _counts.TryGetValue(section, out var count) ? count : null;

    /// <summary>
    /// Sets the term, counts every section and returns the section to show: the only one holding a
    /// result when <paramref name="activeSection"/> has none, otherwise <paramref name="activeSection"/>.
    /// </summary>
    public async Task<string> SetAsync(string? term, string activeSection)
    {
        term ??= string.Empty;
        if (string.Equals(term, Term, StringComparison.Ordinal))
            return activeSection;
        Term = term;
        _counts = Search is null ? new(StringComparer.Ordinal) : await CountAsync(Search);

        if (Changed is { } changed)
        {
            foreach (var handler in changed.GetInvocationList().Cast<Func<Task>>())
                await handler();
        }
        return SectionToShow(activeSection);
    }

    internal string SectionToShow(string activeSection)
    {
        if (Search is null || CountFor(activeSection) is not 0)
            return activeSection;
        var withResults = Sections.Where(section => CountFor(section) > 0).ToList();
        return withResults.Count == 1 ? withResults[0] : activeSection;
    }

    private async Task<Dictionary<string, int>> CountAsync(string search)
    {
        var users = TryCountAsync(async () => (int?)(await api.Auth.GetUsersAsync(1, 1, search)).TotalCount);
        var organizations = TryCountAsync(async () =>
            (await api.Servers.GetOrganizationsAsync(search, 1, 1))?.TotalCount);
        var roles = TryCountAsync(async () => (int?)(await api.Auth.GetRoleDtosAsync(1, 1, search)).TotalCount);
        await Task.WhenAll(users, organizations, roles);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (await users is { } userCount) counts[UsersSection] = userCount;
        if (await organizations is { } organizationCount) counts[OrganizationsSection] = organizationCount;
        if (await roles is { } roleCount) counts[RolesSection] = roleCount;
        return counts;
    }

    // A section that cannot be counted shows no count and is never the one switched to.
    private static async Task<int?> TryCountAsync(Func<Task<int?>> count)
    {
        try
        {
            return await count();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
