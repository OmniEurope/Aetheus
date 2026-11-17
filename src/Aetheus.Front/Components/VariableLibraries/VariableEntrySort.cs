// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.VariableLibraries;

/// <summary>The sort of a library's entries: the one the grid asks for, and where a new entry goes under it.</summary>
internal static class VariableEntrySort
{
    public static (string SortBy, bool Descending) Read(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return ("Key", false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Under the key sort the grid shows, else null: the new entry goes on top.</summary>
    public static Comparison<VariableEntryDto>? Order(string? sortBy, bool descending)
    {
        if (!string.Equals(sortBy, "Key", StringComparison.OrdinalIgnoreCase)) return null;
        var sign = descending ? -1 : 1;
        return (left, right) => sign * string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
    }
}
